using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SeedToolBox.Host;

namespace SeedToolBox.Views;

public static class HotkeyDialog
{
    /// <summary>Captures a key combination. Returns null if cancelled, "" if cleared.</summary>
    public static string? Show(Window owner, string title, string current)
    {
        var value = current;
        string Display(string v) => v.Length > 0 ? v : "未设置";
        var box = new TextBox
        {
            Text = Display(value),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Width = 220,
            TextAlignment = TextAlignment.Center,
            Style = DialogWindow.TextBoxStyle,
            Margin = new Thickness(0, 0, 8, 0),
        };
        box.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            // Plain Tab/Enter/Esc keep their dialog meaning
            if (modifiers == ModifierKeys.None && key is Key.Tab or Key.Enter or Key.Escape) return;
            e.Handled = true;
            var hotkey = new Hotkey(modifiers, key);
            if (hotkey.IsValid) value = hotkey.ToString();
            box.Text = hotkey.IsValid ? value : hotkey + "+";
        };
        box.LostKeyboardFocus += (_, _) => box.Text = Display(value);
        var clear = new Button { Content = "清除", MinWidth = 64 };
        clear.Click += (_, _) => { value = ""; box.Text = Display(value); };

        var body = new StackPanel { Margin = new Thickness(4, 4, 4, 12) };
        body.Children.Add(new TextBlock { Text = "在输入框中按下快捷键组合", Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 0, 0, 8) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(clear);
        body.Children.Add(row);

        var ok = DialogWindow.OkButton();
        var cancel = DialogWindow.CancelButton();
        var window = DialogWindow.Create(title, body, ok, cancel);
        window.Owner = owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? value : null;
    }
}
