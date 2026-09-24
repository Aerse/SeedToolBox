using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.ScreenTools;

namespace SeedToolBox.DevTools;

/// <summary>Generates UUIDs, ULIDs, NanoIDs, random passwords and passphrases with a cryptographic RNG, plus Lorem Ipsum filler.</summary>
sealed class GeneratorPage : DockPanel
{
    const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ", Lower = "abcdefghijklmnopqrstuvwxyz", Digits = "0123456789", Symbols = "!@#$%^&*()-_=+[]{};:,.?/~";
    const string Ambiguous = "Il1O0o";

    readonly TextBox _output = Ui.Area();
    readonly TextBox _count = Ui.Field(56);
    readonly ComboBox _idKind = new() { Width = 170, ItemsSource = new[] { "UUID v4（随机）", "UUID v1（时间）", "UUID v5（名称）", "UUID v7（时间有序）", "ULID", "NanoID" }, SelectedIndex = 0 };
    readonly ComboBox _uuidFormat = new() { Width = 130, ItemsSource = new[] { "标准（小写）", "标准（大写）", "无连字符", "带花括号" }, SelectedIndex = 0 };
    readonly ComboBox _namespace = new() { Width = 290, IsEditable = true, ItemsSource = new[] { "DNS", "URL", "OID", "X500" }, SelectedIndex = 0, ToolTip = "DNS / URL / OID / X500，或填写任意 UUID" };
    readonly TextBox _name = Ui.Field(260);
    readonly TextBox _nanoLength = Ui.Field(56);
    readonly StackPanel _v5Options, _nanoOptions;
    readonly TextBox _length = Ui.Field(56);
    readonly CheckBox _upper = Check("A-Z", true), _lower = Check("a-z", true), _digits = Check("0-9", true), _symbols = Check("符号", true), _noAmbiguous = Check("排除易混字符 Il1O0o", false);
    readonly TextBox _custom = Ui.Field(260);
    readonly TextBox _words = Ui.Field(56);
    readonly TextBox _separator = Ui.Field(40);
    readonly CheckBox _capitalize = Check("首字母大写", true), _addNumber = Check("末尾加数字", false);
    readonly TextBlock _status = Ui.Status();

    public GeneratorPage()
    {
        var header = Ui.Header("UUID / 密码生成", "使用加密级随机数生成 UUID、ULID、NanoID、随机密码与密码短语，也可生成 Lorem Ipsum 占位文本");
        _count.Text = "5";
        _length.Text = "16";
        _nanoLength.Text = "21";
        _words.Text = "5";
        _separator.Text = "-";
        _name.Text = "example.com";

        _v5Options = Ui.Row(Ui.Label("命名空间"), _namespace, Ui.Label("", 12), Ui.Label("名称"), _name);
        _nanoOptions = Ui.Row(Ui.Label("长度"), _nanoLength);
        var idRow = Ui.Row(Ui.Label("ID"), _idKind, Ui.Label("", 8), _uuidFormat, Ui.Label("", 8), Ui.Button("生成 ID", GenerateIds, accent: true));
        _idKind.SelectionChanged += (_, _) => UpdateIdOptions();
        UpdateIdOptions();

        var pwdRow = Ui.Row(Ui.Label("密码长度"), _length, Ui.Label("", 12), _upper, _lower, _digits, _symbols, _noAmbiguous, Ui.Button("生成密码", GeneratePasswords, accent: true));
        var customRow = Ui.Row(Ui.Label("自定义字符集"), _custom, Ui.Label("", 8), new TextBlock { Text = "填写后只用这些字符，忽略上面的勾选", Foreground = Views.DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center });
        var phraseRow = Ui.Row(Ui.Label("密码短语"), _words, Ui.Label("个单词", 12), Ui.Label("分隔符"), _separator, Ui.Label("", 12), _capitalize, _addNumber, Ui.Button("生成短语", GeneratePassphrases, accent: true));
        var loremRow = Ui.Row(Ui.Button("生成 Lorem Ipsum", GenerateLorem), new TextBlock { Text = "段落数取上面的“数量”（最多 100 段）", Foreground = Views.DialogWindow.HintBrush, VerticalAlignment = VerticalAlignment.Center });
        var common = Ui.Row(Ui.Label("数量"), _count, Ui.Label("", 16), Ui.Button("复制全部", () => { if (_output.Text.Length > 0) ScreenToolService.CopyText(_output.Text.TrimEnd()); }), Ui.SaveButton(() => _output.Text.TrimEnd(), _status, "generated.txt"), Ui.Button("清空", () => _output.Clear()));

        foreach (var box in new[] { _upper, _lower, _digits, _symbols, _noAmbiguous, _capitalize, _addNumber })
            box.Margin = new Thickness(0, 0, 12, 0);
        _custom.FontFamily = Ui.Mono;

        var rows = new UIElement[] { common, idRow, _v5Options, _nanoOptions, pwdRow, customRow, phraseRow, loremRow };
        SetDock(header, Dock.Top);
        Children.Add(header);
        foreach (var row in rows)
        {
            SetDock(row, Dock.Top);
            Children.Add(row);
        }
        SetDock(_status, Dock.Bottom);
        Children.Add(_status);
        Children.Add(_output);
        GenerateIds();
    }

