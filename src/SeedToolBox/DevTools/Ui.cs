using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SeedToolBox.Views;

namespace SeedToolBox.DevTools;

/// <summary>Building blocks shared by the developer tool pages.</summary>
static class Ui
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");

    /// <summary>Multi-line code editor.</summary>
    public static TextBox Area(bool wrap = false)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = Mono,
            FontSize = 13,
        };
        DialogWindow.StyleMultiline(box);
        return box;
    }

    public static TextBox Field(double width = double.NaN) => new() { Style = DialogWindow.TextBoxStyle, Width = width };

    public static Button Button(string text, Action onClick, bool accent = false)
    {
        var button = new Button { Content = text, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        if (accent) button.Style = (Style)Application.Current.FindResource("AccentButton");
        button.Click += (_, _) => onClick();
        return button;
    }

    public static TextBlock Label(string text, double right = 8) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, right, 0) };

    public static TextBlock Status() => new()
    {
        Foreground = DialogWindow.HintBrush,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 10, 0, 0),
    };

    public static void SetStatus(TextBlock status, string text, bool error = false)
    {
        status.Text = text;
        status.Foreground = error ? (Brush)Application.Current.Resources["DangerBrush"] : DialogWindow.HintBrush;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    public static FrameworkElement Header(string title, string subtitle) => new StackPanel
    {
        Margin = new Thickness(0, 0, 0, 16),
        Children =
        {
            new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold },
            new TextBlock { Text = subtitle, Foreground = DialogWindow.HintBrush, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
        },
    };

    /// <summary>A caption above a control that fills the rest of the space.</summary>
    public static DockPanel Titled(string caption, UIElement content, UIElement? right = null)
    {
        var panel = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (right != null)
        {
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
        }
        top.Children.Add(new TextBlock { Text = caption, Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"], VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>Two equal columns with a gap.</summary>
    public static Grid Columns(UIElement left, UIElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    static readonly string[] SizeUnits = { "KB", "MB", "GB", "TB" };

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes / 1024.0;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0.#} KB" : $"{value:0.##} {SizeUnits[unit]}";
    }

    /// <summary>Accepts files dropped from Explorer. Uses the preview events so text boxes do not swallow the drop.</summary>
    public static void FileDrop(UIElement target, Action<string[]> onFiles)
    {
        target.AllowDrop = true;
        target.PreviewDragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        target.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
            e.Handled = true;
            onFiles(files);
        };
    }

    /// <summary>Copies whatever <paramref name="text"/> returns, if it is not empty.</summary>
    public static Button CopyButton(Func<string> text, string label = "复制结果") =>
        Button(label, () => { var t = text(); if (t.Length > 0) ScreenTools.ScreenToolService.CopyText(t); });

    /// <summary>Loads a text file into <paramref name="target"/>; <paramref name="loaded"/> gets the path afterwards.</summary>
    public static Button OpenButton(TextBox target, TextBlock status, Action<string>? loaded = null) => Button("打开…", () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(target)) != true) return;
        LoadText(target, dialog.FileName, status);
        loaded?.Invoke(dialog.FileName);
    });

    /// <summary>Reads a text file into a box, reporting failures in the status line.</summary>
    public static bool LoadText(TextBox target, string path, TextBlock status)
    {
        try
        {
            if (new FileInfo(path).Length > 50L * 1024 * 1024)
            {
                SetStatus(status, "文件超过 50 MB，太大了", true);
                return false;
            }
            target.Text = TextFiles.Read(path, out _);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(status, "读取失败：" + ex.Message, true);
            return false;
        }
    }

    /// <summary>Saves whatever <paramref name="text"/> returns as UTF-8.</summary>
    public static Button SaveButton(Func<string> text, TextBlock status, string fileName = "result.txt") => Button("保存…", () =>
    {
        var content = text();
        if (content.Length == 0) { SetStatus(status, "没有可保存的内容", true); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false));
            SetStatus(status, "已保存到 " + dialog.FileName);
        }
        catch (Exception ex) { SetStatus(status, "保存失败：" + ex.Message, true); }
    });

    /// <summary>
    /// Runs <paramref name="work"/> off the UI thread and hands the result back on it.
    /// Only the latest call per <paramref name="token"/> reports, so stale results from fast typing are dropped.
    /// </summary>
    public static async void RunAsync<T>(AsyncToken token, Func<T> work, Action<T> done, Action<Exception> failed)
    {
        int id = ++token.Current;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(work);
            if (id == token.Current) done(result);
        }
        catch (Exception ex)
        {
            if (id == token.Current) failed(ex);
        }
    }
}

/// <summary>Tracks the latest <see cref="Ui.RunAsync"/> call of one page.</summary>
sealed class AsyncToken
{
    public int Current;
}

/// <summary>Reads text files keeping their encoding, so rewriting them does not change it.</summary>
static class TextFiles
{
    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string Read(string path, out Encoding encoding) => Decode(File.ReadAllBytes(path), out encoding);

    public static string Decode(byte[] bytes, out Encoding encoding)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(true);
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        try
        {
            var text = StrictUtf8.GetString(bytes);
            encoding = new UTF8Encoding(false);
            return text;
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: the system code page (GBK on Chinese Windows)
            encoding = Encoding.Default;
            return Encoding.Default.GetString(bytes);
        }
    }

    public static byte[] Encode(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0) return body;
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }
}
