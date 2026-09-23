using System.Windows;
using System.Windows.Controls;

namespace SeedToolBox.Views;

public static class InputDialog
{
    /// <summary>Shows a single-line text prompt. Returns null if cancelled or empty.</summary>
    public static string? Show(Window owner, string title, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 280, Margin = new Thickness(12), Padding = new Thickness(2) };
        var ok = new Button { Content = "确定", IsDefault = true, Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Width = 72 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
            Children = { ok, cancel },
        };

        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel { Children = { box, buttons } },
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }
}
