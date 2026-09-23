using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>Generates UUIDs and random passwords with a cryptographic RNG.</summary>
sealed class GeneratorPage : DockPanel
{
    const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ", Lower = "abcdefghijklmnopqrstuvwxyz", Digits = "0123456789", Symbols = "!@#$%^&*()-_=+[]{};:,.?/~";
    const string Ambiguous = "Il1O0o";

    readonly TextBox _output = Ui.Area();
    readonly TextBox _count = Ui.Field(56);
    readonly ComboBox _uuidFormat = new() { Width = 170, ItemsSource = new[] { "标准（小写）", "标准（大写）", "无连字符", "带花括号" }, SelectedIndex = 0 };
    readonly TextBox _length = Ui.Field(56);
    readonly CheckBox _upper = Check("A-Z", true), _lower = Check("a-z", true), _digits = Check("0-9", true), _symbols = Check("符号", true), _noAmbiguous = Check("排除易混字符 Il1O0o", false);
    readonly TextBlock _status = Ui.Status();

    public GeneratorPage()
    {
        var header = Ui.Header("UUID / 密码生成", "使用加密级随机数生成 UUID 与随机密码");
        _count.Text = "5";
        _length.Text = "16";

        var uuidRow = Ui.Row(Ui.Label("UUID"), _uuidFormat, Ui.Label("", 8), Ui.Button("生成 UUID", GenerateUuids, accent: true));
        var pwdRow = Ui.Row(Ui.Label("密码长度"), _length, Ui.Label("", 12), _upper, _lower, _digits, _symbols, _noAmbiguous, Ui.Button("生成密码", GeneratePasswords, accent: true));
        var common = Ui.Row(Ui.Label("数量"), _count, Ui.Label("", 16), Ui.Button("复制全部", () => { if (_output.Text.Length > 0) ScreenToolService.CopyText(_output.Text.TrimEnd()); }), Ui.Button("清空", () => _output.Clear()));

        foreach (var box in new[] { _upper, _lower, _digits, _symbols, _noAmbiguous })
            box.Margin = new Thickness(0, 0, 12, 0);

        SetDock(header, Dock.Top);
        SetDock(common, Dock.Top);
        SetDock(uuidRow, Dock.Top);
        SetDock(pwdRow, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(header);
        Children.Add(common);
        Children.Add(uuidRow);
        Children.Add(pwdRow);
        Children.Add(_status);
        Children.Add(_output);
        GenerateUuids();
    }

    static CheckBox Check(string text, bool on) => new() { Content = text, IsChecked = on, VerticalAlignment = VerticalAlignment.Center };

    int Count()
    {
        if (int.TryParse(_count.Text, out var n) && n >= 1 && n <= 10000) return n;
        Ui.SetStatus(_status, "数量应为 1–10000", true);
        return 0;
    }

    void GenerateUuids()
    {
        int n = Count();
        if (n == 0) return;
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            var id = Guid.NewGuid();
            sb.AppendLine(_uuidFormat.SelectedIndex switch
            {
                1 => id.ToString("D").ToUpperInvariant(),
                2 => id.ToString("N"),
                3 => id.ToString("B"),
                _ => id.ToString("D"),
            });
        }
        _output.Text = sb.ToString();
        Ui.SetStatus(_status, $"已生成 {n} 个 UUID（v4）");
    }

    void GeneratePasswords()
    {
        int n = Count();
        if (n == 0) return;
        if (!int.TryParse(_length.Text, out var length) || length < 4 || length > 256) { Ui.SetStatus(_status, "密码长度应为 4–256", true); return; }
        var sets = new[] { (_upper, Upper), (_lower, Lower), (_digits, Digits), (_symbols, Symbols) }
            .Where(s => s.Item1.IsChecked == true)
            .Select(s => _noAmbiguous.IsChecked == true ? new string(s.Item2.Where(c => Ambiguous.IndexOf(c) < 0).ToArray()) : s.Item2)
            .ToList();
        if (sets.Count == 0) { Ui.SetStatus(_status, "至少选择一种字符", true); return; }

        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.AppendLine(Password(length, sets));
        _output.Text = sb.ToString();
        double bits = length * Math.Log(string.Concat(sets).Length, 2);
        Ui.SetStatus(_status, $"已生成 {n} 个密码，每个约 {bits:0} 位熵（{(bits >= 80 ? "强" : bits >= 60 ? "中" : "弱")}）");
    }

    /// <summary>A password with at least one character from every chosen set, in random order.</summary>
    public static string Password(int length, System.Collections.Generic.IList<string> sets)
    {
        var all = string.Concat(sets);
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = i < sets.Count ? sets[i][Next(sets[i].Length)] : all[Next(all.Length)];
        for (int i = length - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    /// <summary>Uniform integer in [0, max) without modulo bias.</summary>
    static int Next(int max)
    {
        var bytes = new byte[4];
        uint limit = uint.MaxValue - uint.MaxValue % (uint)max;
        uint value;
        do
        {
            Rng.GetBytes(bytes);
            value = BitConverter.ToUInt32(bytes, 0);
        } while (value >= limit);
        return (int)(value % (uint)max);
    }
}
