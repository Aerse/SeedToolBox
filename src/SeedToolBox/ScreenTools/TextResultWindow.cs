using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeedToolBox.Launcher;
using SeedToolBox.Views;

namespace SeedToolBox.ScreenTools;

/// <summary>Shows recognized text (OCR or QR codes) next to the source image, editable and copyable.</summary>
sealed class TextResultWindow : Window
{
    readonly TextBox _text = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 14,
    };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = DialogWindow.HintBrush };
    readonly Button _copy = new() { Content = "复制", MinWidth = 88, IsDefault = true };
    readonly StackPanel _links = new() { Orientation = Orientation.Horizontal };

    public TextResultWindow(string title, BitmapSource? image)
    {
        Title = title;
        Width = image != null ? 760 : 480;
        Height = 420;
        MinWidth = 360;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        DialogWindow.ApplyTheme(this);
        DialogWindow.StyleMultiline(_text);

        var close = new Button { Content = "关闭", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _copy, close } };
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(_links, Dock.Left);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_links);
        bottom.Children.Add(_status);

        var grid = new Grid();
        if (image != null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            // Never enlarged, so small captures stay crisp
            var preview = DialogWindow.Card(new Image { Source = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) });
            preview.Margin = new Thickness(0, 0, 12, 0);
            grid.Children.Add(preview);
            Grid.SetColumn(_text, 1);
        }
        grid.Children.Add(_text);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(grid);
        Content = root;

        _copy.Click += (_, _) =>
        {
            // The selection if there is one, otherwise everything
            (CopyAction ?? ScreenToolService.CopyText)(_text.SelectionLength > 0 ? _text.SelectedText : _text.Text);
            _status.Text = "已复制";
        };
        _text.TextChanged += (_, _) => _status.Text = "";
        Loaded += (_, _) => { Activate(); _text.Focus(); };
        SetBusy("正在识别…");
    }

    /// <summary>Replaces plain-text copying, e.g. to add a table format.</summary>
    public Action<string>? CopyAction { get; set; }

    public void SetBusy(string message)
    {
        _text.IsReadOnly = true;
        _text.Text = "";
        _copy.IsEnabled = false;
        _status.Text = message;
        Cursor = Cursors.AppStarting;
    }

    /// <param name="links">Values that can be opened in the browser.</param>
    public void SetResult(string text, string emptyMessage, IEnumerable<string>? links = null)
    {
        Cursor = null;
        _text.IsReadOnly = false;
        _text.Text = text;
        _text.SelectAll();
        _copy.IsEnabled = text.Length > 0;
        _status.Text = text.Length > 0 ? "" : emptyMessage;

        _links.Children.Clear();
        if (links == null) return;
        var list = links.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var link = list[i];
            var open = new Button
            {
                Content = list.Count == 1 ? "打开链接" : $"打开链接 {i + 1}",
                ToolTip = link,
                Margin = new Thickness(0, 0, 8, 0),
            };
            open.Click += (_, _) => ProcessLauncher.Start(link);
            _links.Children.Add(open);
        }
    }

    public void SetStatus(string message) => _status.Text = message;

    public void SetError(string message)
    {
        Cursor = null;
        _status.Text = message;
        _status.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
    }

    /// <summary>Adds 翻译, which opens the template with {text} replaced by the selection or all text.</summary>
    public void AddTranslate(string urlTemplate)
    {
        if (string.IsNullOrWhiteSpace(urlTemplate) || _copy.Parent is not StackPanel buttons) return;
        var translate = new Button { Content = "翻译", MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), ToolTip = urlTemplate };
        translate.Click += (_, _) =>
        {
            var text = (_text.SelectionLength > 0 ? _text.SelectedText : _text.Text).Trim();
            if (text.Length == 0) return;
            // Browsers and servers reject very long URLs
            if (text.Length > 1800) text = text.Substring(0, 1800);
            var url = urlTemplate.Contains("{text}")
                ? urlTemplate.Replace("{text}", Uri.EscapeDataString(text))
                : urlTemplate + Uri.EscapeDataString(text);
            ProcessLauncher.Start(url);
        };
        buttons.Children.Insert(0, translate);
    }

    public static bool IsLink(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
