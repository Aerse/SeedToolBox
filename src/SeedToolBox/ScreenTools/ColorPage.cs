using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeedToolBox.DevTools;
using SeedToolBox.Views;

namespace SeedToolBox.ScreenTools;

/// <summary>Toolbox page: colour conversion, recent picks, favourites and a WCAG contrast check.</summary>
sealed class ColorPage : ScrollViewer
{
    readonly ScreenToolService _service;
    readonly TextBox _input = Ui.Field(220);
    readonly Border _swatch = Swatch(64);
    readonly TextBlock _status = Ui.Status();
    readonly StackPanel _formats = new();
    readonly Button _favorite;
    readonly WrapPanel _history = new(), _favorites = new();
    readonly TextBox _foreground = Ui.Field(120), _background = Ui.Field(120);
    readonly TextBlock _sample = new() { Text = "示例文字 Sample Aa", FontSize = 18, Padding = new Thickness(14, 10, 14, 10) };
    readonly Border _sampleBox = new() { CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _contrast = new() { TextWrapping = TextWrapping.Wrap };
    Color _color = Color.FromRgb(0x33, 0x99, 0xFF);

    public ColorPage(ScreenToolService service)
    {
        _service = service;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var panel = new StackPanel();
        panel.Children.Add(Ui.Header("颜色", "HEX / RGB / HSL 转换、取色记录、收藏和对比度检查"));

        _input.Text = ColorTools.Hex(_color);
        _input.TextChanged += (_, _) =>
        {
            if (ColorTools.TryParse(_input.Text, out var c)) { _color = c; Update(false); }
            else Ui.SetStatus(_status, "无法识别，支持 #RRGGBB、rgb(r, g, b)、hsl(h, s%, l%)", true);
        };
        _favorite = Ui.Button("收藏", () => { _service.SetFavorite(_color, !_service.IsFavorite(_color)); });
        panel.Children.Add(Ui.Row(Ui.Label("颜色"), _input,
            Ui.Button("屏幕取色", () => _service.PickColor()),
            _favorite));
        _swatch.Margin = new Thickness(0, 0, 16, 0);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _swatch, _formats } });
        panel.Children.Add(_status);

        panel.Children.Add(Section("最近取色", Ui.Button("清空", () => _service.ClearColorHistory())));
        panel.Children.Add(_history);
        panel.Children.Add(Section("收藏"));
        panel.Children.Add(_favorites);

        panel.Children.Add(Section("对比度检查 (WCAG 2)"));
        _foreground.Text = "#FFFFFF";
        _background.Text = ColorTools.Hex(_color);
        _foreground.TextChanged += (_, _) => UpdateContrast();
        _background.TextChanged += (_, _) => UpdateContrast();
        panel.Children.Add(Ui.Row(Ui.Label("文字"), _foreground, Ui.Label("  背景"), _background,
            Ui.Button("⇄", () => (_foreground.Text, _background.Text) = (_background.Text, _foreground.Text)),
            Ui.Button("用当前颜色", () => _background.Text = ColorTools.Hex(_color))));
        _sampleBox.Child = _sample;
        panel.Children.Add(_sampleBox);
        panel.Children.Add(_contrast);

        Content = panel;
        _service.ColorsChanged += () =>
        {
            RefreshLists();
            UpdateFavoriteButton();
        };
        RefreshLists();
        Update(true);
        UpdateContrast();
    }

    static TextBlock Section(string title) => new() { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8) };

    static FrameworkElement Section(string title, Button action)
    {
        action.Padding = new Thickness(8, 1, 8, 1);
        action.Margin = new Thickness(12, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 8) };
        var text = Section(title);
        text.Margin = new Thickness(0);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        row.Children.Add(action);
        return row;
    }

    static Border Swatch(double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
    };

    void Update(bool setText)
    {
        if (setText) _input.Text = ColorTools.Hex(_color);
        _swatch.Background = new SolidColorBrush(_color);
        Ui.SetStatus(_status, "");
        _formats.Children.Clear();
        var (h, s, l) = ColorTools.ToHsl(_color);
        foreach (var text in new[]
        {
            ColorTools.Hex(_color),
            ColorTools.Rgb(_color),
            ColorTools.Hsl(_color),
            $"{_color.R}, {_color.G}, {_color.B}",
            $"0x{_color.R:X2}{_color.G:X2}{_color.B:X2}",
        })
        {
            var value = new TextBlock { Text = text, FontFamily = Ui.Mono, VerticalAlignment = VerticalAlignment.Center, MinWidth = 200 };
            var copy = Ui.Button("复制", () => { ScreenToolService.CopyText(text); Ui.SetStatus(_status, $"已复制 {text}"); });
            copy.MinWidth = 0;
            copy.Padding = new Thickness(8, 0, 8, 0);
            _formats.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4), Children = { value, copy } });
        }
        UpdateFavoriteButton();
    }

    void UpdateFavoriteButton() => _favorite.Content = _service.IsFavorite(_color) ? "取消收藏" : "收藏";

    void RefreshLists()
    {
        Fill(_history, _service.Settings.ColorHistory, "还没有取色记录，用屏幕取色或快捷键取色后会出现在这里");
        Fill(_favorites, _service.Settings.FavoriteColors, "点「收藏」把当前颜色加入这里，右键色块移除");
    }

    void Fill(WrapPanel target, List<string> colors, string empty)
    {
        target.Children.Clear();
        foreach (var hex in colors)
        {
            if (!ColorTools.TryParse(hex, out var c)) continue;
            var swatch = Swatch(32);
            swatch.Background = new SolidColorBrush(c);
            swatch.Margin = new Thickness(0, 0, 6, 6);
            swatch.Cursor = Cursors.Hand;
            swatch.ToolTip = $"{hex}\n单击选中 · 双击复制" + (target == _favorites ? " · 右键移除" : "");
            swatch.MouseLeftButtonUp += (_, e) =>
            {
                _color = c;
                Update(true);
            };
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                ScreenToolService.CopyText(hex);
                Ui.SetStatus(_status, $"已复制 {hex}");
            };
            if (target == _favorites) swatch.MouseRightButtonUp += (_, _) => _service.SetFavorite(c, false);
            target.Children.Add(swatch);
        }
        if (target.Children.Count == 0)
            target.Children.Add(new TextBlock { Text = empty, Foreground = DialogWindow.HintBrush });
    }

    void UpdateContrast()
    {
        if (!ColorTools.TryParse(_foreground.Text, out var fg) || !ColorTools.TryParse(_background.Text, out var bg))
        {
            _contrast.Text = "请输入两个有效颜色";
            return;
        }
        _sample.Foreground = new SolidColorBrush(fg);
        _sampleBox.Background = new SolidColorBrush(bg);
        double ratio = ColorTools.Contrast(fg, bg);
        static string Mark(bool ok) => ok ? "通过" : "不通过";
        _contrast.Text = $"对比度 {ratio:0.00} : 1\n" +
            $"AA 正文 (≥ 4.5)：{Mark(ratio >= 4.5)}    AA 大字 (≥ 3)：{Mark(ratio >= 3)}\n" +
            $"AAA 正文 (≥ 7)：{Mark(ratio >= 7)}    AAA 大字 (≥ 4.5)：{Mark(ratio >= 4.5)}";
    }
}
