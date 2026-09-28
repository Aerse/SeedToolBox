using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;
using SeedToolBox.Views;

namespace SeedToolBox.Terminal;

static class TerminalDialogs
{
    /// <summary>A one-line prompt; secret uses a password box. Null when cancelled.</summary>
    public static string? Ask(Window? owner, string title, string prompt, bool secret, string initial = "")
    {
        var label = new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(4, 4, 4, 8) };
        var text = new TextBox { Style = DialogWindow.TextBoxStyle, Text = initial, MinWidth = 360 };
        var password = new PasswordBox { Style = DialogWindow.PasswordBoxStyle, MinWidth = 360 };
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(label);
        body.Children.Add(secret ? password : text);
        var ok = DialogWindow.OkButton();
        var window = DialogWindow.Create(title, body, ok, DialogWindow.CancelButton());
        Place(window, owner);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { if (secret) password.Focus(); else { text.Focus(); text.SelectAll(); } };
        return window.ShowDialog() == true ? (secret ? password.Password : text.Text) : null;
    }

    public static void Place(Window window, Window? owner)
    {
        if (owner != null && owner.IsVisible) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public static bool TrustHostKey(Window? owner, string endpoint, string fingerprint, string? known)
    {
        var text = known == null
            ? $"第一次连接 {endpoint}。\n\n主机密钥指纹：\n{fingerprint}\n\n确认这是你要连接的服务器吗？信任后下次不再询问。"
            : $"警告：{endpoint} 的主机密钥和上次不一样！\n这可能是服务器重装过，也可能有人在冒充它（中间人攻击）。\n\n上次：{known}\n这次：{fingerprint}\n\n仍然信任新密钥并继续连接吗？";
        return MessageBox.Show(owner!, text, known == null ? "确认主机密钥" : "主机密钥已改变", MessageBoxButton.YesNo,
            known == null ? MessageBoxImage.Question : MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    // ---------- host editor ----------

    static readonly (string Id, string Label)[] Encodings = { ("utf-8", "UTF-8"), ("gbk", "GBK"), ("gb18030", "GB18030"), ("big5", "Big5"), ("shift_jis", "Shift-JIS"), ("euc-kr", "EUC-KR"), ("iso-8859-1", "Latin-1") };
    static readonly (string Id, string Label)[] Colors = { ("", "无"), ("#E74C3C", "红"), ("#E67E22", "橙"), ("#F1C40F", "黄"), ("#2ECC71", "绿"), ("#3498DB", "蓝"), ("#9B59B6", "紫"), ("#95A5A6", "灰") };

    /// <summary>Edits a copy of the host; returns the edited copy or null.</summary>
    public static HostEntry? EditHost(Window? owner, TerminalData data, HostEntry original, bool isNew)
    {
        var h = original.Clone();
        var name = Ui.Field(); name.Text = h.Name;
        var group = new ComboBox { IsEditable = true, Text = h.Group };
        foreach (var g in data.AllGroups()) group.Items.Add(g);
        var address = Ui.Field(); address.Text = h.Host;
        var port = Ui.Field(70); port.Text = h.Port.ToString();
        var user = Ui.Field(); user.Text = h.User;
        string auth = h.Auth;
        var password = new PasswordBox { Style = DialogWindow.PasswordBoxStyle, Password = SafeReveal(h.Password) };
        var keyPath = Ui.Field(); keyPath.Text = h.KeyPath;
        var passphrase = new PasswordBox { Style = DialogWindow.PasswordBoxStyle, Password = SafeReveal(h.KeyPassphrase) };
        var jumpItems = new List<(string, string)> { ("", "不使用") };
        jumpItems.AddRange(data.Hosts.Where(x => x.Id != h.Id).Select(x => (x.Id, x.Title + "  (" + x.Address + ")")));
        string jump = h.JumpHostId, proxy = h.Proxy, encoding = h.Encoding, color = h.Color;
        var proxyHost = Ui.Field(); proxyHost.Text = h.ProxyHost;
        var proxyPort = Ui.Field(70); proxyPort.Text = h.ProxyPort.ToString();
        var proxyUser = Ui.Field(); proxyUser.Text = h.ProxyUser;
        var proxyPassword = new PasswordBox { Style = DialogWindow.PasswordBoxStyle, Password = SafeReveal(h.ProxyPassword) };
        var startup = Ui.Field(); startup.Text = h.StartupCommand;
        var keepAlive = Ui.Field(70); keepAlive.Text = h.KeepAliveSeconds.ToString();
        var reconnect = new CheckBox { Content = "断线后自动重连", IsChecked = h.AutoReconnect, VerticalAlignment = VerticalAlignment.Center };
        var notes = Ui.Area(wrap: true); notes.Text = h.Notes; notes.Height = 54;

        string protocol = h.IsSsh ? Protocols.Ssh : h.Protocol;
        var serialPort = new ComboBox { IsEditable = true, Text = h.SerialPort, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var name0 in SerialSession.PortNames()) serialPort.Items.Add(name0);
        var baud = new ComboBox { IsEditable = true, Text = h.BaudRate.ToString(), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var b in new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 }) baud.Items.Add(b.ToString());
        int dataBits = h.DataBits;
        string parity = h.Parity, stopBits = h.StopBits, flow = h.FlowControl;
        var serialBox = new StackPanel();
        serialBox.Children.Add(Line("串口", serialPort, SerialSession.PortNames().Length == 0 ? "没有检测到串口，可以直接输入名称，例如 COM3" : null));
        serialBox.Children.Add(Line("波特率", baud));
        serialBox.Children.Add(Line("数据位", ApiUi.Combo(140, new[] { ("8", "8"), ("7", "7"), ("6", "6"), ("5", "5") }, dataBits.ToString(), v => dataBits = int.Parse(v))));
        serialBox.Children.Add(Line("校验", ApiUi.Combo(140, new[] { ("none", "无"), ("odd", "奇校验"), ("even", "偶校验"), ("mark", "Mark"), ("space", "Space") }, parity, v => parity = v)));
        serialBox.Children.Add(Line("停止位", ApiUi.Combo(140, new[] { ("1", "1"), ("1.5", "1.5"), ("2", "2") }, stopBits, v => stopBits = v)));
        serialBox.Children.Add(Line("流控", ApiUi.Combo(140, new[] { ("none", "无"), ("rtscts", "RTS/CTS"), ("xonxoff", "XON/XOFF") }, flow, v => flow = v)));

        var passwordRow = Line("密码", password, "留空则每次连接时询问");
        var keyRow = Line("私钥文件", Browse(keyPath, owner));
        var passphraseRow = Line("私钥口令", passphrase, "没有口令就留空");
        DockPanel addressRow = null!, userRow = null!, authRow = null!;
        var sshOnly = new List<UIElement>();
        void ShowAuth()
        {
            if (addressRow == null) return;
            var ssh = protocol == Protocols.Ssh;
            var serial = protocol == Protocols.Serial;
            static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
            addressRow.Visibility = userRow.Visibility = V(!serial);
            serialBox.Visibility = V(serial);
            authRow.Visibility = V(ssh);
            passwordRow.Visibility = V(ssh ? auth == AuthKinds.Password : !serial);
            keyRow.Visibility = passphraseRow.Visibility = V(ssh && auth == AuthKinds.Key);
            foreach (var e in sshOnly) e.Visibility = V(ssh);
        }
        var authCombo = ApiUi.Combo(200, new[] { (AuthKinds.Password, "密码"), (AuthKinds.Key, "私钥"), (AuthKinds.Interactive, "键盘交互（验证码 / 二次验证）") }, auth, v => { auth = v; ShowAuth(); });
        var proxyBox = new StackPanel();
        proxyBox.Children.Add(Line("代理地址", Row2(proxyHost, Ui.Label("端口", 6), proxyPort)));
        proxyBox.Children.Add(Line("代理用户", proxyUser));
        proxyBox.Children.Add(Line("代理密码", proxyPassword));
        void ShowProxy() => proxyBox.Visibility = proxy == "none" ? Visibility.Collapsed : Visibility.Visible;
        var proxyCombo = ApiUi.Combo(200, new[] { ("none", "不使用"), ("socks5", "SOCKS5"), ("socks4", "SOCKS4"), ("http", "HTTP") }, proxy, v => { proxy = v; ShowProxy(); });

        var basic = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        basic.Children.Add(Line("名称", name, "留空显示 用户@地址"));
        basic.Children.Add(Line("分组", group, "用 / 分隔多级，例如 公司/生产"));
        basic.Children.Add(Line("协议", ApiUi.Combo(200, new[] { (Protocols.Ssh, "SSH"), (Protocols.Telnet, "Telnet"), (Protocols.Serial, "串口"), (Protocols.Ftp, "FTP"), (Protocols.Ftps, "FTPS（FTP over TLS）") }, protocol, v =>
        {
            static int Default(string p) => p switch { Protocols.Telnet => 23, Protocols.Ftp or Protocols.Ftps => 21, _ => 22 };
            if (port.Text.Trim() == Default(protocol).ToString()) port.Text = Default(v).ToString();
            protocol = v;
            ShowAuth();
        })));
        basic.Children.Add(addressRow = Line("地址", Row2(address, Ui.Label("端口", 6), port)));
        basic.Children.Add(serialBox);
        basic.Children.Add(userRow = Line("用户名", user, "Telnet 填了用户名和密码会在出现登录提示时自动填入；FTP 留空为匿名登录"));
        basic.Children.Add(authRow = Line("登录方式", authCombo));
        basic.Children.Add(passwordRow);
        basic.Children.Add(keyRow);
        basic.Children.Add(passphraseRow);
        basic.Children.Add(Line("标签颜色", ApiUi.Combo(120, Colors, color, v => color = v)));
        basic.Children.Add(Line("备注", notes));

        var advanced = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var jumpRow = Line("跳板机", ApiUi.Combo(300, jumpItems, jump, v => jump = v), "先连上跳板机，再从它连到这台");
        var proxyRow = Line("代理", proxyCombo, "跳板机之后的主机不走代理");
        advanced.Children.Add(jumpRow);
        advanced.Children.Add(proxyRow);
        advanced.Children.Add(proxyBox);
        sshOnly.Add(jumpRow);
        sshOnly.Add(proxyRow);
        advanced.Children.Add(Line("字符编码", ApiUi.Combo(140, Encodings, encoding, v => encoding = v)));
        advanced.Children.Add(Line("登录后执行", startup, "例如 cd /var/www && ls"));
        var keepAliveRow = Line("保活间隔(秒)", keepAlive, "0 表示关闭");
        advanced.Children.Add(keepAliveRow);
        advanced.Children.Add(Line("", reconnect));
        sshOnly.Add(keepAliveRow);
        ShowAuth();
        ShowProxy();
        if (protocol != Protocols.Ssh) proxyBox.Visibility = Visibility.Collapsed;

        var tabs = new TabControl { Width = 540, Height = 470 };
        tabs.Items.Add(new TabItem { Header = "基本", Content = basic });
        tabs.Items.Add(new TabItem { Header = "高级", Content = advanced });
        var tunnels = new TunnelList(h.Tunnels, owner);
        var tunnelTab = new TabItem { Header = "端口转发", Content = tunnels };
        tabs.Items.Add(tunnelTab);
        sshOnly.Add(tunnelTab);
        ShowAuth();

        var ok = DialogWindow.OkButton("保存");
        var window = DialogWindow.Create(isNew ? "新建主机" : "编辑主机 " + original.Title, tabs, ok, DialogWindow.CancelButton());
        Place(window, owner);
        ok.Click += (_, _) =>
        {
            if (protocol == Protocols.Serial)
            {
                if ((serialPort.Text ?? "").Trim().Length == 0) { MessageBox.Show(window, "请填写串口"); return; }
                if (!int.TryParse((baud.Text ?? "").Trim(), out var br) || br <= 0) { MessageBox.Show(window, "波特率不对"); return; }
                window.DialogResult = true;
                return;
            }
            if (address.Text.Trim().Length == 0) { MessageBox.Show(window, "请填写地址"); return; }
            if (!int.TryParse(port.Text.Trim(), out var p) || p <= 0 || p > 65535) { MessageBox.Show(window, "端口不对"); return; }
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => (isNew ? address : name).Focus();
        if (window.ShowDialog() != true) return null;

        h.Name = name.Text.Trim();
        h.Protocol = protocol;
        h.SerialPort = (serialPort.Text ?? "").Trim();
        h.BaudRate = int.TryParse((baud.Text ?? "").Trim(), out var baudRate) ? baudRate : 115200;
        h.DataBits = dataBits;
        h.Parity = parity;
        h.StopBits = stopBits;
        h.FlowControl = flow;
        h.Group = string.Join("/", (group.Text ?? "").Split('/').Select(s => s.Trim()).Where(s => s.Length > 0));
        var hostText = address.Text.Trim();
        // "user@host:port" pasted into the address box is taken apart.
        if (hostText.Contains('@')) { var at = hostText.LastIndexOf('@'); user.Text = hostText.Substring(0, at); hostText = hostText.Substring(at + 1); }
        if (hostText.Count(c => c == ':') == 1 && int.TryParse(hostText.Split(':')[1], out var embedded)) { port.Text = embedded.ToString(); hostText = hostText.Split(':')[0]; }
        h.Host = hostText;
        h.Port = int.TryParse(port.Text.Trim(), out var portNumber) ? portNumber : h.Port;
        h.User = user.Text.Trim();
        h.Auth = auth;
        h.Password = password.Password.Length > 0 ? Secret.Protect(password.Password) : "";
        h.KeyPath = keyPath.Text.Trim();
        h.KeyPassphrase = passphrase.Password.Length > 0 ? Secret.Protect(passphrase.Password) : "";
        h.JumpHostId = jump;
        h.Proxy = proxy;
        h.ProxyHost = proxyHost.Text.Trim();
        h.ProxyPort = int.TryParse(proxyPort.Text.Trim(), out var pp) ? pp : 1080;
        h.ProxyUser = proxyUser.Text.Trim();
        h.ProxyPassword = proxyPassword.Password.Length > 0 ? Secret.Protect(proxyPassword.Password) : "";
        h.Encoding = encoding;
        h.StartupCommand = startup.Text;
        h.KeepAliveSeconds = int.TryParse(keepAlive.Text.Trim(), out var ka) ? Math.Max(0, ka) : 30;
        h.AutoReconnect = reconnect.IsChecked == true;
        h.Color = color;
        h.Notes = notes.Text;
        h.Tunnels = tunnels.Result;
        return h;
    }

    static string SafeReveal(string stored)
    {
        try { return stored.Length == 0 ? "" : Secret.Reveal(stored); }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException) { return ""; }
    }

    public static DockPanel Line(string label, FrameworkElement input, string? hint = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var l = new TextBlock { Text = label, Width = 96, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        if (hint != null)
        {
            var stack = new StackPanel();
            stack.Children.Add(input);
            stack.Children.Add(new TextBlock { Text = hint, Foreground = DialogWindow.HintBrush, FontSize = 11, Margin = new Thickness(2, 2, 0, 0) });
            row.Children.Add(stack);
        }
        else row.Children.Add(input);
        return row;
    }

    static DockPanel Row2(FrameworkElement main, params FrameworkElement[] right)
    {
        var d = new DockPanel();
        foreach (var r in right.Reverse()) { DockPanel.SetDock(r, Dock.Right); d.Children.Add(r); }
        d.Children.Add(main);
        return d;
    }

    static DockPanel Browse(TextBox box, Window? owner)
    {
        var b = Ui.Button("浏览…", () =>
        {
            var dlg = new OpenFileDialog { Title = "选择私钥", InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh") };
            if (dlg.ShowDialog(owner) == true) box.Text = dlg.FileName;
        });
        b.Margin = new Thickness(6, 0, 0, 0);
        return Row2(box, b);
    }

    // ---------- settings ----------

    public static bool EditSettings(Window? owner, TerminalSettings s, IReadOnlyList<ShellProfile> shells)
    {
        string theme = s.Theme, cursor = s.CursorStyle, shell = s.DefaultShell;
        var font = Ui.Field(); font.Text = s.FontFamily;
        var size = Ui.Field(70); size.Text = s.FontSize.ToString();
        var lineHeight = Ui.Field(70); lineHeight.Text = s.LineHeight.ToString("0.##");
        var scrollback = Ui.Field(90); scrollback.Text = s.Scrollback.ToString();
        CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 0, 6) };
        var blink = Check("光标闪烁", s.CursorBlink);
        var copySelect = Check("选中即复制", s.CopyOnSelect);
        var rightPaste = Check("右键粘贴（有选中内容时弹出菜单）", s.RightClickPaste);
        var suggest = Check("输入时根据历史和片段提示补全", s.Suggestions);
        var confirmPaste = Check("粘贴多行内容前确认", s.ConfirmMultilinePaste);
        var bell = Check("响铃时发出提示音", s.BellSound);
        var logAlways = Check("自动记录所有会话日志", s.LogAlways);
        var logFolder = Ui.Field(); logFolder.Text = s.LogFolder;
        logFolder.ToolTip = "留空使用 数据目录\\TerminalLogs";

        var body = new StackPanel { Width = 500, Margin = new Thickness(0, 8, 0, 8) };
        body.Children.Add(Line("配色", ApiUi.Combo(200, TerminalThemes.All.Keys.Select(k => (k, k)), theme, v => theme = v)));
        body.Children.Add(Line("字体", font, "可以写多个，用逗号分隔，靠前的优先"));
        body.Children.Add(Line("字号", Row2(size, Ui.Label("行高", 6), lineHeight)));
        body.Children.Add(Line("光标", ApiUi.Combo(140, new[] { ("block", "方块"), ("bar", "竖线"), ("underline", "下划线") }, cursor, v => cursor = v)));
        body.Children.Add(Line("回滚行数", scrollback));
        body.Children.Add(Line("默认终端", ApiUi.Combo(200, shells.Select(x => (x.Id, x.Name)), shell, v => shell = v)));
        body.Children.Add(Line("日志目录", logFolder));
        foreach (var c in new[] { blink, copySelect, rightPaste, suggest, confirmPaste, bell, logAlways }) body.Children.Add(Line("", c));

        var ok = DialogWindow.OkButton();
        var window = DialogWindow.Create("终端设置", body, ok, DialogWindow.CancelButton());
        Place(window, owner);
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return false;
        s.Theme = theme;
        s.FontFamily = font.Text.Trim().Length > 0 ? font.Text.Trim() : new TerminalSettings().FontFamily;
        s.FontSize = int.TryParse(size.Text, out var fs) ? Math.Max(6, Math.Min(48, fs)) : 14;
        s.LineHeight = double.TryParse(lineHeight.Text, out var lh) ? Math.Max(1, Math.Min(2, lh)) : 1.1;
        s.CursorStyle = cursor;
        s.CursorBlink = blink.IsChecked == true;
        s.Scrollback = int.TryParse(scrollback.Text, out var sb) ? Math.Max(100, Math.Min(200000, sb)) : 5000;
        s.DefaultShell = shell;
        s.CopyOnSelect = copySelect.IsChecked == true;
        s.RightClickPaste = rightPaste.IsChecked == true;
        s.Suggestions = suggest.IsChecked == true;
        s.ConfirmMultilinePaste = confirmPaste.IsChecked == true;
        s.BellSound = bell.IsChecked == true;
        s.LogAlways = logAlways.IsChecked == true;
        s.LogFolder = logFolder.Text.Trim();
        return true;
    }
}

/// <summary>Editable list of port forwards, used in the host editor.</summary>
sealed class TunnelList : DockPanel
{
    readonly List<TunnelSpec> _items;
    readonly ListBox _list = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly Window? _owner;
    public List<TunnelSpec> Result => _items;

    public TunnelList(List<TunnelSpec> source, Window? owner)
    {
        _owner = owner;
        _items = source.Select(t => Newtonsoft.Json.JsonConvert.DeserializeObject<TunnelSpec>(Newtonsoft.Json.JsonConvert.SerializeObject(t))!).ToList();
        var buttons = Ui.Row(
            Ui.Button("添加…", () => { var t = Edit(new TunnelSpec { BindPort = 8080, TargetPort = 80 }); if (t != null) { _items.Add(t); Refresh(); } }),
            Ui.Button("编辑…", () => { if (_list.SelectedItem is ListBoxItem { Tag: TunnelSpec t } && Edit(t) is { } e) { _items[_items.IndexOf(t)] = e; Refresh(); } }),
            Ui.Button("删除", () => { if (_list.SelectedItem is ListBoxItem { Tag: TunnelSpec t }) { _items.Remove(t); Refresh(); } }));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(buttons, Dock.Top);
        Children.Add(buttons);
        var hint = new TextBlock { Text = "勾选「随连接启动」的转发会在连上后自动开启；其他的可以在终端工具栏的「端口转发」里手动开关。", TextWrapping = TextWrapping.Wrap, Foreground = DialogWindow.HintBrush, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(hint, Dock.Bottom);
        Children.Add(hint);
        Children.Add(_list);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: TunnelSpec t } && Edit(t) is { } e) { _items[_items.IndexOf(t)] = e; Refresh(); } };
        Refresh();
    }

    void Refresh()
    {
        _list.Items.Clear();
        foreach (var t in _items)
            _list.Items.Add(new ListBoxItem { Tag = t, Content = (t.Name.Length > 0 ? t.Name + "：" : "") + t.Describe + (t.AutoStart ? "  · 随连接启动" : "") });
    }

    public static TunnelSpec? EditSpec(Window? owner, TunnelSpec original)
    {
        var t = Newtonsoft.Json.JsonConvert.DeserializeObject<TunnelSpec>(Newtonsoft.Json.JsonConvert.SerializeObject(original))!;
        string kind = t.Kind;
        var name = Ui.Field(); name.Text = t.Name;
        var bindHost = Ui.Field(); bindHost.Text = t.BindHost;
        var bindPort = Ui.Field(80); bindPort.Text = t.BindPort.ToString();
        var targetHost = Ui.Field(); targetHost.Text = t.TargetHost;
        var targetPort = Ui.Field(80); targetPort.Text = t.TargetPort.ToString();
        var auto = new CheckBox { Content = "随连接启动", IsChecked = t.AutoStart };
        var bindLabel = new TextBlock { Width = 96, VerticalAlignment = VerticalAlignment.Center };
        var targetLabel = new TextBlock { Width = 96, VerticalAlignment = VerticalAlignment.Center };
        DockPanel Pair(TextBlock label, TextBox host, TextBox port)
        {
            var d = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(label, Dock.Left);
            d.Children.Add(label);
            DockPanel.SetDock(port, Dock.Right);
            d.Children.Add(port);
            var colon = Ui.Label(":", 4);
            colon.Margin = new Thickness(4, 0, 4, 0);
            DockPanel.SetDock(colon, Dock.Right);
            d.Children.Add(colon);
            d.Children.Add(host);
            return d;
        }
        var targetRow = Pair(targetLabel, targetHost, targetPort);
        void Labels()
        {
            bindLabel.Text = kind == "R" ? "远程监听" : "本机监听";
            targetLabel.Text = kind == "R" ? "转到本机" : "转到远程";
            targetRow.Visibility = kind == "D" ? Visibility.Collapsed : Visibility.Visible;
        }
        var body = new StackPanel { Width = 440, Margin = new Thickness(0, 8, 0, 8) };
        body.Children.Add(TerminalDialogs.Line("类型", ApiUi.Combo(260, new[] { ("L", "本地转发 (-L)"), ("R", "远程转发 (-R)"), ("D", "动态转发 / SOCKS 代理 (-D)") }, kind, v => { kind = v; Labels(); })));
        body.Children.Add(TerminalDialogs.Line("名称", name));
        body.Children.Add(Pair(bindLabel, bindHost, bindPort));
        body.Children.Add(targetRow);
        body.Children.Add(TerminalDialogs.Line("", auto));
        Labels();
        var ok = DialogWindow.OkButton();
        var window = DialogWindow.Create("端口转发", body, ok, DialogWindow.CancelButton());
        if (owner != null && owner.IsVisible) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        ok.Click += (_, _) =>
        {
            if (!int.TryParse(bindPort.Text, out var bp) || bp <= 0 || bp > 65535) { MessageBox.Show(window, "监听端口不对"); return; }
            if (kind != "D" && (!int.TryParse(targetPort.Text, out var tp) || tp <= 0 || tp > 65535)) { MessageBox.Show(window, "目标端口不对"); return; }
            window.DialogResult = true;
        };
        if (window.ShowDialog() != true) return null;
        t.Kind = kind;
        t.Name = name.Text.Trim();
        t.BindHost = bindHost.Text.Trim().Length > 0 ? bindHost.Text.Trim() : "127.0.0.1";
        t.BindPort = int.Parse(bindPort.Text);
        t.TargetHost = targetHost.Text.Trim().Length > 0 ? targetHost.Text.Trim() : "127.0.0.1";
        t.TargetPort = int.TryParse(targetPort.Text, out var tpp) ? tpp : 0;
        t.AutoStart = auto.IsChecked == true;
        return t;
    }

    TunnelSpec? Edit(TunnelSpec t) => EditSpec(_owner, t);
}
