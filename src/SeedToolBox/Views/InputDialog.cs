using System;
using System.Windows;
using System.Windows.Controls;

namespace SeedToolBox.Views;

public static class InputDialog
{
    /// <summary>Shows a single-line text prompt. Returns null if cancelled or empty.</summary>
    public static string? Show(Window owner, string title, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 320, Margin = new Thickness(4, 4, 4, 12), Style = DialogWindow.TextBoxStyle };
        var ok = DialogWindow.OkButton();
        var cancel = DialogWindow.CancelButton();
        var window = DialogWindow.Create(title, box, ok, cancel);
        window.Owner = owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }
}
