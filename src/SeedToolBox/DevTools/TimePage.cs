using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SeedToolBox.ScreenTools;
using SeedToolBox.Views;

namespace SeedToolBox.DevTools;

/// <summary>Unix timestamps to dates and back, and number base conversion.</summary>
sealed class TimePage : StackPanel
{
    readonly TextBlock _now = new() { FontFamily = Ui.Mono, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
    readonly TextBox _stamp = Ui.Field(160);
    readonly ComboBox _unit = new() { Width = 110, ItemsSource = new[] { "自动识别", "秒", "毫秒" }, SelectedIndex = 0 };
    readonly TextBox _stampResult = Ui.Field(290);
    readonly TextBox _date = Ui.Field(200);
    readonly TextBox _dateResult = Ui.Field(360);
    readonly CheckBox _utc = new() { Content = "按 UTC", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBox[] _radix = { Ui.Field(420), Ui.Field(420), Ui.Field(420), Ui.Field(420) };
    static readonly int[] Bases = { 2, 8, 10, 16 };
    readonly TextBlock _status = Ui.Status();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _updating;

    public TimePage()
    {
        _stampResult.IsReadOnly = true;
        _dateResult.IsReadOnly = true;
        _date.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _stamp.Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

        Children.Add(Ui.Header("时间戳 / 进制转换", "Unix 时间戳与日期互转，二、八、十、十六进制互转"));

        Children.Add(Section("当前时间戳"));
        var copySec = Ui.Button("复制秒", () => ScreenToolService.CopyText(DateTimeOffset.Now.ToUnixTimeSeconds().ToString()));
        var copyMs = Ui.Button("复制毫秒", () => ScreenToolService.CopyText(DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString()));
        Children.Add(Ui.Row(_now, copySec, copyMs));
        _timer.Tick += (_, _) => _now.Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        _now.Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();

        Children.Add(Section("时间戳 → 日期"));
        Children.Add(Ui.Row(_stamp, Ui.Label("", 8), _unit, Ui.Label("", 8), _utc, Ui.Button("转换", StampToDate, accent: true), _stampResult));

        Children.Add(Section("日期 → 时间戳"));
        Children.Add(Ui.Row(_date, Ui.Label("", 8), Ui.Button("转换", DateToStamp, accent: true), _dateResult));
        _stamp.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) StampToDate(); };
        _date.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) DateToStamp(); };

        Children.Add(Section("进制转换（输入任意一栏，其他栏自动更新）"));
        var names = new[] { "二进制", "八进制", "十进制", "十六进制" };
        for (int i = 0; i < _radix.Length; i++)
        {
            int index = i;
            _radix[i].FontFamily = Ui.Mono;
            _radix[i].TextChanged += (_, _) => RadixChanged(index);
            var label = Ui.Label(names[i]);
            label.Width = 70;
            var copy = Ui.Button("复制", () => { if (_radix[index].Text.Length > 0) ScreenToolService.CopyText(_radix[index].Text); });
            copy.Margin = new Thickness(8, 0, 0, 0);
            Children.Add(Ui.Row(label, _radix[i], copy));
        }
        Children.Add(_status);
        StampToDate();
        DateToStamp();
    }

    static TextBlock Section(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 8),
    };

    void StampToDate()
    {
        if (!long.TryParse(_stamp.Text.Trim(), out var value)) { _stampResult.Text = "请输入整数时间戳"; return; }
        bool ms = _unit.SelectedIndex == 2 || (_unit.SelectedIndex == 0 && Math.Abs(value) >= 100_000_000_000);
        try
        {
            var time = ms ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
            time = _utc.IsChecked == true ? time.ToUniversalTime() : time.ToLocalTime();
            _stampResult.Text = time.ToString(ms ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss") + (_utc.IsChecked == true ? " UTC" : $"（{(ms ? "毫秒" : "秒")}，本地时间）");
        }
        catch (ArgumentOutOfRangeException)
        {
            _stampResult.Text = "超出可表示的时间范围";
        }
    }

    void DateToStamp()
    {
        var text = _date.Text.Trim();
        var styles = _utc.IsChecked == true ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.AssumeLocal;
        if (!DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, styles, out var time)
            && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out time))
        {
            _dateResult.Text = "无法识别的日期，例如 2026-09-23 20:30:00";
            return;
        }
        _dateResult.Text = $"{time.ToUnixTimeSeconds()} 秒    {time.ToUnixTimeMilliseconds()} 毫秒";
    }

    void RadixChanged(int index)
    {
        if (_updating) return;
        var text = _radix[index].Text.Trim().Replace("_", "").Replace(" ", "");
        bool negative = text.StartsWith("-");
        if (negative) text = text.Substring(1);
        if (Bases[index] == 16 && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        if (Bases[index] == 2 && text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);

        _updating = true;
        try
        {
            if (text.Length == 0)
            {
                for (int i = 0; i < _radix.Length; i++) if (i != index) _radix[i].Clear();
                _status.Text = "";
                return;
            }
            if (!TryParse(text, Bases[index], out var value))
            {
                Ui.SetStatus(_status, $"不是有效的{(index == 0 ? "二" : index == 1 ? "八" : index == 2 ? "十" : "十六")}进制数", true);
                return;
            }
            if (negative) value = -value;
            for (int i = 0; i < _radix.Length; i++) if (i != index) _radix[i].Text = Format(value, Bases[i]);
            _status.Text = "";
        }
        finally
        {
            _updating = false;
        }
    }

    static bool TryParse(string text, int radix, out BigInteger value)
    {
        value = BigInteger.Zero;
        foreach (var c in text)
        {
            int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 99;
            if (digit >= radix) return false;
            value = value * radix + digit;
        }
        return true;
    }

    static string Format(BigInteger value, int radix)
    {
        if (value.IsZero) return "0";
        bool negative = value.Sign < 0;
        value = BigInteger.Abs(value);
        var sb = new StringBuilder();
        while (!value.IsZero)
        {
            int digit = (int)(value % radix);
            sb.Insert(0, "0123456789ABCDEF"[digit]);
            value /= radix;
        }
        return (negative ? "-" : "") + sb;
    }
}
