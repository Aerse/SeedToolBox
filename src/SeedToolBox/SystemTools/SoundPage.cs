using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.ScreenTools;
using SeedToolBox.Views;

namespace SeedToolBox.SystemTools;

/// <summary>Output and input devices, per-app volume and screen brightness on one page.</summary>
sealed class SoundPage : DockPanel
{
    readonly StackPanel _outputs = new(), _inputs = new(), _apps = new(), _screens = new();
    readonly TextBlock _status = Ui.Status();

    public SoundPage()
    {
        var header = Ui.Header("声音与亮度", "切换耳机和音箱、调每个程序的音量、调显示器亮度；外接显示器需要支持 DDC/CI");
        var refresh = Ui.Button("刷新", Refresh);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);
        top.Children.Add(_status);

        var body = new StackPanel
        {
            Children =
            {
                Section("输出设备", _outputs),
                Section("输入设备", _inputs),
                Section("程序音量（当前输出设备）", _apps),
                Section("屏幕亮度", _screens),
                new TextBlock { Text = "可以在设置页给「切换到下一个输出设备」「麦克风静音」「亮度 +/-」设快捷键", Foreground = DialogWindow.HintBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        };
        SetDock(header, Dock.Top);
        SetDock(top, Dock.Top);
        Children.Add(header);
        Children.Add(top);
        Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += (_, _) => Refresh();
    }

    static FrameworkElement Section(string caption, Panel content) => new StackPanel
    {
        Margin = new Thickness(0, 0, 0, 14),
        Children =
        {
            new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
            content,
        },
    };

    void Refresh()
    {
        try
        {
            FillDevices(_outputs, AudioDevices.Outputs(), input: false);
            FillDevices(_inputs, AudioDevices.Inputs(), input: true);
            FillApps();
            Ui.SetStatus(_status, "");
        }
        catch (COMException ex)
        {
            Log.Error("Failed to read the audio devices", ex);
            Ui.SetStatus(_status, "读取声音设备失败：" + ex.Message, true);
        }
        FillScreens(refresh: true);
    }

    void FillDevices(Panel panel, List<AudioDevice> devices, bool input)
    {
        panel.Children.Clear();
        if (devices.Count == 0) { panel.Children.Add(Hint("没有可用的设备")); return; }
        foreach (var device in devices)
        {
            FrameworkElement right;
            if (device.IsDefault)
                right = VolumeControls(() => AudioDevices.GetVolume(device.Id, input), v => AudioDevices.SetVolume(device.Id, v, input),
                    () => AudioDevices.GetMute(device.Id, input), m => AudioDevices.SetMute(device.Id, m, input), input ? "静音麦克风" : "静音");
            else
                right = Ui.Button("设为默认", () =>
                {
                    try { AudioDevices.SetDefault(device.Id); }
                    catch (COMException ex) { Log.Error("Failed to set the default audio device", ex); Ui.SetStatus(_status, "切换失败：" + ex.Message, true); return; }
                    Refresh();
                });
            panel.Children.Add(Card(device.Name, device.IsDefault ? "默认" : "", right));
        }
    }

    void FillApps()
    {
        _apps.Children.Clear();
        var sessions = AudioDevices.Sessions();
        if (sessions.Count == 0) { _apps.Children.Add(Hint("现在没有程序在用这个设备")); return; }
        foreach (var session in sessions)
        {
            var controls = VolumeControls(() => session.Volume, v => AudioDevices.SetSessionVolume(session, v),
                () => session.Muted, m => AudioDevices.SetSessionMute(session, m), "静音");
            _apps.Children.Add(Card(session.Name, "", controls));
        }
    }

    async void FillScreens(bool refresh)
    {
        _screens.Children.Clear();
        _screens.Children.Add(Hint("正在读取显示器…"));
        List<Display> displays;
        try { displays = await Task.Run(() => Brightness.Displays(refresh)); }
        catch (Exception ex) { Log.Error("Failed to read the displays", ex); displays = new(); }
        _screens.Children.Clear();
        if (displays.Count == 0) { _screens.Children.Add(Hint("没有能调亮度的屏幕（外接显示器要在显示器菜单里打开 DDC/CI）")); return; }
        foreach (var display in displays)
        {
            var value = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = display.Brightness + "%" };
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = display.Brightness, Width = 200, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
            int pending = -1;
            bool busy = false;
            async void Apply()
            {
                // DDC/CI is slow; send only the latest value, one at a time
                if (busy) return;
                busy = true;
                while (pending >= 0)
                {
                    int level = pending;
                    pending = -1;
                    try { if (!await Task.Run(() => Brightness.Set(display, level))) Ui.SetStatus(_status, "调亮度失败：显示器没有响应，点刷新重新读取", true); }
                    catch (Exception ex) { Log.Error("Failed to set the brightness", ex); Ui.SetStatus(_status, "调亮度失败：" + ex.Message, true); }
                }
                busy = false;
            }
            slider.ValueChanged += (_, _) => { value.Text = (int)slider.Value + "%"; pending = (int)slider.Value; Apply(); };
            _screens.Children.Add(Card(display.Name, "", new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, value } }));
        }
    }

    static FrameworkElement VolumeControls(Func<float> get, Action<float> set, Func<bool> muted, Action<bool> setMuted, string muteText)
    {
        var value = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 100, Width = 200, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
        slider.Value = Math.Round(get() * 100);
        value.Text = (int)slider.Value + "%";
        slider.ValueChanged += (_, _) =>
        {
            value.Text = (int)slider.Value + "%";
            try { set((float)(slider.Value / 100)); }
            catch (COMException ex) { Log.Error("Failed to set the volume", ex); }
        };
        var mute = new CheckBox { Content = muteText, IsChecked = muted(), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mute.Click += (_, _) =>
        {
            try { setMuted(mute.IsChecked == true); }
            catch (COMException ex) { Log.Error("Failed to mute", ex); }
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, value, mute } };
    }

    static TextBlock Hint(string text) => new() { Text = text, Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 2, 0, 0) };

    static Border Card(string title, string tag, FrameworkElement right)
    {
        right.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (tag.Length > 0)
            name.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["AccentBrush"],
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = tag, FontSize = 11, Foreground = Brushes.White },
            });
        row.Children.Add(name);
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = row,
        };
    }
}

/// <summary>What the hotkeys do; each shows a toast so the user sees the result.</summary>
static class SoundHotkeys
{
    public static void NextOutput()
    {
        try
        {
            var name = AudioDevices.NextOutput();
            Toast.Show(name == null ? "只有一个输出设备" : name.Length == 0 ? "没有输出设备" : "输出：" + name);
        }
        catch (COMException ex) { Log.Error("Failed to switch the output device", ex); Toast.Show("切换输出设备失败"); }
    }

    public static void ToggleMicrophone()
    {
        try
        {
            bool muted = !AudioDevices.GetMute(null, input: true);
            AudioDevices.SetMute(null, muted, input: true);
            Toast.Show(muted ? "麦克风已静音" : "麦克风已打开");
        }
        catch (COMException ex) { Log.Error("Failed to mute the microphone", ex); Toast.Show("没有麦克风"); }
    }

    public static async void ChangeBrightness(int delta)
    {
        int? level = null;
        try { level = await Task.Run(() => Brightness.Change(delta)); }
        catch (Exception ex) { Log.Error("Failed to change the brightness", ex); }
        Toast.Show(level is { } l ? $"亮度 {l}%" : "没有能调亮度的屏幕", 1.5);
    }
}
