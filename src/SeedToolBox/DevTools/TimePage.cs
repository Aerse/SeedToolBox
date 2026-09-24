using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SeedToolBox.ScreenTools;
using SeedToolBox.Views;

namespace SeedToolBox.DevTools;

/// <summary>Unix timestamps and dates in any time zone, date arithmetic, and number base / bit layout conversion.</summary>
sealed class TimePage : DockPanel
{
    readonly TextBlock _now = new() { FontFamily = Ui.Mono, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
    readonly TextBox _stamp = Ui.Field(160);
    readonly ComboBox _unit = new() { Width = 110, ItemsSource = new[] { "自动识别", "秒", "毫秒" }, SelectedIndex = 0 };
    readonly TextBox _stampResult = Ui.Field(290);
    readonly TextBox _date = Ui.Field(200);
    readonly TextBox _dateResult = Ui.Field(360);
    readonly ComboBox _zone = new() { Width = 360, DisplayMemberPath = nameof(TimeZoneInfo.DisplayName) };
    readonly TextBox _formats = Ui.Area();

    readonly TextBox _base = Ui.Field(200);
    readonly TextBox[] _amounts = { Ui.Field(56), Ui.Field(56), Ui.Field(56), Ui.Field(56), Ui.Field(56), Ui.Field(56) };
    readonly TextBox _addResult = Ui.Field(360);
    readonly TextBox _start = Ui.Field(200);
    readonly TextBox _end = Ui.Field(200);
    readonly TextBox _duration = Ui.Area();

    readonly TextBox[] _radix = { Ui.Field(420), Ui.Field(420), Ui.Field(420), Ui.Field(420) };
    static readonly int[] Bases = { 2, 8, 10, 16 };
    readonly ComboBox _width = new() { Width = 90, ItemsSource = new[] { "8 位", "16 位", "32 位", "64 位" }, SelectedIndex = 2 };
    readonly TextBox _bits = Ui.Area();
    readonly TextBox _float = Ui.Field(200);
    readonly ComboBox _floatKind = new() { Width = 130, ItemsSource = new[] { "float (32 位)", "double (64 位)" }, SelectedIndex = 1 };
    readonly TextBox _floatResult = Ui.Area();

    readonly TextBlock _status = Ui.Status();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    BigInteger? _value;
    bool _updating;

    public TimePage()
    {
        foreach (var box in new[] { _stampResult, _dateResult, _formats, _addResult, _duration, _bits, _floatResult }) box.IsReadOnly = true;
        _date.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _stamp.Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        _base.Text = _start.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _end.Text = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var a in _amounts) a.Text = "0";
        _amounts[2].Text = "7";

        var zones = TimeZoneInfo.GetSystemTimeZones().ToList();
        _zone.ItemsSource = zones;
        _zone.SelectedIndex = Math.Max(0, zones.FindIndex(z => z.Id == TimeZoneInfo.Local.Id));
        _zone.SelectionChanged += (_, _) => { StampToDate(); DateToStamp(); };

        var header = Ui.Header("时间戳 / 进制转换", "Unix 时间戳与日期互转、时区、日期计算，二、八、十、十六进制互转与位视图、IEEE-754 浮点分解");
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "时间戳", Content = Scroll(BuildStampTab()) });
        tabs.Items.Add(new TabItem { Header = "日期计算", Content = Scroll(BuildDateTab()) });
        tabs.Items.Add(new TabItem { Header = "进制与位", Content = Scroll(BuildRadixTab()) });

        SetDock(header, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(_status);
        Children.Add(tabs);

        _timer.Tick += (_, _) => _now.Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        _now.Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        StampToDate();
        DateToStamp();
        AddToDate();
        Duration();
    }

    static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 8, 8, 0),
    };

    StackPanel BuildStampTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Section("当前时间戳"));
        var copySec = Ui.Button("复制秒", () => ScreenToolService.CopyText(DateTimeOffset.Now.ToUnixTimeSeconds().ToString()));
        var copyMs = Ui.Button("复制毫秒", () => ScreenToolService.CopyText(DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString()));
        var now = Ui.Button("用当前时间", () => { _stamp.Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(); StampToDate(); });
        panel.Children.Add(Ui.Row(_now, copySec, copyMs, now));

        panel.Children.Add(Ui.Row(Ui.Label("时区"), _zone));

        panel.Children.Add(Section("时间戳 → 日期"));
        panel.Children.Add(Ui.Row(_stamp, Ui.Label("", 8), _unit, Ui.Label("", 8), Ui.Button("转换", StampToDate, accent: true), _stampResult));

        panel.Children.Add(Section("日期 → 时间戳（未写时区时按上面选的时区）"));
        panel.Children.Add(Ui.Row(_date, Ui.Label("", 8), Ui.Button("转换", DateToStamp, accent: true), _dateResult));
        _stamp.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) StampToDate(); };
        _date.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) DateToStamp(); };

        panel.Children.Add(Section("各种格式"));
        _formats.Height = 290;
        var copy = Ui.CopyButton(() => _formats.SelectionLength > 0 ? _formats.SelectedText : _formats.Text, "复制");
        copy.Margin = new Thickness(0, 8, 0, 0);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(_formats);
        panel.Children.Add(copy);
        return panel;
    }

    StackPanel BuildDateTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Section("日期加减（负数为减）"));
        panel.Children.Add(Ui.Row(Ui.Label("起始"), _base, Ui.Label("", 8), Ui.Button("现在", () => { _base.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); AddToDate(); })));
        var units = new[] { "年", "月", "日", "时", "分", "秒" };
        var row = Ui.Row();
        for (int i = 0; i < _amounts.Length; i++)
        {
            _amounts[i].TextChanged += (_, _) => AddToDate();
            row.Children.Add(_amounts[i]);
            row.Children.Add(Ui.Label(units[i], 12));
        }
        panel.Children.Add(row);
        _base.TextChanged += (_, _) => AddToDate();
        panel.Children.Add(Ui.Row(Ui.Label("结果"), _addResult));

        panel.Children.Add(Section("两个日期之间的时长"));
        panel.Children.Add(Ui.Row(Ui.Label("开始"), _start, Ui.Label("", 16), Ui.Label("结束"), _end, Ui.Label("", 8),
            Ui.Button("交换", () => (_start.Text, _end.Text) = (_end.Text, _start.Text))));
        _start.TextChanged += (_, _) => Duration();
        _end.TextChanged += (_, _) => Duration();
        _duration.Height = 170;
        panel.Children.Add(_duration);
        return panel;
    }

    StackPanel BuildRadixTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Section("进制转换（输入任意一栏，其他栏自动更新）"));
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
            panel.Children.Add(Ui.Row(label, _radix[i], copy));
        }

        panel.Children.Add(Section("位视图与补码"));
        panel.Children.Add(Ui.Row(Ui.Label("位宽"), _width));
        _width.SelectionChanged += (_, _) => ShowBits();
        _bits.Height = 190;
        panel.Children.Add(_bits);

        panel.Children.Add(Section("IEEE-754 浮点分解"));
        _float.FontFamily = Ui.Mono;
        _float.ToolTip = "输入小数，如 0.1、-2.5e10、NaN、Infinity；或 0x 开头的位模式，如 0x3DCCCCCD";
        _float.Text = "0.1";
        _float.TextChanged += (_, _) => ShowFloat();
        _floatKind.SelectionChanged += (_, _) => ShowFloat();
        panel.Children.Add(Ui.Row(Ui.Label("数值"), _float, Ui.Label("", 8), _floatKind));
        _floatResult.Height = 200;
        panel.Children.Add(_floatResult);
        ShowFloat();
        return panel;
    }

    static TextBlock Section(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 8),
    };

    TimeZoneInfo Zone => _zone.SelectedItem as TimeZoneInfo ?? TimeZoneInfo.Local;

    // Timestamps

    void StampToDate()
    {
        if (!long.TryParse(_stamp.Text.Trim(), out var value)) { _stampResult.Text = "请输入整数时间戳"; return; }
        bool ms = _unit.SelectedIndex == 2 || (_unit.SelectedIndex == 0 && Math.Abs(value) >= 100_000_000_000);
        try
        {
            var utc = ms ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
            var time = TimeZoneInfo.ConvertTime(utc, Zone);
            _stampResult.Text = time.ToString(ms ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss") + $"（{(ms ? "毫秒" : "秒")}，UTC{Offset(time.Offset)}）";
            ShowFormats(time);
        }
        catch (ArgumentOutOfRangeException)
        {
            _stampResult.Text = "超出可表示的时间范围";
        }
    }

    void DateToStamp()
    {
        if (ParseDate(_date.Text) is not { } time)
        {
            _dateResult.Text = "无法识别的日期，例如 2026-09-23 20:30:00";
            return;
        }
        _dateResult.Text = $"{time.ToUnixTimeSeconds()} 秒    {time.ToUnixTimeMilliseconds()} 毫秒";
        ShowFormats(TimeZoneInfo.ConvertTime(time, Zone));
    }

    /// <summary>Parses a date; one without an offset or Z is taken as a time in the chosen zone.</summary>
    DateTimeOffset? ParseDate(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return null;
        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (!DateTime.TryParse(text, culture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var dt)) continue;
            try
            {
                if (dt.Kind != DateTimeKind.Unspecified && DateTimeOffset.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out var explicitTime)) return explicitTime;
                return new DateTimeOffset(dt, Zone.GetUtcOffset(dt));
            }
            catch (ArgumentException) { return null; }
        }
        return null;
    }

    static string Offset(TimeSpan offset) => (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm");

    void ShowFormats(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        var zh = CultureInfo.GetCultureInfo("zh-CN");
        var week = CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(time.DateTime, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        var days = (time - DateTimeOffset.Now).TotalDays;
        var relative = Math.Abs(days) < 1 ? $"{Math.Abs((time - DateTimeOffset.Now).TotalHours):0.#} 小时{(days < 0 ? "前" : "后")}" : $"{Math.Abs(days):0.#} 天{(days < 0 ? "前" : "后")}";
        var lines = new (string, string)[]
        {
            ("本地格式", time.ToString("yyyy-MM-dd HH:mm:ss")),
            ("ISO 8601", time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)),
            ("ISO 8601 UTC", utc.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
            ("ISO 8601 基本", time.ToString("yyyyMMdd'T'HHmmsszzz", CultureInfo.InvariantCulture).Replace(":", "")),
            ("RFC 3339", time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)),
            ("RFC 1123 / HTTP", utc.ToString("R", CultureInfo.InvariantCulture)),
            ("RFC 2822 / 邮件", time.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + time.ToString("zzz").Replace(":", "")),
            ("中文", time.ToString("yyyy年M月d日 dddd HH:mm:ss", zh)),
            (".NET 往返 (o)", time.ToString("o", CultureInfo.InvariantCulture)),
            ("Unix 秒", time.ToUnixTimeSeconds().ToString()),
            ("Unix 毫秒", time.ToUnixTimeMilliseconds().ToString()),
            (".NET Ticks", utc.Ticks.ToString()),
            ("Windows FILETIME", SafeFileTime(time)),
            ("星期 / 周数", $"{time.ToString("dddd", zh)}，ISO 第 {week} 周，一年中第 {time.DayOfYear} 天"),
            ("时区", $"{Zone.DisplayName}，UTC{Offset(time.Offset)}{(Zone.IsDaylightSavingTime(time) ? "（夏令时）" : "")}"),
            ("距离现在", relative),
        };
        _formats.Text = string.Join("\n", lines.Select(l => $"{l.Item1.PadRight(16 - l.Item1.Count(c => c > 127))}{l.Item2}"));
    }

    static string SafeFileTime(DateTimeOffset time)
    {
        try { return time.ToFileTime().ToString(); }
        catch (ArgumentOutOfRangeException) { return "（超出范围）"; }
    }

    // Date arithmetic

    void AddToDate()
    {
        if (ParseDate(_base.Text) is not { } start) { _addResult.Text = "无法识别的起始日期"; return; }
        var n = new int[6];
        for (int i = 0; i < 6; i++)
            if (!int.TryParse(_amounts[i].Text.Trim().Length == 0 ? "0" : _amounts[i].Text.Trim(), out n[i])) { _addResult.Text = "请输入整数"; return; }
        try
        {
            var result = start.DateTime.AddYears(n[0]).AddMonths(n[1]).AddDays(n[2]).AddHours(n[3]).AddMinutes(n[4]).AddSeconds(n[5]);
            _addResult.Text = result.ToString("yyyy-MM-dd HH:mm:ss dddd", CultureInfo.GetCultureInfo("zh-CN"));
        }
        catch (ArgumentOutOfRangeException) { _addResult.Text = "超出可表示的日期范围"; }
    }

    void Duration()
    {
        if (ParseDate(_start.Text) is not { } a || ParseDate(_end.Text) is not { } b) { _duration.Text = "无法识别的日期"; return; }
        var span = b - a;
        var sign = span < TimeSpan.Zero ? "-" : "";
        var abs = span.Duration();
        var (from, to) = a <= b ? (a.DateTime, b.DateTime) : (b.DateTime, a.DateTime);
        int months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        if (from.AddMonths(months) > to) months--;
        var rest = to - from.AddMonths(months);
        var sb = new StringBuilder();
        sb.Append($"相差        {sign}{(int)abs.TotalDays} 天 {abs.Hours} 小时 {abs.Minutes} 分 {abs.Seconds} 秒\n");
        sb.Append($"按年月      {sign}{months / 12} 年 {months % 12} 个月 {(int)rest.TotalDays} 天 {rest.Hours} 小时 {rest.Minutes} 分 {rest.Seconds} 秒\n");
        sb.Append($"总周数      {sign}{abs.TotalDays / 7:0.##} 周\n");
        sb.Append($"总天数      {sign}{abs.TotalDays:0.####} 天\n");
        sb.Append($"总小时      {sign}{abs.TotalHours:0.##} 小时\n");
        sb.Append($"总分钟      {sign}{abs.TotalMinutes:0.##} 分\n");
        sb.Append($"总秒数      {sign}{abs.TotalSeconds:0.###} 秒\n");
        sb.Append($"工作日      {sign}{WorkDays(from.Date, to.Date)} 天（周一至周五，不含结束日，不计节假日）");
        _duration.Text = sb.ToString();
    }

    static int WorkDays(DateTime from, DateTime to)
    {
        int total = (int)(to - from).TotalDays, weeks = total / 7, count = weeks * 5;
        for (var d = from.AddDays(weeks * 7); d < to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) count++;
        return count;
    }

    // Radix

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
                _value = null;
                ShowBits();
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
            _value = value;
            ShowBits();
        }
        finally
        {
            _updating = false;
        }
    }

    int BitWidth => 8 << _width.SelectedIndex;

    void ShowBits()
    {
        if (_value is not { } value) { _bits.Text = ""; return; }
        int width = BitWidth;
        var modulus = BigInteger.One << width;
        var sb = new StringBuilder();
        if (value >= modulus || value < -(modulus >> 1))
        {
            _bits.Text = $"{value} 超出 {width} 位能表示的范围（有符号 {-(modulus >> 1)} ~ {(modulus >> 1) - 1}，无符号 0 ~ {modulus - 1}）";
            return;
        }
        var raw = value < 0 ? value + modulus : value;
        var signed = raw >= modulus >> 1 ? raw - modulus : raw;
        var binary = Format(raw, 2).PadLeft(width, '0');
        sb.Append($"补码（{width} 位）  0x{Format(raw, 16).PadLeft(width / 4, '0')}\n");
        sb.Append($"有符号        {signed}\n");
        sb.Append($"无符号        {raw}\n");
        sb.Append($"置位个数      {binary.Count(c => c == '1')}\n\n");
        // Bits from the most significant byte, 8 per line group, with the index of each byte's top bit
        for (int i = 0; i < width; i += 8)
        {
            var bits = binary.Substring(i, 8);
            sb.Append($"位 {width - 1 - i,2}-{width - 8 - i,2}   {bits.Substring(0, 4)} {bits.Substring(4)}   0x{Convert.ToByte(bits, 2):X2}\n");
        }
        if (width == 32) sb.Append($"\n按 float 解释  {BitConverter.ToSingle(BitConverter.GetBytes((uint)raw), 0).ToString("R", CultureInfo.InvariantCulture)}\n");
        if (width == 64) sb.Append($"\n按 double 解释 {BitConverter.Int64BitsToDouble((long)(ulong)raw).ToString("R", CultureInfo.InvariantCulture)}\n");
        _bits.Text = sb.ToString();
    }

    void ShowFloat()
    {
        var text = _float.Text.Trim();
        bool single = _floatKind.SelectedIndex == 0;
        ulong bits;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(text.Substring(2).Replace("_", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bits) || (single && bits > uint.MaxValue))
            { _floatResult.Text = "无效的位模式"; return; }
        }
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || TryNamed(text, out d))
            bits = single ? BitConverter.ToUInt32(BitConverter.GetBytes((float)d), 0) : (ulong)BitConverter.DoubleToInt64Bits(d);
        else { _floatResult.Text = text.Length == 0 ? "" : "请输入数字，如 0.1、-2.5e10、NaN"; return; }

        int expBits = single ? 8 : 11, fracBits = single ? 23 : 52, total = expBits + fracBits + 1, bias = single ? 127 : 1023;
        var binary = Convert.ToString((long)bits, 2).PadLeft(total, '0');
        if (binary.Length > total) binary = binary.Substring(binary.Length - total);
        var sign = binary.Substring(0, 1);
        var exponent = binary.Substring(1, expBits);
        var fraction = binary.Substring(1 + expBits);
        long e = Convert.ToInt64(exponent, 2);
        bool fracZero = fraction.All(c => c == '0');
        string kind = e == 0 ? (fracZero ? "零" : "非规格化数") : e == (1 << expBits) - 1 ? (fracZero ? "无穷大" : "NaN") : "规格化数";
        double value = single ? BitConverter.ToSingle(BitConverter.GetBytes((uint)bits), 0) : BitConverter.Int64BitsToDouble((long)bits);

        var sb = new StringBuilder();
        sb.Append($"实际存储值    {value.ToString(single ? "G9" : "G17", CultureInfo.InvariantCulture)}\n");
        sb.Append($"十六进制      0x{bits.ToString(single ? "X8" : "X16")}\n");
        sb.Append($"二进制        {sign} {exponent} {fraction}\n");
        sb.Append($"符号位        {sign}（{(sign == "1" ? "负" : "正")}）\n");
        sb.Append($"指数          {exponent} = {e}，偏移 {bias}，实际指数 {(e == 0 ? 1 - bias : e - bias)}\n");
        sb.Append($"尾数          {fraction}\n");
        sb.Append($"类型          {kind}\n");
        if (kind is "规格化数" or "非规格化数")
            sb.Append($"公式          (-1)^{sign} × {(e == 0 ? "0" : "1")}.尾数 × 2^{(e == 0 ? 1 - bias : e - bias)}");
        _floatResult.Text = sb.ToString();
    }

    static bool TryNamed(string text, out double value)
    {
        value = text.ToLowerInvariant() switch
        {
            "nan" => double.NaN,
            "inf" or "infinity" or "+inf" or "+infinity" or "∞" => double.PositiveInfinity,
            "-inf" or "-infinity" or "-∞" => double.NegativeInfinity,
            _ => 0,
        };
        return value != 0 || double.IsNaN(value);
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
