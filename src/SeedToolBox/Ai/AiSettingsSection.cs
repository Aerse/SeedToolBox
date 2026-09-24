using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.Ai;

/// <summary>The AI 助手 section of the settings page: installing pi, download sources, keys and the model.</summary>
static class AiSettingsSection
{
    static readonly string[] ThinkingLevels = { "off", "minimal", "low", "medium", "high" };

    public static FrameworkElement Create(AiService service)
    {
        var s = service.Settings;
        var panel = new StackPanel();
        var body = new StackPanel();

        var enabled = new CheckBox { Content = "开启 AI 助手（基于 pi，需要自己的模型 API Key 或订阅）", IsChecked = s.Enabled, Margin = new Thickness(0, 0, 0, 8) };
        enabled.Click += (_, _) =>
        {
            s.Enabled = enabled.IsChecked == true;
            service.Save();
            body.IsEnabled = s.Enabled;
        };
        panel.Children.Add(enabled);
        panel.Children.Add(Spaced(Hint("选中文字按快捷键，文字会带进对话框；截图时可以直接问 AI；启动器里输入 ai 加问题。快捷键在上方「快捷键」中设置。")));
        body.IsEnabled = s.Enabled;
        panel.Children.Add(body);

        // —— Runtime
        var state = Ui.Status();
        void UpdateState()
        {
            var install = PiRuntime.Find(s);
            Ui.SetStatus(state, install == null
                ? "未安装 pi"
                : $"pi {install.Version}（{(install.Builtin ? "内置" : "本机")}）", install == null);
        }
        var source = new ComboBox { Width = 200 };
        source.Items.Add("内置（程序自己下载管理）");
        source.Items.Add("本机已安装的 pi");
        source.SelectedIndex = s.Source == "system" ? 1 : 0;
        source.SelectionChanged += (_, _) => { s.Source = source.SelectedIndex == 1 ? "system" : "builtin"; service.Save(); UpdateState(); };
        body.Children.Add(Spaced(Ui.Row(Label("pi 来源"), source, Spacer(), state)));

        var mirror = new ComboBox { Width = 200 };
        foreach (var m in PiMirrors.All) mirror.Items.Add(m.Name);
        mirror.Items.Add("自定义");
        int mirrorIndex = Array.FindIndex(PiMirrors.All, m => m.Key == s.Mirror);
        mirror.SelectedIndex = mirrorIndex >= 0 ? mirrorIndex : PiMirrors.All.Length;
        var custom = new StackPanel();
        var nodeMirror = Ui.Field(360);
        nodeMirror.Text = s.CustomNodeMirror;
        nodeMirror.LostKeyboardFocus += (_, _) => { s.CustomNodeMirror = nodeMirror.Text.Trim(); service.Save(); };
        var registry = Ui.Field(360);
        registry.Text = s.CustomRegistry;
        registry.LostKeyboardFocus += (_, _) => { s.CustomRegistry = registry.Text.Trim(); service.Save(); };
        custom.Children.Add(Spaced(Ui.Row(Label("Node 镜像"), nodeMirror)));
        custom.Children.Add(Spaced(Ui.Row(Label("npm 源"), registry)));
        void UpdateCustom() => custom.Visibility = mirror.SelectedIndex == PiMirrors.All.Length ? Visibility.Visible : Visibility.Collapsed;
        mirror.SelectionChanged += (_, _) =>
        {
            s.Mirror = mirror.SelectedIndex < PiMirrors.All.Length ? PiMirrors.All[mirror.SelectedIndex].Key : "custom";
            service.Save();
            UpdateCustom();
        };
        UpdateCustom();
        var fallback = new CheckBox { Content = "失败时自动换其他源", IsChecked = s.FallbackMirrors, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        fallback.Click += (_, _) => { s.FallbackMirrors = fallback.IsChecked == true; service.Save(); };
        body.Children.Add(Spaced(Ui.Row(Label("下载源"), mirror, fallback)));
        body.Children.Add(custom);

        var progress = new ProgressBar { Height = 6, Width = 360, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        var log = Ui.Area(wrap: true);
        log.IsReadOnly = true;
        log.Height = 140;
        log.Visibility = Visibility.Collapsed;
        log.Margin = new Thickness(0, 0, 0, 8);
        CancellationTokenSource? running = null;
        Button installButton = null!;
        installButton = Ui.Button(PiRuntime.Builtin() == null ? "下载安装 pi" : "更新 pi", async () =>
        {
            if (running != null) { running.Cancel(); return; }
            running = new CancellationTokenSource();
            installButton.Content = "取消";
            log.Clear();
            log.Visibility = progress.Visibility = Visibility.Visible;
            var installer = new PiInstaller(s,
                new Progress<string>(line => { log.AppendText(line + "\n"); log.ScrollToEnd(); }),
                new Progress<double?>(p => { progress.IsIndeterminate = p == null; if (p != null) progress.Value = p.Value * 100; }));
            try
            {
                await installer.RunAsync(running.Token);
                if (s.Source == "system" && PiRuntime.System() == null) { s.Source = "builtin"; service.Save(); }
            }
            catch (OperationCanceledException) { log.AppendText("已取消\n"); }
            catch (Exception ex) { log.AppendText(ex.Message + "\n"); }
            finally
            {
                running = null;
                progress.Visibility = Visibility.Collapsed;
                installButton.Content = PiRuntime.Builtin() == null ? "下载安装 pi" : "更新 pi";
                UpdateState();
            }
        }, accent: true);
        body.Children.Add(Spaced(Ui.Row(installButton, Hint($"  约 40 MB，装在 {PiRuntime.Root}，不改系统 PATH"))));
        body.Children.Add(progress);
        body.Children.Add(log);
        UpdateState();

        // —— Model service: pick one, paste the key, save; the model is chosen automatically
        var builtins = PiConfig.Providers;
        var templates = PiConfig.CustomTemplates;
        var provider = new ComboBox { Width = 260 };
        foreach (var p in builtins) provider.Items.Add(p.Name);
        foreach (var t in templates) provider.Items.Add(t.Name);
        provider.Items.Add("其他（兼容 OpenAI 接口）");
        var key = new PasswordBox { Width = 360, Style = DialogWindow.PasswordBoxStyle };
        var baseUrl = Ui.Field(360);
        var modelName = Ui.Field(360);
        var urlRow = Spaced(Ui.Row(Label("接口地址"), baseUrl));
        var modelRow = Spaced(Ui.Row(Label("模型名"), modelName));
        var saveStatus = Ui.Status();
        var keyHint = Hint("");
        bool IsBuiltin() => provider.SelectedIndex < builtins.Length;
        int TemplateIndex() => provider.SelectedIndex - builtins.Length;
        provider.SelectionChanged += (_, _) =>
        {
            var custom = !IsBuiltin();
            urlRow.Visibility = modelRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            if (!custom) return;
            int t = TemplateIndex();
            baseUrl.Text = t < templates.Length ? templates[t].BaseUrl : "";
            var example = t < templates.Length ? templates[t].Models : "";
            // Some templates only carry a hint instead of a model name
            modelName.Text = example.Contains("填写") ? "" : example.Split(',')[0].Trim();
            modelName.ToolTip = example.Length > 0 ? "例如：" + example : "服务商提供的模型名";
        };
        // Show what is configured now: the provider of the current model, with its saved address and models
        var currentProvider = s.Model.Contains("/") ? s.Model.Substring(0, s.Model.IndexOf('/')) : "";
        int builtinIndex = Array.FindIndex(builtins, p => p.Key == currentProvider);
        int templateIndex = Array.FindIndex(templates, t => t.Id == currentProvider);
        var saved = currentProvider.Length > 0 && builtinIndex < 0 ? PiConfig.CustomProvider(s, currentProvider) : null;
        provider.SelectedIndex = builtinIndex >= 0 ? builtinIndex
            : saved == null ? 0
            : builtins.Length + (templateIndex >= 0 ? templateIndex : templates.Length);
        if (saved is { } c)
        {
            baseUrl.Text = c.BaseUrl;
            modelName.Text = string.Join(", ", c.Models);
        }
        void UpdateKeyHint()
        {
            string id = IsBuiltin() ? builtins[provider.SelectedIndex].Key : TemplateIndex() < templates.Length ? templates[TemplateIndex()].Id : "custom";
            key.Password = IsBuiltin() ? PiConfig.ApiKey(s, id) : PiConfig.CustomProvider(s, id)?.Key ?? "";
            keyHint.Text = !IsBuiltin() || key.Password.Length > 0 || !PiConfig.Authorized(s).Contains(id) ? "" : "  已通过 /login 登录";
        }
        provider.SelectionChanged += (_, _) => UpdateKeyHint();
        UpdateKeyHint();

        bool loading = false;
        var model = new ComboBox { Width = 360 };
        void ShowModels(System.Collections.Generic.IEnumerable<string> ids)
        {
            loading = true;
            model.Items.Clear();
            foreach (var id in ids) model.Items.Add(id);
            if (s.Model.Length > 0 && !model.Items.Contains(s.Model)) model.Items.Insert(0, s.Model);
            model.SelectedItem = s.Model.Length > 0 ? s.Model : null;
            loading = false;
        }
        ShowModels(new string[0]);
        Button save = null!;
        save = Ui.Button("保存并使用", async () =>
        {
            string providerId;
            string? chosen = null;
            try
            {
                if (IsBuiltin())
                {
                    providerId = builtins[provider.SelectedIndex].Key;
                    if (key.Password.Trim().Length == 0 && !PiConfig.Authorized(s).Contains(providerId)) { Ui.SetStatus(saveStatus, "请填写 API Key", true); return; }
                    if (key.Password.Trim().Length > 0) PiConfig.SetApiKey(s, providerId, key.Password.Trim());
                }
                else
                {
                    int t = TemplateIndex();
                    providerId = t < templates.Length ? templates[t].Id : "custom";
                    var names = modelName.Text.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
                    if (baseUrl.Text.Trim().Length == 0 || names.Count == 0) { Ui.SetStatus(saveStatus, "请填写接口地址和模型名", true); return; }
                    var newKey = key.Password.Trim();
                    if (newKey.Length == 0) newKey = PiConfig.CustomProvider(s, providerId)?.Key ?? "";
                    baseUrl.Text = PiConfig.NormalizeBaseUrl(baseUrl.Text);
                    PiConfig.SetCustomProvider(s, providerId, baseUrl.Text, newKey, names);
                    chosen = providerId + "/" + names[0];
                }
                UpdateKeyHint();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save the AI provider", ex);
                Ui.SetStatus(saveStatus, "保存失败：" + ex.Message, true);
                return;
            }

            if (PiRuntime.Find(s) == null)
            {
                if (chosen != null) { s.Model = chosen; service.Save(); ShowModels(model.Items.Cast<string>().ToList()); }
                Ui.SetStatus(saveStatus, "已保存。还没有安装 pi，装好后就能用了", true);
                return;
            }
            save.IsEnabled = false;
            saveStatus.Text = "已保存，正在读取可用模型…";
            try
            {
                var list = await LoadModels();
                // Built-in providers: take the first model pi lists for them
                chosen ??= list.FirstOrDefault(m => m.Id.StartsWith(providerId + "/")).Id;
                if (chosen != null) { s.Model = chosen; service.Save(); ShowModels(model.Items.Cast<string>().ToList()); }
                saveStatus.Text = chosen != null ? $"已保存，当前使用 {chosen}" : "已保存，但 pi 没有列出这个服务的模型，请检查 Key";
            }
            catch (Exception ex) { Ui.SetStatus(saveStatus, "已保存，但读取模型失败：" + ex.Message, true); }
            finally { save.IsEnabled = true; }
        }, accent: true);

        body.Children.Add(Spaced(Ui.Row(Label("模型服务"), provider)));
        body.Children.Add(Spaced(Ui.Row(Label("API Key"), key, keyHint)));
        body.Children.Add(urlRow);
        body.Children.Add(modelRow);
        body.Children.Add(Spaced(Ui.Row(save, Spacer(), saveStatus)));

        // —— Current model; filled from pi when the section opens
        async System.Threading.Tasks.Task<System.Collections.Generic.List<(string Id, string Name)>> LoadModels()
        {
            using var client = PiClient.Start(s);
            var list = await client.GetModelsAsync();
            ShowModels(list.Select(m => m.Id));
            return list;
        }
        model.SelectionChanged += (_, _) => { if (!loading) SaveModel(); };
        void SaveModel()
        {
            if (loading) return;
            if (model.SelectedItem is not string text) return;
            if (text == s.Model) return;
            s.Model = text;
            service.Save();
        }
        model.DropDownOpened += async (_, _) =>
        {
            if (model.Items.Count > 1 || PiRuntime.Find(s) == null) return;
            try { await LoadModels(); } catch (Exception) { }
        };
        body.Children.Add(Spaced(Ui.Row(Label("当前模型"), model)));

        // —— Rarely needed
        var advanced = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var share = new CheckBox { Content = $"使用本机 pi 的登录和模型配置（{PiRuntime.SharedConfig}）", IsChecked = s.ShareConfig, Margin = new Thickness(0, 0, 0, 8) };
        share.Click += (_, _) => { s.ShareConfig = share.IsChecked == true; service.Save(); ShowModels(new string[0]); UpdateKeyHint(); };
        advanced.Children.Add(share);
        var thinking = new ComboBox { Width = 120 };
        foreach (var t in ThinkingLevels) thinking.Items.Add(t);
        thinking.SelectedIndex = Math.Max(0, Array.IndexOf(ThinkingLevels, s.Thinking));
        thinking.SelectionChanged += (_, _) => { s.Thinking = ThinkingLevels[thinking.SelectedIndex]; service.Save(); };
        advanced.Children.Add(Spaced(Ui.Row(Label("推理强度"), thinking, Hint("  off 最快；支持推理的模型调高后更准但更慢"))));
        advanced.Children.Add(Spaced(Ui.Row(Ui.Button("打开 pi 终端", () =>
        {
            var install = PiRuntime.Find(s);
            if (install == null) { Ui.SetStatus(state, "请先安装 pi", true); return; }
            PiRuntime.OpenTerminal(install, s);
        }), Hint("  用 Claude、ChatGPT、Copilot 等订阅：在终端里输入 /login"))));
        body.Children.Add(new Expander { Header = "高级", Content = advanced, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }

    static TextBlock Label(string text) { var l = Ui.Label(text); l.Width = 96; return l; }
    static FrameworkElement Spacer() => new Border { Width = 8 };
    static TextBlock Hint(string text) => new() { Text = text, Foreground = DialogWindow.HintBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    static T Spaced<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 0, 0, 8);
        return element;
    }
}
