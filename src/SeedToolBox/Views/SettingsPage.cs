using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeedToolBox.Clips;
using SeedToolBox.DevTools;
using SeedToolBox.Host;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.Views;

/// <summary>Everything that used to live in the settings menu, the tray menu and the settings dialogs.</summary>
sealed class SettingsPage : ScrollViewer
{
    static readonly int[] Rates = { 10, 15, 20, 24, 30, 60 };
    static readonly int[] GifRates = { 5, 10, 15, 20, 25 };
    static readonly int[] GifScales = { 100, 75, 50, 33 };

    /// <summary>What the page reads and changes; supplied by the app.</summary>
    public sealed class Options
    {
        public Func<bool> SizeLocked { get; set; } = () => false;
        public Action<bool> SetSizeLocked { get; set; } = _ => { };
        public Func<bool> AutoStart { get; set; } = () => false;
        public Action<bool> SetAutoStart { get; set; } = _ => { };
        /// <summary>Label and current value of each hotkey.</summary>
        public Func<IReadOnlyList<(string Label, string Value)>> Hotkeys { get; set; } = () => Array.Empty<(string, string)>();
        /// <summary>Changes one hotkey ("" = none); returns an error message, or null when it took effect.</summary>
        public Func<int, string, string?> SetHotkey { get; set; } = (_, _) => null;
        public RecordSettings Record { get; set; } = new();
        public Action SaveRecord { get; set; } = () => { };
        public ClipboardSettings Clipboard { get; set; } = new();
        public Action SaveClipboard { get; set; } = () => { };
        public Action Backup { get; set; } = () => { };
        public Action Restore { get; set; } = () => { };
        public Action OpenData { get; set; } = () => { };
        public Action OpenApp { get; set; } = () => { };
    }

    readonly Options _o;
    readonly CheckBox _lock = Check("锁定启动器窗口尺寸");
    readonly CheckBox _autoStart = Check("开机自动启动（在后台运行）");
    readonly StackPanel _hotkeys = new();
    readonly TextBlock _hotkeyStatus = Ui.Status();

