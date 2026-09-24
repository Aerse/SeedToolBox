using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace SeedToolBox.Views;

/// <summary>Shared dialog look: custom title bar, white body, grey footer with the buttons on the right.</summary>
static class DialogWindow
{
    public static Style TextBoxStyle => (Style)Application.Current.FindResource("FluentTextBox");
    public static Style PasswordBoxStyle => (Style)Application.Current.FindResource("FluentPasswordBox");

    public static Button OkButton(string text = "确定") => new() { Content = text, IsDefault = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
    public static Button CancelButton(string text = "取消") => new() { Content = text, IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

    /// <summary>Theme font and background for resizable tool windows that keep the system title bar.</summary>
    public static void ApplyTheme(Window window)
    {
        var res = Application.Current.Resources;
        window.UseLayoutRounding = true;
        window.FontFamily = (System.Windows.Media.FontFamily)res["UiFont"];
        window.FontSize = 13;
        window.Foreground = (System.Windows.Media.Brush)res["TextBrush"];
        window.Background = (System.Windows.Media.Brush)res["WindowBrush"];
    }

    /// <summary>Multi-line input with the theme look.</summary>
    public static void StyleMultiline(TextBox box)
    {
        box.Style = TextBoxStyle;
        box.VerticalContentAlignment = VerticalAlignment.Stretch;
        box.Padding = new Thickness(8, 6, 8, 6);
    }

    public static Brush HintBrush => (Brush)Application.Current.Resources["HintTextBrush"];

    /// <summary>White rounded panel with a light border.</summary>
    public static Border Card(UIElement child) => new()
    {
        Background = (Brush)Application.Current.Resources["CardBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Child = child,
    };

    public static Window Create(string title, UIElement body, params Button[] buttons)
    {
        var res = Application.Current.Resources;
        var window = new Window
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            FontFamily = (System.Windows.Media.FontFamily)res["UiFont"],
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)res["TextBrush"],
            Background = (System.Windows.Media.Brush)res["CardBrush"],
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
        });

        var close = new Button { Content = "\uE8BB", Style = (Style)res["CloseButton"], Margin = new Thickness(0, 0, 4, 0), ToolTip = "关闭" };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => window.Close();
        var caption = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), FontSize = 12 };
        var titleBar = new DockPanel { Height = 40 };
        DockPanel.SetDock(close, Dock.Right);
        titleBar.Children.Add(close);
        titleBar.Children.Add(caption);

        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) footerButtons.Children.Add(b);
        var footer = new Border
        {
            Background = (System.Windows.Media.Brush)res["WindowBrush"],
            BorderBrush = (System.Windows.Media.Brush)res["CardBorderBrush"],
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 14, 16, 14),
            Child = footerButtons,
        };

        var root = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(titleBar);
        if (buttons.Length > 0) root.Children.Add(footer);
        root.Children.Add(new Border { Padding = new Thickness(12, 0, 12, 8), Child = body });
        window.Content = root;
        window.SourceInitialized += (_, _) => WindowEffects.RoundCorners(window);
        return window;
    }
}
