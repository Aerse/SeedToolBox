using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.Ai.Automation;

/// <summary>What the automation mode changed, newest first, with undo.</summary>
sealed class OperationLogPage : DockPanel
{
    readonly OperationLog _log;
    readonly StackPanel _list = new();
    readonly TextBlock _status = Ui.Status();

    public OperationLogPage(OperationLog log)
    {
        _log = log;
        var header = Ui.Header("AI 操作记录", "AI 自动化模式做过的每个改动；移动、复制、新建、写入文件和添加的提醒、笔记都可以撤销，删除的文件在回收站里");
        var clear = Ui.Button("清空记录", () =>
        {
            if (_log.Items.Count == 0) return;
            if (MessageBox.Show(Window.GetWindow(this), "清空后就不能再撤销这些改动了。确定清空吗？", "AI 操作记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _log.Clear();
        });
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(clear, Dock.Right);
        top.Children.Add(clear);
        top.Children.Add(_status);
        SetDock(header, Dock.Top);
        SetDock(top, Dock.Top);
        Children.Add(header);
        Children.Add(top);
        Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += (_, _) => { _log.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => _log.Changed -= Refresh;
    }

    void Refresh()
    {
        _list.Children.Clear();
        if (_log.Items.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "还没有记录", Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 4, 0, 0) });
            return;
        }
        foreach (var operation in _log.Items.Take(200))
        {
            var item = operation;
            FrameworkElement right;
            if (item.CanUndo)
                right = Ui.Button("撤销", () =>
                {
                    var changed = OperationLog.ChangedSince(item);
                    if (changed.Count > 0 && MessageBox.Show(Window.GetWindow(this),
                            "这些文件在那之后又被改过：\n" + string.Join("\n", changed.Take(10)) + (changed.Count > 10 ? $"\n……共 {changed.Count} 个" : "") +
                            "\n\n撤销时现在的内容会移到回收站。继续吗？", "AI 操作记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    var problem = _log.Undo(item);
                    Ui.SetStatus(_status, problem.Length == 0 ? "已撤销：" + item.Summary : "有些没有撤销：\n" + problem, problem.Length > 0);
                });
            else
                right = new TextBlock { Text = item.Undone ? "已撤销" : "", Foreground = DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center };
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(8, 0, 0, 0);
            var card = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            card.Children.Add(right);
            var detail = item.Kind == OperationKind.Move || item.Kind == OperationKind.Copy
                ? string.Join("\n", item.Items.Take(8).Select(p => item.Kind == OperationKind.Move ? $"{p[0]} → {p[1]}" : $"{p[1]} → {p[0]}")) + (item.Items.Count > 8 ? $"\n……共 {item.Items.Count} 项" : "")
                : "";
            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = item.Summary, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = item.Undone ? 0.55 : 1 },
                    new TextBlock { Text = item.Time.ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 12, Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 2, 0, 0) },
                },
            };
            if (detail.Length > 0) text.ToolTip = detail;
            card.Children.Add(text);
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