    static CheckBox Check(string text, bool on) => new() { Content = text, IsChecked = on, VerticalAlignment = VerticalAlignment.Center };

    void UpdateIdOptions()
    {
        int kind = _idKind.SelectedIndex;
        _v5Options.Visibility = kind == 2 ? Visibility.Visible : Visibility.Collapsed;
        _nanoOptions.Visibility = kind == 5 ? Visibility.Visible : Visibility.Collapsed;
        _uuidFormat.IsEnabled = kind <= 3;
    }

    int Count(int max = 10000)
    {
        if (int.TryParse(_count.Text, out var n) && n >= 1 && n <= max) return n;
        Ui.SetStatus(_status, $"数量应为 1–{max}", true);
        return 0;
    }

    string FormatUuid(Guid id) => _uuidFormat.SelectedIndex switch
    {
        1 => id.ToString("D").ToUpperInvariant(),
        2 => id.ToString("N"),
        3 => id.ToString("B"),
        _ => id.ToString("D"),
    };

    Guid? Namespace()
    {
        var text = _namespace.Text.Trim();
        switch (text.ToUpperInvariant())
        {
            case "DNS": return Identifiers.DnsNamespace;
            case "URL": return Identifiers.UrlNamespace;
            case "OID": return Identifiers.OidNamespace;
            case "X500": return Identifiers.X500Namespace;
        }
        return Guid.TryParse(text, out var id) ? id : null;
    }

