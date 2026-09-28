using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.DevTools;

/// <summary>A Postman-style API client: collections, tabs, environments, scripts, a runner and history.</summary>
sealed partial class ApiPage : DockPanel
{
    sealed class Tab
    {
        public ApiRequest? Saved;
        public ApiRequest Draft = new();
        public ExecResult? Result;
        public CancellationTokenSource? Cancel;
        public Border Chip = null!;
        public TextBlock Method = null!, Title = null!, Dot = null!;

        static readonly string Blank = new ApiRequest().Signature();
        public bool Dirty => Draft.Signature() != (Saved?.Signature() ?? Blank);
    }

    readonly Func<string, Task<string>>? _ai;
    readonly ApiData _data;
    readonly List<Tab> _tabs = new();
    Tab? _current;
    readonly DispatcherTimer _saveTimer;
    readonly WrapPanel _tabStrip = new();
    readonly ComboBox _environment = new() { Width = 160, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = Ui.Status();
    readonly Grid _split = new();
    readonly ColumnDefinition _sidebarColumn;

    Window? Owner => Window.GetWindow(this);

    public ApiPage(Func<string, Task<string>>? ai)
    {
        _ai = ai;
        _data = ApiStore.Load();
        if (_data.Collections.Count == 0 && _data.Environments.Count == 0) AddSamples();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        var root = new Grid();
        _sidebarColumn = new ColumnDefinition { Width = new GridLength(Math.Max(180, _data.SidebarWidth)), MinWidth = 160 };
        root.ColumnDefinitions.Add(_sidebarColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        var sidebar = BuildSidebar();
        root.Children.Add(sidebar);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.DragCompleted += (_, _) => { _data.SidebarWidth = _sidebarColumn.ActualWidth; ScheduleSave(); };
        Grid.SetColumn(splitter, 1);
        root.Children.Add(splitter);

        var main = new DockPanel { Margin = new Thickness(6, 0, 0, 0) };
        main.Children.Add(BuildTopBar());
        _status.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        main.Children.Add(_status);
        BuildEditor();
        BuildResponse();
        ApplyLayout();
        main.Children.Add(_split);
        Grid.SetColumn(main, 2);
        root.Children.Add(main);
        Children.Add(root);

        RestoreTabs();
        RefreshEnvironments();
        RefreshTree();
        RefreshHistory();

        PreviewKeyDown += OnKey;
        Unloaded += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); } };
    }

    void AddSamples()
    {
        var env = new ApiEnvironment { Name = "示例环境" };
        env.Variables.Add(new KeyValue { Key = "baseUrl", Value = "https://httpbin.org" });
        _data.Environments.Add(env);
        _data.ActiveEnvironment = env.Id;
        var c = new ApiCollection { Name = "示例集合" };
        c.Requests.Add(new ApiRequest
        {
            Name = "GET 示例", Url = "{{baseUrl}}/get?hello=world",
            Params = { new KeyValue { Key = "hello", Value = "world" } },
            TestScript = "pm.test(\"状态码是 200\", () => pm.response.to.have.status(200));",
        });
        var post = new ApiRequest { Name = "POST JSON", Method = "POST", Url = "{{baseUrl}}/post" };
        post.Body.Mode = BodyModes.Raw;
        post.Body.Raw = "{\n  \"name\": \"seed\",\n  \"time\": \"{{$isoTimestamp}}\"\n}";
        c.Requests.Add(post);
        _data.Collections.Add(c);
    }

    // ---------- persistence ----------

    void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }

    void SaveNow()
    {
        _data.Tabs = _tabs.Select(t => new ApiTabState { RequestId = t.Saved?.Id ?? "", Draft = t.Draft }).ToList();
        _data.ActiveTab = _current != null ? _tabs.IndexOf(_current) : 0;
        try { ApiStore.Save(_data); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { Ui.SetStatus(_status, "保存失败：" + ex.Message, true); }
    }

    /// <summary>Saves right away; for structural changes such as the tree or environments.</summary>
    void Persist() { _saveTimer.Stop(); SaveNow(); }

    // ---------- top bar and tabs ----------

    UIElement BuildTopBar()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(bar, Dock.Top);
        var right = Ui.Row(Ui.Label("环境"), _environment, ApiUi.Icon("", "管理环境和全局变量", ManageEnvironments));
        right.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        _environment.SelectionChanged += (_, _) =>
        {
            if (_environment.SelectedItem is ComboBoxItem { Tag: string id } && id != _data.ActiveEnvironment)
            {
                _data.ActiveEnvironment = id;
                Persist();
            }
        };
        var add = ApiUi.Icon("", "新标签页 (Ctrl+T)", () => OpenTab(null, new ApiRequest()));
        _tabStrip.Children.Add(add);
        bar.Children.Add(_tabStrip);
        return bar;
    }

    void RefreshEnvironments()
    {
        _environment.Items.Clear();
        _environment.Items.Add(new ComboBoxItem { Content = "无环境", Tag = "" });
        foreach (var e in _data.Environments) _environment.Items.Add(new ComboBoxItem { Content = e.Name, Tag = e.Id });
        _environment.SelectedItem = _environment.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _data.ActiveEnvironment) ?? _environment.Items[0];
    }

    ApiEnvironment? ActiveEnvironment => _data.Environments.FirstOrDefault(e => e.Id == _data.ActiveEnvironment);

    void ManageEnvironments()
    {
        new EnvironmentWindow(Owner, _data, ScheduleSave, ActiveEnvironment).ShowDialog();
        if (ActiveEnvironment == null) _data.ActiveEnvironment = "";
        RefreshEnvironments();
        Persist();
    }

    void RestoreTabs()
    {
        foreach (var state in _data.Tabs)
        {
            var saved = state.RequestId.Length > 0 ? ApiTree.FindRequest(_data, state.RequestId) : null;
            AddTab(saved, state.Draft ?? saved?.Clone() ?? new ApiRequest());
        }
        if (_tabs.Count == 0) AddTab(null, new ApiRequest());
        Select(_tabs[Math.Max(0, Math.Min(_data.ActiveTab, _tabs.Count - 1))]);
    }

    Tab AddTab(ApiRequest? saved, ApiRequest draft)
    {
        var tab = new Tab { Saved = saved, Draft = draft };
        tab.Method = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        tab.Title = new TextBlock { MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        tab.Dot = new TextBlock { Text = "●", Foreground = ApiUi.Res("AccentBrush"), Margin = new Thickness(6, 0, 0, 0), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4), Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand, ToolTip = "关闭 (Ctrl+W)", Foreground = ApiUi.Res("TextBrush"),
        };
        close.Click += (_, _) => CloseTab(tab);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { tab.Method, tab.Title, tab.Dot, close } };
        tab.Chip = new Border
        {
            Child = content, Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 4, 4),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        tab.Chip.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) Select(tab); };
        tab.Chip.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        _tabs.Add(tab);
        _tabStrip.Children.Insert(_tabStrip.Children.Count - 1, tab.Chip);
        UpdateChip(tab);
        return tab;
    }

    /// <summary>Opens <paramref name="saved"/> in its tab if one is open already, otherwise in a new tab.</summary>
    void OpenTab(ApiRequest? saved, ApiRequest? draft = null)
    {
        if (saved != null && _tabs.FirstOrDefault(t => t.Saved == saved) is { } open) { Select(open); return; }
        Select(AddTab(saved, draft ?? saved?.Clone() ?? new ApiRequest()));
        ScheduleSave();
    }

    void UpdateChip(Tab tab)
    {
        tab.Method.Text = ApiUi.MethodShort(tab.Draft.Method);
        tab.Method.Foreground = ApiUi.MethodBrush(tab.Draft.Method);
        tab.Title.Text = tab.Draft.Name.Length > 0 ? tab.Draft.Name : "未命名";
        tab.Dot.Visibility = tab.Dirty ? Visibility.Visible : Visibility.Collapsed;
        var selected = tab == _current;
        tab.Chip.Background = selected ? ApiUi.Res("CardBrush") : Brushes.Transparent;
        tab.Chip.BorderBrush = selected ? ApiUi.Res("AccentBrush") : ApiUi.Res("ControlBorderBrush");
        tab.Chip.ToolTip = tab.Saved == null ? "未保存的请求" : tab.Draft.Url;
    }

    void Select(Tab tab)
    {
        _current = tab;
        foreach (var t in _tabs) UpdateChip(t);
        LoadEditor(tab.Draft);
        ShowResult(tab.Result);
        UpdateSendButton();
    }

    bool CloseTab(Tab tab)
    {
        if (tab.Dirty && !(tab.Saved == null && tab.Draft.Url.Length == 0))
        {
            Select(tab);
            var answer = MessageBox.Show(Owner!, $"「{tab.Title.Text}」有没保存的修改，要保存吗？", "关闭标签页", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !SaveTab(tab)) return false;
        }
        tab.Cancel?.Cancel();
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _tabStrip.Children.Remove(tab.Chip);
        if (_tabs.Count == 0) AddTab(null, new ApiRequest());
        if (_current == tab) Select(_tabs[Math.Min(index, _tabs.Count - 1)]);
        ScheduleSave();
        return true;
    }

    /// <summary>Called after every edit of the current draft.</summary>
    void Touched()
    {
        if (_current == null) return;
        UpdateChip(_current);
        ScheduleSave();
    }

    bool SaveTab(Tab tab)
    {
        if (tab.Saved != null)
        {
            tab.Saved.Assign(tab.Draft);
        }
        else
        {
            var name = Ai.AiService.AskText(Owner, "保存请求", "请求名称", tab.Draft.Name.Length > 0 && tab.Draft.Name != "新请求" ? tab.Draft.Name : GuessName(tab.Draft));
            if (string.IsNullOrWhiteSpace(name)) return false;
            var folder = ChooseFolder("保存到哪个集合或文件夹？");
            if (folder == null) return false;
            tab.Draft.Name = name!.Trim();
            var saved = tab.Draft.Clone(newId: true);
            folder.Requests.Add(saved);
            tab.Saved = saved;
            if (_current == tab) LoadEditor(tab.Draft);
        }
        UpdateChip(tab);
        Persist();
        RefreshTree();
        Ui.SetStatus(_status, "已保存「" + tab.Draft.Name + "」");
        return true;
    }

    static string GuessName(ApiRequest r)
    {
        var url = r.Url.Split('?')[0].TrimEnd('/');
        var last = url.Length > 0 ? url.Substring(url.LastIndexOf('/') + 1) : "";
        return last.Length > 0 && !last.Contains("}}") ? r.Method + " " + last : "新请求";
    }

    /// <summary>Lets the user pick a collection or folder; creates a collection when there is none.</summary>
    ApiFolder? ChooseFolder(string title)
    {
        if (_data.Collections.Count == 0) _data.Collections.Add(new ApiCollection { Name = "我的集合" });
        var list = new ListBox { Height = 260, Width = 380, Margin = new Thickness(0, 10, 0, 0) };
        void Add(ApiFolder f, int depth)
        {
            list.Items.Add(new ListBoxItem { Content = new string(' ', depth * 4) + (depth == 0 ? "\U0001F4DA " : "\U0001F4C1 ") + f.Name, Tag = f });
            foreach (var sub in f.Folders) Add(sub, depth + 1);
        }
        foreach (var c in _data.Collections) Add(c, 0);
        list.SelectedIndex = 0;
        var ok = Views.DialogWindow.OkButton();
        var body = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = title }, list } };
        var window = Views.DialogWindow.Create("选择位置", body, ok, Views.DialogWindow.CancelButton());
        window.Owner = Owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok.Click += (_, _) => window.DialogResult = true;
        list.MouseDoubleClick += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true && list.SelectedItem is ListBoxItem { Tag: ApiFolder f2 } ? f2 : null;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.S when _current != null: SaveTab(_current); break;
            case Key.Enter: SendOrCancel(); break;
            case Key.T: OpenTab(null, new ApiRequest()); break;
            case Key.W when _current != null: CloseTab(_current); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>The variables a request sees: globals, its collection's, and the active environment's.</summary>
    ExecContext ContextFor(Tab tab)
    {
        var path = tab.Saved != null ? ApiTree.PathOf(_data, tab.Saved) ?? new List<ApiFolder>() : new List<ApiFolder>();
        var collection = path.FirstOrDefault() as ApiCollection;
        var env = ActiveEnvironment;
        return new ExecContext
        {
            Collection = collection,
            Path = path,
            Variables = new ApiVariables(_data.Globals, collection?.Variables, env?.Variables),
            EnvironmentName = env?.Name ?? "",
        };
    }
}
