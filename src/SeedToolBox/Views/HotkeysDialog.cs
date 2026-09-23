using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SeedToolBox.Host;

namespace SeedToolBox.Views;

public static class HotkeysDialog
{
    /// <summary>Edits several hotkeys at once. Returns the new values in the same order ("" = none), or null if cancelled.</summary>
    public static string[]? Show(IReadOnlyList<(string Label, string Current)> entries)
    {
        var values = entries.Select(e => e.Current).ToArray();
        var grid = new Grid { Margin = new Thickness(12, 8, 12, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (int i = 0; i < entries.Count; i++)
        {
            int index = i;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = entries[i].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            var box = new TextBox
            {
                Text = Display(values[i]),
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                MinWidth = 200,
                FontSize = 14,
                TextAlignment = TextAlignment.Center,
                Padding = new Thickness(4),
                Margin = new Thickness(0, 4, 8, 4),
            };
            var clear = new Button { Content = "清除", Width = 56, Margin = new Thickness(0, 4, 0, 4) };

            box.PreviewKeyDown += (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                var modifiers = Keyboard.Modifiers;
                // Plain Enter/Esc/Tab keep working as OK/Cancel/next field
                if (modifiers == ModifierKeys.None && key is Key.Enter or Key.Escape or Key.Tab) return;

                e.Handled = true;
                var hotkey = new Hotkey(modifiers, key);
                box.Text = hotkey.IsValid ? hotkey.ToString() : hotkey + "+";
                if (hotkey.IsValid) values[index] = hotkey.ToString();
            };
            // An unfinished combination falls back to the last valid one
            box.LostKeyboardFocus += (_, _) => box.Text = Display(values[index]);
            clear.Click += (_, _) => { values[index] = ""; box.Text = Display(""); };

            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            Grid.SetRow(clear, i);
            Grid.SetColumn(clear, 2);
            grid.Children.Add(label);
            grid.Children.Add(box);
            grid.Children.Add(clear);
        }

        var hint = new TextBlock
        {
            Text = "点击输入框后直接按下组合键（如 Ctrl+Alt+A、F1）",
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(12, 12, 12, 0),
        };
        var ok = new Button { Content = "确定", IsDefault = true, Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Width = 72 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 8, 12, 12),
            Children = { ok, cancel },
        };

        var window = new Window
        {
            Title = "热键设置",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = false,
            Content = new StackPanel { Children = { hint, grid, buttons } },
        };

        ok.Click += (_, _) =>
        {
            var duplicate = values.Where(v => v.Length > 0).GroupBy(v => v).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
            {
                MessageBox.Show(window, $"{duplicate.Key} 被设置了多次，请修改", "热键设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => window.Activate();

        return window.ShowDialog() == true ? values : null;
    }

    static string Display(string value) => value.Length > 0 ? value : "无";
}
