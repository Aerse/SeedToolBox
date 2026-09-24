using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.Reminders;

sealed class RemindersPage : DockPanel
{
    readonly ReminderService _service;
    readonly TextBox _input = Ui.Field();
    readonly TextBlock _preview = Ui.Status();
    readonly StackPanel _list = new();

    public RemindersPage(ReminderService service)
    {
        _service = service;
        var header = Ui.Header("提醒", "到点在屏幕右下角弹出提醒；也可以直接在启动器里输入，如「10分钟后 喝水」");

        var add = Ui.Button("添加", Add, accent: true);
        add.Margin = new Thickness(8, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        row.Children.Add(_input);
        _input.TextChanged += (_, _) => Preview();
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        _preview.Margin = new Thickness(0, 6, 0, 0);

        var examples = new TextBlock
        {
            Text = "例：25分钟后 休息一下　1小时30分后 开会　下午3点半 打电话　明天9点 交报告　周五 18:00 写周报　5月20日 纪念日　每天 8:30 打卡　工作日 9点 站会",
            Foreground = DialogWindow.HintBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16),
        };

        var top = new StackPanel { Children = { row, _preview, examples, Caption("待提醒") } };
        SetDock(header, Dock.Top);
        SetDock(top, Dock.Top);
        Children.Add(header);
        Children.Add(top);
        Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Preview();
        Loaded += (_, _) => { _service.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => _service.Changed -= Refresh;
    }

    static TextBlock Caption(string text) =>
        new() { Text = text, Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"], Margin = new Thickness(0, 0, 0, 6) };

    void Preview()
    {
        if (_input.Text.Trim().Length == 0) Ui.SetStatus(_preview, "输入时间和内容");
        else if (ReminderParser.TryParse(_input.Text, DateTime.Now, out var r)) Ui.SetStatus(_preview, $"「{r.Text}」{r.DueText(DateTime.Now)}");
        else Ui.SetStatus(_preview, "看不懂这个时间，试试「10分钟后 …」或「明天9点 …」", true);
    }

    void Add()
    {
        if (!ReminderParser.TryParse(_input.Text, DateTime.Now, out var reminder)) { Preview(); return; }
        _service.Add(reminder);
        _input.Clear();
        Ui.SetStatus(_preview, $"已添加：「{reminder.Text}」{reminder.DueText(DateTime.Now)}");
    }

    void Refresh()
    {
        _list.Children.Clear();
        var now = DateTime.Now;
        var items = _service.Items;
        if (items.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "还没有提醒", Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 4, 0, 0) });
            return;
        }
        foreach (var reminder in items)
        {
            var delete = new Button { Content = "\uE74D", Style = (Style)Application.Current.FindResource("IconButton"), ToolTip = "删除", VerticalAlignment = VerticalAlignment.Center };
            delete.Click += (_, _) => _service.Remove(reminder);
            var card = new DockPanel();
            DockPanel.SetDock(delete, Dock.Right);
            card.Children.Add(delete);
            card.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = reminder.Text, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = reminder.DueText(now), FontSize = 12, Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 2, 0, 0) },
                },
            });
            _list.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["CardBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 8, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = card,
            });
        }
    }
}
