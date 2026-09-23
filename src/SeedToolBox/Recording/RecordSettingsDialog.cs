using System;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.ScreenTools;
using SeedToolBox.Views;

namespace SeedToolBox.Recording;

static class RecordSettingsDialog
{
    static readonly int[] Rates = { 10, 15, 20, 24, 30, 60 };
    static readonly int[] GifRates = { 5, 10, 15, 20, 25 };
    static readonly int[] GifScales = { 100, 75, 50, 33 };

    /// <summary>Edits the settings in place. Returns false if cancelled.</summary>
    public static bool Show(RecordSettings settings, Window? owner)
    {
        var grid = new Grid { Margin = new Thickness(4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fps = Combo(Rates, r => $"{r} 帧/秒", settings.Fps, 30);
        var quality = new ComboBox { Items = { "低（文件小）", "中", "高（清晰）" }, SelectedIndex = Math.Max(0, Math.Min(2, settings.Quality)) };
        var gifFps = Combo(GifRates, r => $"{r} 帧/秒", settings.GifFps, 15);
        var gifScale = Combo(GifScales, s => $"{s}%", settings.GifScale, 100);
        var systemAudio = new CheckBox { Content = "录制系统声音", IsChecked = settings.SystemAudio };
        var microphone = new CheckBox { Content = "录制麦克风", IsChecked = settings.Microphone };
        var cursor = new CheckBox { Content = "显示鼠标指针", IsChecked = settings.ShowCursor };
        var clicks = new CheckBox { Content = "显示鼠标点击效果", IsChecked = settings.ClickEffect };
        var countdown = new CheckBox { Content = "开始前倒计时 3 秒", IsChecked = settings.Countdown };

        int row = 0;
        void Add(string label, FrameworkElement control)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            control.Margin = new Thickness(0, 4, 0, 4);
            control.MinWidth = 180;
            if (label.Length > 0)
            {
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 16, 4) };
                Grid.SetRow(text, row);
                grid.Children.Add(text);
            }
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            row++;
        }
        Add("视频帧率", fps);
        Add("视频画质", quality);
        Add("GIF 默认帧率", gifFps);
        Add("GIF 默认尺寸", gifScale);
        Add("声音", systemAudio);
        Add("", microphone);
        Add("鼠标", cursor);
        Add("", clicks);
        Add("其他", countdown);

        var hint = new TextBlock
        {
            Text = "录屏热键可在「设置」页中修改；录制中再按一次热键即可停止",
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["HintTextBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            Margin = new Thickness(4, 8, 4, 8),
        };
        var ok = DialogWindow.OkButton();
        var cancel = DialogWindow.CancelButton();
        var window = DialogWindow.Create("录屏设置", new StackPanel { Children = { grid, hint } }, ok, cancel);
        window.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        window.Topmost = true;
        window.Owner = owner;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => window.Activate();
        if (window.ShowDialog() != true) return false;

        settings.Fps = Rates[fps.SelectedIndex];
        settings.Quality = quality.SelectedIndex;
        settings.GifFps = GifRates[gifFps.SelectedIndex];
        settings.GifScale = GifScales[gifScale.SelectedIndex];
        settings.SystemAudio = systemAudio.IsChecked == true;
        settings.Microphone = microphone.IsChecked == true;
        settings.ShowCursor = cursor.IsChecked == true;
        settings.ClickEffect = clicks.IsChecked == true;
        settings.Countdown = countdown.IsChecked == true;
        return true;
    }

    static ComboBox Combo(int[] values, Func<int, string> text, int current, int fallback)
    {
        var box = new ComboBox();
        foreach (var v in values) box.Items.Add(text(v));
        int index = Array.IndexOf(values, current);
        box.SelectedIndex = index >= 0 ? index : Array.IndexOf(values, fallback);
        return box;
    }
}
