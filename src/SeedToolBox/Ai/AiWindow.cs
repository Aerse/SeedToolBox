using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.ScreenTools;
using SeedToolBox.Views;

namespace SeedToolBox.Ai;

/// <summary>
/// One conversation with pi: the streamed answers, and a box for messages that can carry pictures
/// (pasted, dropped, picked from a file or taken as a screenshot). Each window runs its own pi process, ended when the window closes.
/// </summary>
sealed class AiWindow : Window
{
    readonly AiService _service;
    readonly FlowDocument _chat = new() { PagePadding = new Thickness(12), FontSize = 14, TextAlignment = TextAlignment.Left };
    readonly FlowDocumentScrollViewer _output = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
    readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(80) };
    /// <summary>The answer being streamed, re-rendered from Markdown as text arrives.</summary>
    Section? _answer;
    ScrollViewer? _scroll;
    readonly TextBox _question = Ui.Area(wrap: true);
    readonly TextBlock _status = Ui.Status();
    readonly TextBlock _model = new() { Foreground = DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button _send;
    /// <summary>Pictures waiting to go with the next message.</summary>
    readonly List<BitmapSource> _images = new();
    readonly WrapPanel _attachments = new() { Margin = new Thickness(0, 0, 0, 6) };
    PiClient? _client;
    bool _busy, _thinking;
    string _lastAnswer = "";

    public AiWindow(AiService service, string text, BitmapSource? image)
    {
        _service = service;
        Title = "AI 助手";
        Width = 820;
        Height = 600;
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
        _question.ToolTip = "回车发送，Shift+回车换行；可以直接粘贴或拖入图片";

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

        // PreviewKeyDown: AcceptsReturn would take plain Enter before KeyDown sees it
        _question.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Send(_question.Text); }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PasteImage()) e.Handled = true;
        };
        AllowDrop = true;
        _question.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        _question.PreviewDrop += (_, e) => { if (DropImages(e.Data)) e.Handled = true; };
        Drop += (_, e) => DropImages(e.Data);

        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        var answerButtons = Ui.Row(copy, fresh);
        DockPanel.SetDock(answerButtons, Dock.Right);
        tools.Children.Add(answerButtons);
        tools.Children.Add(Ui.Row(capture, pick));

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
        DockPanel.SetDock(tools, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(askRow);
        root.Children.Add(_attachments);
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
        Closed += (_, _) => _client?.Dispose();
        SetBusy(false);
        Ui.SetStatus(_status, "回车发送，Shift+回车换行；图片可以粘贴、拖入或截图");
        _model.Text = service.Settings.Model.Length > 0 ? service.Settings.Model : "默认模型";
        ShowAttachments();
        if (image != null) Attach(image);
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

    bool DropImages(IDataObject data) => data.GetData(DataFormats.FileDrop) is string[] files && AddFiles(files);

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
        var pngs = _images.Select(Png).ToList();
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
            _answer = new Section { Margin = new Thickness(0, 0, 0, 12) };
            _chat.Blocks.Add(_answer);
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

    void EnsureClient()
    {
        if (_client != null) return;
        var client = _client = PiClient.Start(_service.Settings);
        client.ThinkingDelta += _ =>
        {
            if (!_thinking) { _thinking = true; Ui.SetStatus(_status, "模型正在推理…"); }
        };
        client.TextDelta += delta =>
        {
            if (_lastAnswer.Length == 0) Ui.SetStatus(_status, "正在回答…（Esc 停止）");
            _lastAnswer += delta;
            if (!_render.IsEnabled) _render.Start();
        };
        client.Finished += error =>
        {
            _render.Stop();
            RenderAnswer();
            SetBusy(false);
            if (error != null) Ui.SetStatus(_status, "出错了：" + error, true);
            else Ui.SetStatus(_status, _lastAnswer.Length > 0 ? "完成，可以继续追问" : "模型没有返回内容");
        };
        client.Exited += error =>
        {
            _client = null;
            client.Dispose();
            SetBusy(false);
            Ui.SetStatus(_status, "pi 意外退出" + (error.Length > 0 ? "：" + LastLine(error) : ""), true);
        };
        _ = LoadModelName(client);
    }

    async System.Threading.Tasks.Task LoadModelName(PiClient client)
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
    }

    async void Stop()
    {
        if (!_busy || _client == null) return;
        Ui.SetStatus(_status, "正在停止…");
        try { await _client.AbortAsync(); }
        catch (Exception) { Restart(); SetBusy(false); Ui.SetStatus(_status, "已停止"); }
    }

    async void NewConversation()
    {
        if (_busy) return;
        _chat.Blocks.Clear();
        _answer = null;
        _lastAnswer = "";
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
        Cursor = busy ? Cursors.AppStarting : null;
    }

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
        _answer.Blocks.Clear();
        _answer.Blocks.AddRange(Markdown.Render(_lastAnswer).ToList());
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

    static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
