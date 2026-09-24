using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SeedToolBox.Reminders;

/// <summary>
/// Reads reminders typed in plain Chinese: "10分钟后 喝水", "1小时30分后 开会", "明天9点 交报告", "下午3点半 打电话",
/// "周五 18:00 周报", "每天 8:30 打卡", "工作日 9点 站会". A leading "提醒" or "tx " is allowed.
/// </summary>
static class ReminderParser
{
    const string Num = @"(?:\d+(?:\.\d+)?|半|[零一二两三四五六七八九十]+)";
    const string Weekday = "[一二三四五六日天]";

    static readonly Regex Relative = new(
        $@"^(?<parts>(?:{Num}\s*个?\s*(?:秒钟?|分钟|分|小时|钟头|天)\s*)+)(?:后|之后|以后)|^(?<short>(?:\d+\s*(?:h|m|min|s)\s*)+)(?=\s|$)",
        RegexOptions.IgnoreCase);
    static readonly Regex RelativePart = new($@"(?<n>{Num})\s*个?\s*(?<u>秒钟?|分钟|分|小时|钟头|天|h|min|m|s)", RegexOptions.IgnoreCase);

    static readonly Regex Absolute = new(
        $@"^(?:(?<repeat>每天|每日|工作日|每个工作日|每周(?<rw>{Weekday})|每星期(?<rw>{Weekday}))\s*)?" +
        $@"(?:(?<day>今天|今晚|明天|明早|明晚|后天|大后天)|(?<next>下个?)?(?:周|星期|礼拜)(?<wd>{Weekday})|(?<m>\d{{1,2}})月(?<d>\d{{1,2}})[日号]?|(?<y>\d{{4}})[-/.](?<m2>\d{{1,2}})[-/.](?<d2>\d{{1,2}}))?\s*" +
        @"(?:(?<ampm>上午|早上|早晨|早|中午|下午|傍晚|晚上|夜里|凌晨)\s*)?" +
        $@"(?:(?<h>\d{{1,2}}|[零一二两三四五六七八九十]+)\s*(?:[:：](?<min>\d{{2}})|点\s*(?:(?<half>半)|(?<qm>\d{{1,2}}|[零一二三四五六七八九十]+)\s*分?|钟)?|时))?");