    public SettingsPage(Options options)
    {
        _o = options;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var root = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 16, 0) };
        Content = root;
        root.Children.Add(Ui.Header("设置", "所有设置修改后立即生效"));

        _lock.Click += (_, _) => _o.SetSizeLocked(_lock.IsChecked == true);
        _autoStart.Click += (_, _) => { _o.SetAutoStart(_autoStart.IsChecked == true); _autoStart.IsChecked = _o.AutoStart(); };
        root.Children.Add(Section("常规", _autoStart, _lock));

        _hotkeyStatus.Text = "点击输入框后直接按下组合键（如 Ctrl+Alt+A、F1）";
        root.Children.Add(Section("快捷键", _hotkeys, _hotkeyStatus));

        root.Children.Add(Section("录屏", RecordSection()));
        root.Children.Add(Section("剪贴板历史", ClipboardSection()));

        root.Children.Add(Section("数据",
            Hint("启动项和所有设置保存在程序目录的 Data 文件夹，每天自动备份一次"),
            Ui.Row(Ui.Button("备份数据…", () => _o.Backup()), Ui.Button("恢复数据…", () => _o.Restore()),
                Ui.Button("打开数据文件夹", () => _o.OpenData()), Ui.Button("打开程序位置", () => _o.OpenApp()))));

        var exe = typeof(SettingsPage).Assembly;
        var built = System.IO.File.GetLastWriteTime(exe.Location);
        root.Children.Add(Section("关于",
            new TextBlock { Text = $"SeedToolBox {exe.GetName().Version}", FontWeight = FontWeights.SemiBold },
            Hint($"构建于 {built:yyyy-MM-dd HH:mm}  ·  github.com/Aerse/SeedToolBox")));

        // Other places (tray, hotkeys) can change these, so re-read on every visit
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
    }

    void Reload()
    {
        _lock.IsChecked = _o.SizeLocked();
        _autoStart.IsChecked = _o.AutoStart();
        BuildHotkeys();
    }

    void BuildHotkeys()
    {
        _hotkeys.Children.Clear();
        var entries = _o.Hotkeys();
        for (int i = 0; i < entries.Count; i++)
        {
            int index = i;
            var value = entries[i].Value;
            var box = new TextBox
            {
                Text = Display(value),
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                Width = 200,
                TextAlignment = TextAlignment.Center,
                Style = DialogWindow.TextBoxStyle,
                Margin = new Thickness(0, 0, 8, 0),
            };
            void Apply(string text)
            {
                if (text == value) { box.Text = Display(value); return; }
                var error = _o.SetHotkey(index, text);
                if (error == null)
                {
                    value = text;
                    Ui.SetStatus(_hotkeyStatus, $"{entries[index].Label} 已改为 {Display(text)}");
                }
                else Ui.SetStatus(_hotkeyStatus, error, true);
                box.Text = Display(value);
            }
            box.PreviewKeyDown += (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                var modifiers = Keyboard.Modifiers;
                if (modifiers == ModifierKeys.None && key == Key.Tab) return;
                e.Handled = true;
                var hotkey = new Hotkey(modifiers, key);
                if (hotkey.IsValid) Apply(hotkey.ToString());
                else box.Text = hotkey + "+";
            };
            // An unfinished combination falls back to the current one
            box.LostKeyboardFocus += (_, _) => box.Text = Display(value);
            var label = new TextBlock { Text = entries[i].Label, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            _hotkeys.Children.Add(Ui.Row(label, box, Ui.Button("清除", () => Apply(""))));
        }
    }

    FrameworkElement RecordSection()
    {
        var r = _o.Record;
        var fps = Combo(Rates, v => $"{v} 帧/秒", r.Fps, 30, v => r.Fps = v);
        var quality = new ComboBox { Width = 140, Items = { "低（文件小）", "中", "高（清晰）" }, SelectedIndex = Math.Max(0, Math.Min(2, r.Quality)) };
        quality.SelectionChanged += (_, _) => { r.Quality = quality.SelectedIndex; _o.SaveRecord(); };
        var gifFps = Combo(GifRates, v => $"{v} 帧/秒", r.GifFps, 15, v => r.GifFps = v);
        var gifScale = Combo(GifScales, v => $"{v}%", r.GifScale, 100, v => r.GifScale = v);
        return new StackPanel
        {
            Children =
            {
                Ui.Row(Ui.Label("视频帧率"), fps, Ui.Label("", 16), Ui.Label("画质"), quality),
                Ui.Row(Ui.Label("GIF 帧率"), gifFps, Ui.Label("", 16), Ui.Label("尺寸"), gifScale),
                Toggle("录制系统声音", r.SystemAudio, v => r.SystemAudio = v),
                Toggle("录制麦克风", r.Microphone, v => r.Microphone = v),
                Toggle("显示鼠标指针", r.ShowCursor, v => r.ShowCursor = v),
                Toggle("显示鼠标点击效果", r.ClickEffect, v => r.ClickEffect = v),
                Toggle("开始前倒计时 3 秒", r.Countdown, v => r.Countdown = v),
            },
        };

        CheckBox Toggle(string text, bool current, Action<bool> set)
        {
            var box = Check(text);
            box.IsChecked = current;
            box.Click += (_, _) => { set(box.IsChecked == true); _o.SaveRecord(); };
            return box;
        }
    }

    ComboBox Combo(int[] values, Func<int, string> text, int current, int fallback, Action<int> set)
    {
        var box = new ComboBox { Width = 140 };
        foreach (var v in values) box.Items.Add(text(v));
        int index = Array.IndexOf(values, current);
        box.SelectedIndex = index >= 0 ? index : Array.IndexOf(values, fallback);
        box.SelectionChanged += (_, _) => { set(values[box.SelectedIndex]); _o.SaveRecord(); };
        return box;
    }

    FrameworkElement ClipboardSection()
    {
        var c = _o.Clipboard;
        var enabled = Check("记录剪贴板历史");
        enabled.IsChecked = c.Enabled;
        enabled.Click += (_, _) => { c.Enabled = enabled.IsChecked == true; _o.SaveClipboard(); };
        var autoPaste = Check("选中记录后自动粘贴到之前的窗口");
        autoPaste.IsChecked = c.AutoPaste;
        autoPaste.Click += (_, _) => { c.AutoPaste = autoPaste.IsChecked == true; _o.SaveClipboard(); };
        var max = Ui.Field(80);
        max.Text = c.MaxItems.ToString();
        max.LostKeyboardFocus += (_, _) =>
        {
            if (int.TryParse(max.Text, out var n)) { c.MaxItems = Math.Max(10, Math.Min(5000, n)); _o.SaveClipboard(); }
            max.Text = c.MaxItems.ToString();
        };
        return new StackPanel { Children = { enabled, autoPaste, Ui.Row(Ui.Label("最多保留"), max, Ui.Label("条（置顶的不计入）")) } };
    }

    static CheckBox Check(string text) => new() { Content = text, Margin = new Thickness(0, 0, 0, 10) };

    static TextBlock Hint(string text) => new() { Text = text, Foreground = DialogWindow.HintBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };

    static string Display(string value) => value.Length > 0 ? value : "无";

    static Border Section(string title, params UIElement[] children)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var child in children) panel.Children.Add(child);
        return new Border
        {
            Child = panel,
            BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 14, 16, 6),
            Margin = new Thickness(0, 0, 0, 12),
        };
    }
}