    void GenerateIds()
    {
        int n = Count();
        if (n == 0) return;
        int kind = _idKind.SelectedIndex;
        Func<string> next;
        string what;
        switch (kind)
        {
            case 1: next = () => FormatUuid(Identifiers.V1()); what = "UUID（v1）"; break;
            case 2:
                if (Namespace() is not { } ns) { Ui.SetStatus(_status, "命名空间应为 DNS、URL、OID、X500 或一个 UUID", true); return; }
                // v5 is deterministic: one UUID per comma-separated name, and the count does not apply
                var list = _name.Text.Split(new[] { ',', '，', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (list.Count == 0) list.Add("");
                _output.Text = string.Concat(list.Select(name => (list.Count > 1 ? name + "\t" : "") + FormatUuid(Identifiers.V5(ns, name)) + "\r\n"));
                Ui.SetStatus(_status, $"已生成 {list.Count} 个 UUID（v5，SHA-1）；同一命名空间和名称总是得到同一个 UUID，多个名称可用逗号分隔");
                return;
            case 3: next = () => FormatUuid(Identifiers.V7()); what = "UUID（v7）"; break;
            case 4: next = Identifiers.Ulid; what = "ULID"; break;
            case 5:
                if (!int.TryParse(_nanoLength.Text, out var length) || length < 2 || length > 256) { Ui.SetStatus(_status, "NanoID 长度应为 2–256", true); return; }
                next = () => Identifiers.NanoId(length);
                what = $"NanoID（{length} 位，约 {length * 6} 位熵）";
                break;
            default: next = () => FormatUuid(Guid.NewGuid()); what = "UUID（v4）"; break;
        }
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.AppendLine(next());
        _output.Text = sb.ToString();
        Ui.SetStatus(_status, $"已生成 {n} 个 {what}");
    }

    void GeneratePasswords()
    {
        int n = Count();
        if (n == 0) return;
        if (!int.TryParse(_length.Text, out var length) || length < 4 || length > 256) { Ui.SetStatus(_status, "密码长度应为 4–256", true); return; }
        List<string> sets;
        if (_custom.Text.Length > 0)
        {
            var custom = new string(_custom.Text.Where(c => !char.IsControl(c)).Distinct().ToArray());
            if (custom.Length < 2) { Ui.SetStatus(_status, "自定义字符集至少要有 2 个不同字符", true); return; }
            sets = new List<string> { custom };
        }
        else
        {
            sets = new[] { (_upper, Upper), (_lower, Lower), (_digits, Digits), (_symbols, Symbols) }
                .Where(s => s.Item1.IsChecked == true)
                .Select(s => _noAmbiguous.IsChecked == true ? new string(s.Item2.Where(c => Ambiguous.IndexOf(c) < 0).ToArray()) : s.Item2)
                .ToList();
            if (sets.Count == 0) { Ui.SetStatus(_status, "至少选择一种字符", true); return; }
        }

        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.AppendLine(Password(length, sets));
        _output.Text = sb.ToString();
        double bits = length * Math.Log(string.Concat(sets).Length, 2);
        Ui.SetStatus(_status, $"已生成 {n} 个密码，每个约 {bits:0} 位熵（{Strength(bits)}）");
    }

    static string Strength(double bits) => bits >= 80 ? "强" : bits >= 60 ? "中" : "弱";

    void GeneratePassphrases()
    {
        int n = Count();
        if (n == 0) return;
        if (!int.TryParse(_words.Text, out var words) || words < 2 || words > 20) { Ui.SetStatus(_status, "单词数应为 2–20", true); return; }
        var separator = _separator.Text;
        bool capitalize = _capitalize.IsChecked == true, number = _addNumber.IsChecked == true;
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            var parts = Enumerable.Range(0, words).Select(_ =>
            {
                var w = WordList[Next(WordList.Length)];
                return capitalize ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w;
            }).ToList();
            if (number) parts.Add(Next(100).ToString());
            sb.AppendLine(string.Join(separator, parts));
        }
        _output.Text = sb.ToString();
        double bits = words * Math.Log(WordList.Length, 2) + (number ? Math.Log(100, 2) : 0);
        Ui.SetStatus(_status, $"已生成 {n} 个密码短语（词表 {WordList.Length} 词），每个约 {bits:0} 位熵（{Strength(bits)}）");
    }

    void GenerateLorem()
    {
        int n = Count(100);
        if (n == 0) return;
        var sb = new StringBuilder();
        for (int p = 0; p < n; p++)
        {
            int sentences = 4 + Next(4);
            for (int s = 0; s < sentences; s++)
            {
                if (p == 0 && s == 0) { sb.Append("Lorem ipsum dolor sit amet, consectetur adipiscing elit."); continue; }
                int count = 6 + Next(10);
                var words = Enumerable.Range(0, count).Select(_ => Lorem[Next(Lorem.Length)]).ToArray();
                words[0] = char.ToUpperInvariant(words[0][0]) + words[0].Substring(1);
                // An occasional comma makes it read like prose
                if (count > 9) words[3 + Next(count - 6)] += ",";
                sb.Append(' ').Append(string.Join(" ", words)).Append('.');
            }
            sb.Append("\r\n\r\n");
        }
        _output.Text = sb.ToString().Replace("\r\n ", "\r\n");
        Ui.SetStatus(_status, $"已生成 {n} 段 Lorem Ipsum");
    }

    /// <summary>A password with at least one character from every chosen set, in random order.</summary>
    public static string Password(int length, IList<string> sets)
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

    static readonly string[] Lorem =
    (
        "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua enim ad minim veniam " +
        "quis nostrud exercitation ullamco laboris nisi aliquip ex ea commodo consequat duis aute irure in reprehenderit voluptate velit esse cillum " +
        "eu fugiat nulla pariatur excepteur sint occaecat cupidatat non proident sunt culpa qui officia deserunt mollit anim id est laborum " +
        "integer vitae justo eget magna fermentum iaculis nunc sed augue lacus viverra maecenas accumsan pellentesque habitant morbi tristique senectus netus " +
        "malesuada fames ac turpis egestas mauris rhoncus aenean vel porta nibh venenatis cras semper auctor neque volutpat blandit cursus risus ultrices"
    ).Split(' ');

    /// <summary>Short, common, easy-to-type English words for passphrases.</summary>
    static readonly string[] WordList =
    (
        "able acid aged also area army away baby back ball band bank base bath bear beat bell belt best bird blow blue boat body bone book boot born boss " +
        "both bowl bread brick bridge brown bulk burn bush busy cake calm camp card care cart case cash cast cell chat chip city clay club coal coat code " +
        "cold cook cool copy corn cost crew crop dark data date dawn deal deep deer desk dial diet dish door dose down draw drop drum duck dust duty each " +
        "earn east easy edge else even ever exit face fact fair fall farm fast fear feed feel file fill film find fine fire firm fish flag flat flow " +
        "folk food foot fork form fort four free frog fuel full fund gain game gate gear gift girl give glad glass goal gold golf good grab gray grow " +
        "hair half hall hand hard harm hawk head heat help herb hero high hill hint hold hole home hook hope horn host hour huge idea inch iron item " +
        "jade jazz join joke jump just keen keep kind king kite knee lake lamp land lane last late lawn lead leaf lean left lens life lift light lime " +
        "line lion list live load loan lock long loop lord loud love luck lung made mail main make mall many mark mask mass meal meat menu mild milk " +
        "mill mind mine mint mode moon more most move much nail name navy near neck nest news next nice nine node noon nose note oven over pace pack " +
        "page pain pair palm park part past path peak pear pick pile pine pink pipe plan play plot plum poem pole pond pool port post pull pump pure " +
        "quiz race rail rain rank rare rate read real rest rice rich ride ring rise road rock roof room root rope rose ruby rule safe sail salt sand " +
        "save seal seat seed ship shoe shop shot side sign silk sing site size skin sky slow snow soap sock soft soil song soup spot star stay step " +
        "stone sugar suit sun swan table tail tale talk tall tank tape task team tent test text tide tile time tiny tone tool tour town tree trip " +
        "tube tune turn twin unit vast verb view vine vote wage walk wall warm wave wax week well west whale wheat wide wife wild wind wine wing wire " +
        "wise wolf wood wool word work yard yarn year yell zero zinc zone"
    ).Split(' ');
}