    public static bool TryParse(string input, DateTime now, out Reminder reminder)
    {
        reminder = null!;
        var text = input.Trim();
        if (text.StartsWith("提醒我")) text = text.Substring(3).TrimStart();
        else if (text.StartsWith("提醒")) text = text.Substring(2).TrimStart();
        else if (text.StartsWith("tx ", StringComparison.OrdinalIgnoreCase)) text = text.Substring(3).TrimStart();
        if (text.Length == 0) return false;

        if (Relative.Match(text) is { Success: true } rel)
        {
            var parts = rel.Groups["parts"].Success ? rel.Groups["parts"].Value : rel.Groups["short"].Value;
            double seconds = 0;
            foreach (Match p in RelativePart.Matches(parts))
            {
                if (ParseNumber(p.Groups["n"].Value) is not { } n) return false;
                seconds += n * UnitSeconds(p.Groups["u"].Value.ToLowerInvariant());
            }
            if (seconds < 1 || seconds > 366 * 86400) return false;
            reminder = new Reminder { Due = now.AddSeconds(seconds), Text = Message(text.Substring(rel.Length)) };
            return true;
        }

        var abs = Absolute.Match(text);
        bool hasDate = abs.Groups["day"].Success || abs.Groups["wd"].Success || abs.Groups["m"].Success || abs.Groups["y"].Success;
        bool hasTime = abs.Groups["h"].Success;
        bool hasRepeat = abs.Groups["repeat"].Success;
        if (!abs.Success || (!hasDate && !hasTime) || (hasRepeat && !hasTime && !hasDate)) return false;
        // A bare number without 点 or a colon is not a time, e.g. a search for "3"
        if (!hasDate && !hasRepeat && !abs.Groups["ampm"].Success && abs.Value.Trim().All(char.IsDigit)) return false;

        int hour = 9, minute = 0;
        if (hasTime)
        {
            if (ParseNumber(abs.Groups["h"].Value) is not { } h || h > 24) return false;
            hour = (int)h;
            if (abs.Groups["min"].Success) minute = int.Parse(abs.Groups["min"].Value, CultureInfo.InvariantCulture);
            else if (abs.Groups["half"].Success) minute = 30;
            else if (abs.Groups["qm"].Success) minute = (int)(ParseNumber(abs.Groups["qm"].Value) ?? 0);
            if (minute > 59) return false;
        }
        var ampm = abs.Groups["ampm"].Value;
        var day = abs.Groups["day"].Value;
        if (day is "今晚" or "明晚") ampm = "晚上";
        if (ampm is "下午" or "傍晚" or "晚上" or "夜里" && hour < 12) hour += 12;
        else if (ampm == "中午" && hour < 5) hour += 12;
        else if (ampm is "上午" or "早上" or "早晨" or "早" or "凌晨" && hour == 12) hour = 0;
        if (hour == 24) hour = 0;

        var repeat = abs.Groups["repeat"].Value switch
        {
            "每天" or "每日" => ReminderRepeat.Daily,
            "工作日" or "每个工作日" => ReminderRepeat.Weekdays,
            "" => ReminderRepeat.None,
            _ => ReminderRepeat.Weekly,
        };

        DateTime date = now.Date;
        if (day is "明天" or "明早" or "明晚") date = date.AddDays(1);
        else if (day == "后天") date = date.AddDays(2);
        else if (day == "大后天") date = date.AddDays(3);
        else if (abs.Groups["wd"].Success || abs.Groups["rw"].Success)
        {
            int target = WeekdayIndex(abs.Groups["wd"].Success ? abs.Groups["wd"].Value : abs.Groups["rw"].Value);
            int today = ((int)now.DayOfWeek + 6) % 7;
            int ahead = (target - today + 7) % 7;
            if (abs.Groups["next"].Success) ahead = 7 - today + target;
            date = date.AddDays(ahead);
            if (!abs.Groups["next"].Success && ahead == 0 && date.AddHours(hour).AddMinutes(minute) <= now) date = date.AddDays(7);
            if (abs.Groups["rw"].Success) repeat = ReminderRepeat.Weekly;
        }
        else if (abs.Groups["m"].Success || abs.Groups["y"].Success)
        {
            int y = abs.Groups["y"].Success ? int.Parse(abs.Groups["y"].Value) : now.Year;
            int m = int.Parse(abs.Groups["m"].Success ? abs.Groups["m"].Value : abs.Groups["m2"].Value);
            int d = int.Parse(abs.Groups["m"].Success ? abs.Groups["d"].Value : abs.Groups["d2"].Value);
            if (m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
            date = new DateTime(y, m, d);
            if (!abs.Groups["y"].Success && date.AddHours(hour).AddMinutes(minute) <= now) date = date.AddYears(1);
        }

        var due = date.AddHours(hour).AddMinutes(minute);
        if (due <= now && repeat != ReminderRepeat.None) due = Reminder.Next(due, repeat, now);
        else if (due <= now && !hasDate)
        {
            // "3点" at 14:00 means 15:00, not tomorrow's 03:00
            if (ampm.Length == 0 && hour < 12 && due.AddHours(12) > now) due = due.AddHours(12);
            else due = due.AddDays(1);
        }
        if (due <= now) return false;
        if (repeat == ReminderRepeat.Weekdays && due.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) due = Reminder.Next(due, repeat, due);

        reminder = new Reminder { Due = due, Text = Message(text.Substring(abs.Length)), Repeat = repeat };
        return true;
    }

    static string Message(string rest)
    {
        rest = rest.Trim().TrimStart('，', ',', '：', ':').Trim();
        if (rest.StartsWith("提醒我")) rest = rest.Substring(3).Trim();
        else if (rest.StartsWith("提醒")) rest = rest.Substring(2).Trim();
        return rest.Length > 0 ? rest : "提醒";
    }

    static double UnitSeconds(string unit) => unit switch
    {
        "秒" or "秒钟" or "s" => 1,
        "分" or "分钟" or "m" or "min" => 60,
        "小时" or "钟头" or "h" => 3600,
        _ => 86400,
    };

    static int WeekdayIndex(string c) => "一二三四五六日".IndexOf(c == "天" ? "日" : c, StringComparison.Ordinal);

    /// <summary>Digits, 半, or Chinese numerals up to 99.</summary>
    static double? ParseNumber(string s)
    {
        if (s == "半") return 0.5;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        const string digits = "零一二三四五六七八九";
        int Digit(char c) => c == '两' ? 2 : digits.IndexOf(c);
        int ten = s.IndexOf('十');
        if (ten < 0) return s.Length == 1 && Digit(s[0]) >= 0 ? Digit(s[0]) : null;
        int high = ten == 0 ? 1 : Digit(s[0]);
        int low = ten == s.Length - 1 ? 0 : Digit(s[ten + 1]);
        if (high < 0 || low < 0 || s.Length > ten + 2 || ten > 1) return null;
        return high * 10 + low;
    }
}
