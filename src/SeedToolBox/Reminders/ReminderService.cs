using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SeedToolBox.Core.Services;
using SeedToolBox.Recording;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.Reminders;

public enum ReminderRepeat { None, Daily, Weekdays, Weekly }

public sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }
    public ReminderRepeat Repeat { get; set; }

    /// <summary>The first repeat after <paramref name="after"/>.</summary>
    public static DateTime Next(DateTime due, ReminderRepeat repeat, DateTime after)
    {
        if (repeat == ReminderRepeat.None) return due;
        do
        {
            due = due.AddDays(repeat == ReminderRepeat.Weekly ? 7 : 1);
            while (repeat == ReminderRepeat.Weekdays && due.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) due = due.AddDays(1);
        } while (due <= after);
        return due;
    }

    public string RepeatText => Repeat switch
    {
        ReminderRepeat.Daily => "每天",
        ReminderRepeat.Weekdays => "工作日",
        ReminderRepeat.Weekly => "每" + "周一周二周三周四周五周六周日".Substring(((int)Due.DayOfWeek + 6) % 7 * 2, 2),
        _ => "",
    };

    /// <summary>Like "明天 09:00（15 小时后）".</summary>
    public string DueText(DateTime now)
    {
        var day = (Due.Date - now.Date).Days switch
        {
            0 => "今天",
            1 => "明天",
            2 => "后天",
            _ => Due.Year == now.Year ? Due.ToString("M月d日") : Due.ToString("yyyy年M月d日"),
        };
        var left = Due - now;
        var when = left.TotalMinutes < 1 ? $"{Math.Max(0, (int)left.TotalSeconds)} 秒后"
            : left.TotalHours < 1 ? $"{(int)Math.Ceiling(left.TotalMinutes)} 分钟后"
            : left.TotalDays < 1 ? $"{(int)left.TotalHours} 小时 {left.Minutes} 分钟后"
            : $"{(int)left.TotalDays} 天后";
        var repeat = RepeatText;
        return $"{(repeat.Length > 0 ? repeat + " " : "")}{day} {Due:HH:mm}（{when}）";
    }
}

sealed class ReminderData
{
    public List<Reminder> Items { get; set; } = new();
}

/// <summary>Keeps the reminders and shows a popup when one is due; missed ones show at the next start.</summary>
sealed class ReminderService
{
    const string SettingsName = "reminders";

    readonly ISettingsStore _store;
    readonly ReminderData _data;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<ReminderPopup> _popups = new();

    public event Action? Changed;

    public ReminderService(ISettingsStore store)
    {
        _store = store;
        _data = store.Load<ReminderData>(SettingsName);
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        // Give the app a moment to start before showing what was missed while it was closed
        Application.Current.Dispatcher.BeginInvoke(new Action(() => Check(missed: true)), DispatcherPriority.ApplicationIdle);
    }

    public IReadOnlyList<Reminder> Items => _data.Items.OrderBy(r => r.Due).ToList();

    public void Add(Reminder reminder)
    {
        _data.Items.Add(reminder);
        Save();
    }

    public void Remove(Reminder reminder)
    {
        _data.Items.RemoveAll(r => r.Id == reminder.Id);
        Save();
    }

    void Save()
    {
        _store.Save(SettingsName, _data);
        Changed?.Invoke();
    }

    void Check(bool missed = false)
    {
        var now = DateTime.Now;
        var due = _data.Items.Where(r => r.Due <= now).ToList();
        if (due.Count == 0)
        {
            // Refresh the "N 分钟后" texts about once a minute
            if (now.Second == 0) Changed?.Invoke();
            return;
        }
        foreach (var reminder in due)
        {
            bool late = missed || now - reminder.Due > TimeSpan.FromMinutes(2);
            var shown = new Reminder { Text = reminder.Text, Due = reminder.Due };
            if (reminder.Repeat == ReminderRepeat.None) _data.Items.Remove(reminder);
            else reminder.Due = Reminder.Next(reminder.Due, reminder.Repeat, now);
            ShowPopup(shown, late);
        }
        Save();
    }

    void ShowPopup(Reminder reminder, bool late)
    {
        var popup = new ReminderPopup(reminder, late, minutes =>
        {
            if (minutes > 0) Add(new Reminder { Text = reminder.Text, Due = DateTime.Now.AddMinutes(minutes) });
        });
        popup.Closed += (_, _) => { _popups.Remove(popup); Arrange(); };
        _popups.Add(popup);
        popup.Show();
        Arrange();
        PlaySound();
    }

    /// <summary>Stacks the popups up from the bottom right corner.</summary>
    void Arrange()
    {
        var area = WinForms.Screen.PrimaryScreen.WorkingArea;
        int y = area.Bottom;
        foreach (var popup in _popups)
        {
            double scale = OverlayTools.ScaleOf(popup);
            int w = (int)(popup.ActualWidth * scale), h = (int)(popup.ActualHeight * scale);
            if (w == 0) continue;
            y -= h + (int)(12 * scale);
            OverlayTools.Place(popup, area.Right - w - (int)(12 * scale), y, 0, 0);
        }
    }

    static void PlaySound()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Calendar.wav");
        try
        {
            if (File.Exists(file)) new SoundPlayer(file).Play();
            else SystemSounds.Asterisk.Play();
        }
        catch (Exception ex) { Log.Error("Failed to play the reminder sound", ex); }
    }
}

/// <summary>The card in the screen corner; it stays until dismissed and never takes focus from what the user is doing.</summary>
sealed class ReminderPopup : Window
{
    public ReminderPopup(Reminder reminder, bool late, Action<int> snooze)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Title = "提醒";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (label, minutes) in new[] { ("5 分钟后", 5), ("10 分钟后", 10), ("1 小时后", 60) })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3) };
            button.Click += (_, _) => { snooze(minutes); Close(); };
            buttons.Children.Add(button);
        }
        var ok = new Button { Content = "知道了", Style = (Style)Application.Current.FindResource("AccentButton"), Padding = new Thickness(14, 3, 14, 3) };
        ok.Click += (_, _) => Close();
        buttons.Children.Add(ok);

        var when = late ? $"错过的提醒 · {reminder.Due:M月d日 HH:mm}" : $"提醒 · {reminder.Due:HH:mm}";
        Content = new Border
        {
            Margin = new Thickness(10),
            Width = 340,
            Background = (Brush)Application.Current.FindResource("CardBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.25 },
            Child = new StackPanel
            {
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            new TextBlock { Text = "\uE823", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 14, Foreground = (Brush)Application.Current.FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center },
                            new TextBlock { Text = when, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("SecondaryTextBrush"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                    new TextBlock { Text = reminder.Text, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) },
                    buttons,
                },
            },
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) DragMove(); };
    }
}
