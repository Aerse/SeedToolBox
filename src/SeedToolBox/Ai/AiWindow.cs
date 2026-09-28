using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using SeedToolBox.Ai.Automation;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.ScreenTools;
using SeedToolBox.Views;

namespace SeedToolBox.Ai;

/// <summary>
/// One conversation with pi: the streamed answers, and a box for messages that can carry pictures
/// (pasted, dropped, picked from a file or taken as a screenshot). Each window runs its own pi process, ended when the window closes.
/// In automation mode pi gets the SeedToolBox tools; every change is shown as a card the user allows or refuses.
/// </summary>
sealed class AiWindow : Window
{
    readonly AiService _service;
    readonly FlowDocument _chat = new() { PagePadding = new Thickness(12), FontSize = 14, TextAlignment = TextAlignment.Left };
    readonly FlowDocumentScrollViewer _output = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
    readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(80) };
    /// <summary>The answer being streamed, re-rendered from Markdown as text arrives; a tool call starts a new one.</summary>
    Section? _answer;
    readonly System.Text.StringBuilder _segment = new();
    /// <summary>The start of the answer that is rendered for good, and how many of the answer's blocks that made; only the rest is redone.</summary>
    int _frozenLength, _frozenBlocks;
    ScrollViewer? _scroll;
    readonly TextBox _question = Ui.Area(wrap: true);
    readonly TextBlock _status = Ui.Status();
    readonly TextBlock _model = new() { Foreground = DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button _send;
    readonly ToggleButton _mode = new() { Content = "自动化", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
    readonly WrapPanel _tasks = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly StackPanel _automationTools;
    /// <summary>Pictures waiting to go with the next message.</summary>
    readonly List<BitmapSource> _images = new();
    readonly WrapPanel _attachments = new() { Margin = new Thickness(0, 0, 0, 6) };
    /// <summary>The tool calls shown in the chat, by call id: the status line and the details text.</summary>
    readonly Dictionary<string, (TextBlock Line, TextBox Details, string Label)> _calls = new();
    PiClient? _client;
    ToolServer? _server;
    bool _busy, _thinking, _automation;
    string _lastAnswer = "", _firstQuestion = "";

    public AiWindow(AiService service, string text, BitmapSource? image, bool? automation = null)
    {
        _service = service;
        _automation = automation ?? service.Settings.AutomationDefault;
        Width = 820;
        Height = 640;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DialogWindow.ApplyTheme(this);

        _chat.FontFamily = FontFamily;
        _chat.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        _output.Document = _chat;
        _render.Tick += (_, _) => { _render.Stop(); RenderAnswer(); };
        _question.AcceptsTab = false;
        _question.MinHeight = 64;
        _question.MaxHeight = 160;
        _question.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _question.Text = text;

        _send = Ui.Button("发送", () => { if (_busy) Stop(); else Send(_question.Text); }, accent: true);
        var copy = Ui.Button("复制回答", () =>
        {
            var answer = _output.Selection is { IsEmpty: false } selection ? selection.Text : _lastAnswer;
            if (answer.Trim().Length == 0) return;
            ScreenToolService.CopyText(answer.Trim());
            Ui.SetStatus(_status, "已复制");
        });
        var fresh = Ui.Button("新对话", NewConversation);
        var capture = Ui.Button("截图", () => service.CaptureFor(this));
        capture.ToolTip = "截一块屏幕，附到这条消息里";
        var pick = Ui.Button("图片", PickImage);
        pick.ToolTip = "从文件添加图片";
        _mode.ToolTip = "自动化模式：AI 可以用工具查看和整理文件、调音量亮度、加提醒等；每个改动都会先问你";
        _mode.IsChecked = _automation;
        _mode.Click += (_, _) => SetMode(_mode.IsChecked == true);
        var save = Ui.Button("存为快捷任务", SaveTask);
        save.ToolTip = "把这次对话的第一条消息存成按钮，以后一点就做；也可以定时运行";
        var log = Ui.Button("操作记录", service.OpenLog);
        log.ToolTip = "查看和撤销 AI 做过的改动";
        _automationTools = Ui.Row(save, log);

        // PreviewKeyDown: AcceptsReturn would take plain Enter before KeyDown sees it
        _question.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Send(_question.Text); }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PasteImage()) e.Handled = true;
        };
        AllowDrop = true;
        _question.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        _question.PreviewDrop += (_, e) => { if (DropFiles(e.Data)) e.Handled = true; };
        Drop += (_, e) => DropFiles(e.Data);

        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        var answerButtons = Ui.Row(copy, fresh);
        DockPanel.SetDock(answerButtons, Dock.Right);
        tools.Children.Add(answerButtons);
        tools.Children.Add(Ui.Row(_mode, capture, pick, _automationTools));

        var askRow = new DockPanel();
        _send.Margin = new Thickness(8, 0, 0, 0);
        _send.VerticalAlignment = VerticalAlignment.Stretch;
        DockPanel.SetDock(_send, Dock.Right);
        askRow.Children.Add(_send);
        askRow.Children.Add(_question);

        var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        _model.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(_model, Dock.Right);
        bottom.Children.Add(_model);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(askRow, Dock.Bottom);
        DockPanel.SetDock(_attachments, Dock.Bottom);
        DockPanel.SetDock(_tasks, Dock.Bottom);
        DockPanel.SetDock(tools, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(askRow);
        root.Children.Add(_attachments);
        root.Children.Add(_tasks);
        root.Children.Add(tools);
        root.Children.Add(DialogWindow.Card(_output));
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (_busy) Stop(); else Close(); e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            Activate();
            _question.Focus();
            _question.CaretIndex = _question.Text.Length;
            _scroll = FindScroll(_output);
        };
        Closed += (_, _) => { AnswerCards(); Restart(); };
        SetBusy(false);
        _model.Text = service.Settings.Model.Length > 0 ? service.Settings.Model : "默认模型";
        ShowAttachments();
        ShowMode();
        if (image != null) Attach(image);
    }

    void ShowMode()
    {
        Title = _automation ? "AI 助手 · 自动化" : "AI 助手";
        _automationTools.Visibility = _automation ? Visibility.Visible : Visibility.Collapsed;
        _question.ToolTip = _automation
            ? "说出要做的事，例如「把下载文件夹里的图片按月份整理」；回车发送，Shift+回车换行"
            : "回车发送，Shift+回车换行；可以直接粘贴或拖入图片";
        Ui.SetStatus(_status, _automation ? "自动化模式：改动文件、结束进程等操作会先请你确认，做过的改动可以在「操作记录」里撤销" : "回车发送，Shift+回车换行；图片可以粘贴、拖入或截图");
        ShowTasks();
    }

    void SetMode(bool automation)
    {
        if (_busy) { _mode.IsChecked = _automation; return; }
        if (automation == _automation) return;
        _automation = automation;
        // The tools are fixed when pi starts, so the conversation starts over
        Restart();
        _firstQuestion = "";
        _chat.Blocks.Add(new Paragraph(new Run(automation ? "—— 已切换到自动化模式，新对话 ——" : "—— 已切换到聊天模式，新对话 ——"))
        {
            Foreground = DialogWindow.HintBrush, FontSize = 12, TextAlignment = TextAlignment.Center,
        });
        ScrollToEnd();
        ShowMode();
    }

    void ShowTasks()
    {
        _tasks.Children.Clear();
        if (_automation)
            foreach (var task in _service.Settings.QuickTasks)
            {
                var item = task;
                var button = Ui.Button(item.Name, () => { if (!_busy) Send(item.Prompt); });
                button.Margin = new Thickness(0, 0, 6, 4);
                button.ToolTip = item.Prompt + (item.Schedule.Length > 0 ? "\n\n定时：" + item.Schedule : "") + "\n\n右键可以删除或定时";
                var menu = new ContextMenu();
                var schedule = new MenuItem { Header = "定时运行…" };
                schedule.Click += (_, _) => _service.ScheduleTask(item, this);
                var remove = new MenuItem { Header = "删除" };
                remove.Click += (_, _) => { _service.RemoveTask(item); ShowTasks(); };
                menu.Items.Add(schedule);
                menu.Items.Add(remove);
                button.ContextMenu = menu;
                _tasks.Children.Add(button);
            }
        _tasks.Visibility = _tasks.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SaveTask()
    {
        var prompt = _firstQuestion.Length > 0 ? _firstQuestion : _question.Text.Trim();
        if (prompt.Length == 0) { Ui.SetStatus(_status, "先发一条消息，再存为快捷任务", true); return; }
        var name = AiService.AskText(this, "存为快捷任务", "按钮上显示的名字", prompt.Length > 12 ? prompt.Substring(0, 12) : prompt);
        if (string.IsNullOrWhiteSpace(name)) return;
        _service.Settings.QuickTasks.Add(new QuickTask { Name = name!.Trim(), Prompt = prompt });
        _service.Save();
        ShowTasks();
        Ui.SetStatus(_status, "已保存；右键按钮可以设置定时运行");
    }

    /// <summary>Adds a picture to the next message.</summary>
    public void Attach(BitmapSource image)
    {
        _images.Add(image);
        ShowAttachments();
        if (_question.Text.Trim().Length == 0) Ui.SetStatus(_status, "已附上图片，输入问题后回车；直接回车会让 AI 描述图片");
        Activate();
        _question.Focus();
    }

    void ShowAttachments()
    {
        _attachments.Children.Clear();
        foreach (var image in _images)
        {
            var item = image;
            var remove = new Button { Content = "✕", Padding = new Thickness(4, 0, 4, 0), MinWidth = 0, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "移除" };
            remove.Click += (_, _) => { _images.Remove(item); ShowAttachments(); };
            var cell = new Grid { Margin = new Thickness(0, 0, 8, 0) };
            cell.Children.Add(DialogWindow.Card(new Image { Source = item, Height = 64, MaxWidth = 160, Stretch = Stretch.Uniform, Margin = new Thickness(4) }));
            cell.Children.Add(remove);
            _attachments.Children.Add(cell);
        }
        _attachments.Visibility = _images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Ctrl+V with a picture (or picture files) on the clipboard attaches it instead of pasting text.</summary>
    bool PasteImage()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image) { Attach(image); return true; }
            if (Clipboard.ContainsFileDropList()) return AddFiles(Clipboard.GetFileDropList().Cast<string>());
        }
        catch (COMException ex) { Log.Error("Failed to paste an image", ex); }
        return false;
    }

    /// <summary>Pictures are attached; in automation mode other files and folders are added to the message as paths.</summary>
    bool DropFiles(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return false;
        bool any = AddFiles(files);
        if (_automation)
        {
            var paths = files.Where(f => !ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
            if (paths.Count > 0)
            {
                _question.Text = (_question.Text.TrimEnd() + "\n" + string.Join("\n", paths)).TrimStart('\n');
                _question.CaretIndex = _question.Text.Length;
                any = true;
            }
        }
        return any;
    }

    void PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff", Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    bool AddFiles(IEnumerable<string> files)
    {
        bool any = false;
        foreach (var file in files.Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(file);
                image.EndInit();
                image.Freeze();
                Attach(image);
                any = true;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
                Ui.SetStatus(_status, "打不开图片：" + Path.GetFileName(file), true);
            }
        }
        return any;
    }

    /// <summary>Sends a message with the pictures waiting in the box.</summary>
    public void Send(string question)
    {
        question = question.Trim();
        if (_busy || question.Length == 0 && _images.Count == 0) return;
        if (question.Length == 0) question = AiService.ImageQuestion;
        if (_firstQuestion.Length == 0) _firstQuestion = question;
        var pngs = _images.Select(AutomationTools.Png).ToList();
        AddQuestion(question, _images.ToList());
        _question.Clear();
        _images.Clear();
        ShowAttachments();
        Prompt(question, pngs);
    }

    async void Prompt(string message, List<byte[]> pngs)
    {
        try
        {
            EnsureClient();
            SetBusy(true);
            _lastAnswer = "";
            _thinking = false;
            StartSegment();
            Ui.SetStatus(_status, "正在思考…");
            await _client!.PromptAsync(message, pngs);
        }
        catch (Exception ex)
        {
            Log.Error("AI prompt failed", ex);
            SetBusy(false);
            Ui.SetStatus(_status, "发送失败：" + ex.Message, true);
        }
    }

    void StartSegment()
    {
        _segment.Clear();
        _frozenLength = _frozenBlocks = 0;
        _answer = new Section { Margin = new Thickness(0, 0, 0, 12) };
        _chat.Blocks.Add(_answer);
    }

    void EnsureClient()
    {
        if (_client != null) return;
        PiClient client;
        if (_automation)
        {
            _server = new ToolServer(_service) { Confirm = ConfirmChange };
            client = PiClient.StartAutomation(_service.Settings, _server);
        }
        else client = PiClient.Start(_service.Settings);
        _client = client;
        client.ThinkingDelta += _ =>
        {
            if (!_thinking) { _thinking = true; Ui.SetStatus(_status, "模型正在推理…"); }
        };
        client.TextDelta += delta =>
        {
            if (_segment.Length == 0) Ui.SetStatus(_status, "正在回答…（Esc 停止）");
            _segment.Append(delta);
            _lastAnswer += delta;
            if (!_render.IsEnabled) _render.Start();
        };
        client.ToolStarted += ToolStarted;
        client.ToolEnded += ToolEnded;
        client.UiRequest += request => _ = AnswerUi(client, request);
        client.Notice += notice => Ui.SetStatus(_status, notice);
        client.Finished += error =>
        {
            _render.Stop();
            RenderAnswer();
            SetBusy(false);
            AnswerCards();
            if (error != null) Ui.SetStatus(_status, "出错了：" + error, true);
            else Ui.SetStatus(_status, _lastAnswer.Length > 0 || _calls.Count > 0 ? "完成，可以继续追问" : "模型没有返回内容");
        };
        client.Exited += error =>
        {
            if (_client == client) Restart();
            SetBusy(false);
            AnswerCards();
            Ui.SetStatus(_status, "pi 意外退出" + (error.Length > 0 ? "：" + LastLine(error) : ""), true);
        };
        _ = LoadModelName(client);
    }

    async Task LoadModelName(PiClient client)
    {
        try
        {
            var model = await client.GetModelAsync();
            _model.Text = model.Length > 0 ? model : "未配置模型：去设置里填写 API Key";
        }
        catch (Exception) { }
    }

    void Restart()
    {
        _client?.Dispose();
        _client = null;
        _server?.Dispose();
        _server = null;
    }

    async void Stop()
    {
        if (!_busy || _client == null) return;
        Ui.SetStatus(_status, "正在停止…");
        AnswerCards();
        try { await _client.AbortAsync(); }
        catch (Exception) { Restart(); SetBusy(false); Ui.SetStatus(_status, "已停止"); }
    }

    async void NewConversation()
    {
        if (_busy) return;
        _chat.Blocks.Clear();
        _calls.Clear();
        _answer = null;
        _lastAnswer = _firstQuestion = "";
        _segment.Clear();
        _frozenLength = _frozenBlocks = 0;
        _server?.ResetSession();
        if (_client != null)
        {
            try { await _client.NewSessionAsync(); }
            catch (Exception) { Restart(); }
        }
        Ui.SetStatus(_status, "已开始新对话");
        _question.Focus();
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _send.Content = busy ? "停止" : "发送";
        _mode.IsEnabled = !busy;
        Cursor = busy ? Cursors.AppStarting : null;
    }

    // Tool calls

    void ToolStarted(string id, string tool, JObject args)
    {
        _render.Stop();
        RenderAnswer();
        var label = _server?.Label(tool) ?? tool;
        var line = new TextBlock { Text = "⏳ " + label + Brief(args), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"] };
        var details = new TextBox
        {
            Text = "参数：\n" + args.ToString(Newtonsoft.Json.Formatting.Indented),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            Style = DialogWindow.TextBoxStyle,
        };
        var expander = new Expander { Header = line, Content = details, Margin = new Thickness(0, 0, 0, 2) };
        _chat.Blocks.Add(new BlockUIContainer(expander) { Margin = new Thickness(0, 0, 0, 6) });
        _calls[id] = (line, details, label);
        Ui.SetStatus(_status, "正在" + label + "…");
        StartSegment();
        ScrollToEnd();
    }

    void ToolEnded(string id, string output, bool failed)
    {
        if (!_calls.TryGetValue(id, out var call)) return;
        call.Line.Text = (failed ? "✗ " : "✓ ") + call.Line.Text.Substring(2);
        if (failed) call.Line.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        call.Details.Text += "\n\n" + (failed ? "失败：\n" : "结果：\n") + (output.Length > 8000 ? output.Substring(0, 8000) + "…" : output);
        Ui.SetStatus(_status, "正在思考…");
    }

    /// <summary>"：D:\a.txt" — the first short string argument, to tell calls apart at a glance.</summary>
    static string Brief(JObject args)
    {
        foreach (var p in args.Properties())
        {
            if (p.Value.Type == JTokenType.String && ((string)p.Value!).Length is > 0 and < 120) return "：" + ((string)p.Value!).Replace("\n", " ");
            if (p.Value is JArray { Count: > 0 } array) return $"：{array.Count} 项";
        }
        return "";
    }

    // Cards

    async Task<ConfirmChoice> ConfirmChange(string label, string change, bool sessionAllow)
    {
        var options = sessionAllow ? new[] { "允许", "拒绝", "本次对话都允许" } : new[] { "允许", "拒绝" };
        var choice = await AskCard("需要你确认：" + label, change, options);
        return choice switch
        {
            "允许" => ConfirmChoice.Allow,
            "本次对话都允许" => ConfirmChoice.AllowSession,
            _ => ConfirmChoice.Deny,
        };
    }

    /// <summary>Answers a question from an extension (our plugin gate, or an installed plugin) with a card.</summary>
    async Task AnswerUi(PiClient client, JObject request)
    {
        var id = (string?)request["id"] ?? "";
        var method = (string?)request["method"];
        var title = (string?)request["title"] ?? "";
        var message = (string?)request["message"] ?? "";
        switch (method)
        {
            case "select":
                var options = (request["options"] as JArray ?? new JArray()).Select(o => o.Type == JTokenType.Object ? (string?)o["label"] ?? o.ToString() : o.ToString()).ToArray();
                var choice = await AskCard("插件询问", (title + "\n" + message).Trim(), options);
                client.RespondUi(id, value: choice);
                break;
            case "confirm":
                var yes = await AskCard("插件询问", (title + "\n" + message).Trim(), new[] { "是", "否" });
                if (yes == null) client.RespondUi(id);
                else client.RespondUi(id, confirmed: yes == "是");
                break;
            default:
                var text = await AskCard("插件询问", (title + "\n" + message).Trim(), new[] { "确定", "取消" }, input: (string?)request["prefill"] ?? "");
                client.RespondUi(id, value: text);
                break;
        }
    }

    /// <summary>A card in the chat with buttons; resolves with the chosen button (or the typed text), or null when cancelled.</summary>
    Task<string?> AskCard(string title, string message, string[] options, string? input = null)
    {
        _render.Stop();
        RenderAnswer();
        var done = new TaskCompletionSource<string?>();
        var body = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            Margin = new Thickness(0, 6, 0, 8),
        };
        var answer = input != null ? new TextBox { Text = input, Style = DialogWindow.TextBoxStyle, Margin = new Thickness(0, 0, 0, 8), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160 } : null;
        var buttons = new WrapPanel();
        var state = new TextBlock { Foreground = DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        void Finish(string? value)
        {
            if (!done.TrySetResult(value)) return;
            buttons.Visibility = Visibility.Collapsed;
            if (answer != null) answer.IsReadOnly = true;
            state.Text = value == null ? "已取消" : "→ " + (input != null ? (value == "取消" ? "已取消" : "已提交") : value);
            state.Visibility = Visibility.Visible;
            Ui.SetStatus(_status, "正在处理…");
        }
        for (int i = 0; i < options.Length; i++)
        {
            var option = options[i];
            var button = Ui.Button(option, () =>
            {
                if (input != null) Finish(option == "取消" ? null : answer!.Text);
                else Finish(option);
            }, accent: i == 0);
            button.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(button);
        }
        var panel = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeights.SemiBold }, body } };
        if (answer != null) panel.Children.Add(answer);
        panel.Children.Add(buttons);
        panel.Children.Add(state);
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentBrush"],
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Child = panel,
        };
        _chat.Blocks.Add(new BlockUIContainer(card) { Margin = new Thickness(0, 0, 0, 10) });
        // A card can only be answered once; stopping answers the ones still open
        _pendingFinish[done] = Finish;
        done.Task.ContinueWith(_ => _pendingFinish.Remove(done), TaskScheduler.FromCurrentSynchronizationContext());
        StartSegment();
        ScrollToEnd();
        Ui.SetStatus(_status, "等你确认…");
        if (!IsActive) FlashWindow();
        return done.Task;
    }

    readonly Dictionary<TaskCompletionSource<string?>, Action<string?>> _pendingFinish = new();

    /// <summary>Refuses every card still waiting.</summary>
    void AnswerCards()
    {
        foreach (var finish in _pendingFinish.Values.ToList()) finish(null);
    }

    void FlashWindow()
    {
        var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle, dwFlags = 3 | 12, uCount = 3 };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }

    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);

    /// <summary>The user's message as a tinted block with its pictures.</summary>
    void AddQuestion(string question, List<BitmapSource> images)
    {
        var block = new Section
        {
            Background = (Brush)Application.Current.Resources["SelectedBrush"],
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 12),
        };
        block.Blocks.Add(new Paragraph(new Run(question)) { Margin = new Thickness(0) });
        if (images.Count > 0)
        {
            var strip = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var image in images)
                strip.Children.Add(new Image { Source = image, MaxHeight = 180, MaxWidth = 320, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(0, 0, 8, 0) });
            block.Blocks.Add(new BlockUIContainer(strip));
        }
        _chat.Blocks.Add(block);
        ScrollToEnd();
    }

    void RenderAnswer()
    {
        if (_answer == null) return;
        bool atEnd = _scroll == null || _scroll.VerticalOffset >= _scroll.ScrollableHeight - 24;
        // Closed paragraphs and code blocks stay as they are (with any selection in them); only the open tail is rendered again
        var tail = _segment.ToString(_frozenLength, _segment.Length - _frozenLength);
        while (_answer.Blocks.Count > _frozenBlocks) _answer.Blocks.Remove(_answer.Blocks.LastBlock);
        int stable = Markdown.StableEnd(tail);
        if (stable > 0)
        {
            _answer.Blocks.AddRange(Markdown.Render(tail.Substring(0, stable)).ToList());
            _frozenBlocks = _answer.Blocks.Count;
            _frozenLength += stable;
            tail = tail.Substring(stable);
        }
        _answer.Blocks.AddRange(Markdown.Render(tail).ToList());
        if (atEnd) ScrollToEnd();
    }

    /// <summary>Scrolls down after layout, so the newly added blocks are measured.</summary>
    void ScrollToEnd()
    {
        _scroll ??= FindScroll(_output);
        if (_scroll != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _scroll.ScrollToEnd()));
    }

    static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

    static string LastLine(string text)
    {
        var lines = text.Trim().Split('\n');
        return lines[lines.Length - 1].Trim();
    }
}
