using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SeedToolBox.Host;

namespace SeedToolBox.Views;

public static class HotkeyDialog
{
    /// <summary>Lets the user press a new key combination. Returns null if cancelled, "" if cleared.</summary>
    public static string? Show(string current)
    {
        string result = current;
        var box = new TextBox
        {
            Text = current.Length > 0 ? current : "无",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            MinWidth = 240,
            FontSize = 16,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(4),
            Margin = new Thickness(12, 4, 12, 12),
        };
        var hint = new TextBlock
        {
            Text = "在下框中直接按下新的组合键（如 Ctrl+Q、Alt+Space、F1）",
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(12, 12, 12, 0),
        };
        var ok = new Button { Content = "确定", IsDefault = true, Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var clear = new Button { Content = "清除", Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Width = 72 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
            Children = { ok, clear, cancel },
        };

        var window = new Window
        {
            Title = "设置呼出热键",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = false,
            Content = new StackPanel { Children = { hint, box, buttons } },
        };

        box.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            // Plain Enter/Esc keep working as OK/Cancel
            if (modifiers == ModifierKeys.None && key is Key.Enter or Key.Escape) return;

            e.Handled = true;
            var hotkey = new Hotkey(modifiers, key);
            box.Text = hotkey.IsValid ? hotkey.ToString() : hotkey + "+";
            if (hotkey.IsValid) result = hotkey.ToString();
        };
        ok.Click += (_, _) => window.DialogResult = true;
        clear.Click += (_, _) => { result = ""; window.DialogResult = true; };
        window.Loaded += (_, _) => { window.Activate(); box.Focus(); };

        return window.ShowDialog() == true ? result : null;
    }
}
