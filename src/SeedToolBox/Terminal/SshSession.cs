using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Terminal;

/// <summary>What a connection needs from the user while it connects; called on a background thread.</summary>
interface IConnectPrompts
{
    /// <summary>Returns true to trust the key. <paramref name="known"/> is the previously accepted key, if it changed.</summary>
    bool TrustHostKey(string endpoint, string fingerprint, string? known);
    /// <summary>Asks for a password, passphrase or keyboard-interactive answer; null cancels.</summary>
    string? Ask(string title, string prompt, bool secret);
}

/// <summary>An authenticated SSH connection, possibly through jump hosts and a proxy. Shells, SFTP and tunnels share it.</summary>
sealed class SshConnection : IDisposable
{
    public HostEntry Host { get; }
    public SshClient Client { get; private set; } = null!;
    public event Action<string>? Lost;

    readonly TerminalData _data;
    readonly IConnectPrompts _prompts;
    readonly List<IDisposable> _chain = new();
    ConnectionInfo _info = null!;
    string _endpoint = "";
    string? _systemInfo;
    int _disposed;

    SshConnection(HostEntry host, TerminalData data, IConnectPrompts prompts)
    {
        Host = host;
        _data = data;
        _prompts = prompts;
    }

    public static Task<SshConnection> OpenAsync(HostEntry host, TerminalData data, IConnectPrompts prompts, CancellationToken cancel) => Task.Run(() =>
    {
        var c = new SshConnection(host, data, prompts);
        try { c.Open(cancel); }
        catch { c.Dispose(); throw; }
        return c;
    }, cancel);

    void Open(CancellationToken cancel)
    {
        string address = Host.Host;
        int port = Host.Port;
        // Jump hosts: connect to the first, forward a local port to the next, and so on.
        var jumps = new List<HostEntry>();
        for (var j = _data.Find(Host.JumpHostId); j != null && jumps.Count < 5 && !jumps.Contains(j) && j != Host; j = _data.Find(j.JumpHostId)) jumps.Insert(0, j);
        HostEntry? previous = null;
        SshClient? via = null;
        foreach (var jump in jumps)
        {
            cancel.ThrowIfCancellationRequested();
            var info = Info(jump, previous == null ? jump.Host : "127.0.0.1", previous == null ? jump.Port : (int)((ForwardedPortLocal)_chain.Last()).BoundPort, previous == null);
            via = Connect(info, jump, $"{jump.Host}:{jump.Port}");
            _chain.Add(via);
            previous = jump;
            var forward = Forward(via, jump == jumps.Last() ? Host.Host : jumps[jumps.IndexOf(jump) + 1].Host, jump == jumps.Last() ? Host.Port : jumps[jumps.IndexOf(jump) + 1].Port);
            _chain.Add(forward);
        }
        if (via != null) { address = "127.0.0.1"; port = (int)((ForwardedPortLocal)_chain.Last()).BoundPort; }
        cancel.ThrowIfCancellationRequested();
        _endpoint = $"{Host.Host}:{Host.Port}";
        _info = Info(Host, address, port, jumps.Count == 0);
        Client = Connect(_info, Host, _endpoint);
        Client.ErrorOccurred += (_, e) => Lose(e.Exception.Message);
    }

    static ForwardedPortLocal Forward(SshClient via, string host, int port)
    {
        var f = new ForwardedPortLocal("127.0.0.1", 0, host, (uint)port);
        via.AddForwardedPort(f);
        f.Start();
        return f;
    }

    SshClient Connect(ConnectionInfo info, HostEntry host, string endpoint)
    {
        var client = new SshClient(info) { KeepAliveInterval = host.KeepAliveSeconds > 0 ? TimeSpan.FromSeconds(host.KeepAliveSeconds) : Timeout.InfiniteTimeSpan };
        client.HostKeyReceived += (_, e) =>
        {
            var fp = e.HostKeyName + " SHA256:" + e.FingerPrintSHA256;
            if (_data.KnownHosts.TryGetValue(endpoint, out var known) && known == fp) { e.CanTrust = true; return; }
            e.CanTrust = _prompts.TrustHostKey(endpoint, fp, known);
            if (e.CanTrust) lock (_data) _data.KnownHosts[endpoint] = fp;
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { client.Connect(); Log.Info($"SSH {endpoint} connected in {watch.ElapsedMilliseconds} ms ({client.ConnectionInfo.CurrentKeyExchangeAlgorithm}, {client.ConnectionInfo.CurrentServerEncryption})"); }
        catch (SshAuthenticationException ex) { client.Dispose(); throw new InvalidOperationException("登录失败：" + ex.Message, ex); }
        catch (SshConnectionException ex) when (ex.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.HostKeyNotVerifiable) { client.Dispose(); throw new InvalidOperationException("没有信任主机密钥，已断开", ex); }
        catch (SocketException ex) { client.Dispose(); throw new InvalidOperationException($"连不上 {endpoint}：{ex.Message}", ex); }
        catch (SshOperationTimeoutException ex) { client.Dispose(); throw new InvalidOperationException($"连接 {endpoint} 超时", ex); }
        return client;
    }

    ConnectionInfo Info(HostEntry host, string address, int port, bool useProxy)
    {
        var methods = new List<AuthenticationMethod>();
        switch (host.Auth)
        {
            case AuthKinds.Key:
            {
                var passphrase = Secret.Reveal(host.KeyPassphrase);
                PrivateKeyFile key;
                var path = Environment.ExpandEnvironmentVariables(host.KeyPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
                try { key = passphrase.Length > 0 ? new PrivateKeyFile(path, passphrase) : new PrivateKeyFile(path); }
                catch (Exception ex) when (ex is SshPassPhraseNullOrEmptyException or SshException)
                {
                    passphrase = _prompts.Ask("私钥口令", $"「{Path.GetFileName(path)}」有口令保护，请输入口令：", true) ?? throw new OperationCanceledException();
                    key = new PrivateKeyFile(path, passphrase);
                }
                catch (FileNotFoundException) { throw new InvalidOperationException("找不到私钥文件 " + path); }
                methods.Add(new PrivateKeyAuthenticationMethod(host.User, key));
                break;
            }
            case AuthKinds.Interactive:
                break;
            default:
            {
                var password = Secret.Reveal(host.Password);
                if (password.Length == 0) password = _prompts.Ask("登录 " + host.Title, $"{host.Address} 的密码：", true) ?? throw new OperationCanceledException();
                methods.Add(new PasswordAuthenticationMethod(host.User, password));
                break;
            }
        }
        var interactive = new KeyboardInteractiveAuthenticationMethod(host.User);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var p in e.Prompts)
            {
                // Servers that only allow keyboard-interactive usually ask for the password this way.
                var saved = Secret.Reveal(host.Password);
                p.Response = !p.IsEchoed && saved.Length > 0 && p.Request.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                    ? saved
                    : _prompts.Ask("登录 " + host.Title, (e.Instruction.Length > 0 ? e.Instruction + "\n" : "") + p.Request, !p.IsEchoed) ?? "";
            }
        };
        methods.Add(interactive);
        var proxy = useProxy ? host.Proxy switch { "socks4" => ProxyTypes.Socks4, "socks5" => ProxyTypes.Socks5, "http" => ProxyTypes.Http, _ => ProxyTypes.None } : ProxyTypes.None;
        var info = proxy == ProxyTypes.None
            ? new ConnectionInfo(address, port, host.User, methods.ToArray())
            : new ConnectionInfo(address, port, host.User, proxy, host.ProxyHost, host.ProxyPort, host.ProxyUser, Secret.Reveal(host.ProxyPassword), methods.ToArray());
        info.Timeout = TimeSpan.FromSeconds(15);
        info.Encoding = Encoding.UTF8;
        return info;
    }

    /// <summary>A second connection with the same route, for SFTP (SSH.NET keeps SFTP on its own session).</summary>
    public SftpClient OpenSftp()
    {
        var sftp = new SftpClient(_info) { KeepAliveInterval = Client.KeepAliveInterval, OperationTimeout = TimeSpan.FromSeconds(30) };
        sftp.HostKeyReceived += (_, e) => e.CanTrust = _data.KnownHosts.TryGetValue(_endpoint, out var k) && k == e.HostKeyName + " SHA256:" + e.FingerPrintSHA256;
        sftp.Connect();
        return sftp;
    }

    /// <summary>Runs a command on its own channel and returns stdout; used by the monitor.</summary>
    public string Run(string command, int timeoutSeconds = 10)
    {
        using var cmd = Client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        return cmd.Execute();
    }

    /// <summary>"Linux 5.15 x86_64, Ubuntu 22.04" or similar, for the assistant.</summary>
    public string SystemInfo()
    {
        if (_systemInfo != null) return _systemInfo;
        try
        {
            var text = Run("uname -srm; (. /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\"); echo \"$SHELL\"", 5);
            _systemInfo = string.Join(", ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException) { _systemInfo = "Linux"; }
        return _systemInfo;
    }

    public bool IsConnected => _disposed == 0 && Client is { IsConnected: true };

    void Lose(string reason)
    {
        if (_disposed == 0) Lost?.Invoke(reason);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Client?.Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException) { }
        for (var i = _chain.Count - 1; i >= 0; i--)
        {
            try { _chain[i].Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException) { }
        }
    }
}

/// <summary>An interactive shell on an SSH connection.</summary>
sealed class SshSession : ISession
{
    public event Action<byte[]>? Output;
    public event Action<string?>? Closed;

    public SshConnection Connection { get; }
    readonly ShellStream _stream;
    readonly Encoding _encoding;
    readonly Decoder? _decoder;
    readonly bool _ownsConnection;
    int _closed;

    public bool IsOpen => _closed == 0;

    public SshSession(SshConnection connection, int cols, int rows, bool ownsConnection)
    {
        Connection = connection;
        _ownsConnection = ownsConnection;
        _encoding = GetEncoding(connection.Host.Encoding);
        if (_encoding.CodePage != 65001) _decoder = _encoding.GetDecoder();
        var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.ECHO] = 1, [TerminalModes.TTY_OP_ISPEED] = 38400, [TerminalModes.TTY_OP_OSPEED] = 38400 };
        _stream = connection.Client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 64 * 1024, modes);
        _stream.Closed += (_, _) => Close("连接已关闭");
        connection.Lost += Close;
    }

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "SSH reader" }.Start();
        if (Connection.Host.StartupCommand.Trim().Length > 0) Write(Connection.Host.StartupCommand.TrimEnd() + "\r");
    }

    void ReadLoop()
    {
        var buffer = new byte[32 * 1024];
        try
        {
            int n;
            while ((n = _stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                byte[] bytes;
                if (_decoder != null)
                {
                    // xterm.js expects UTF-8, so text from GBK and similar servers is converted on the way.
                    var chars = new char[_decoder.GetCharCount(buffer, 0, n)];
                    _decoder.GetChars(buffer, 0, n, chars, 0);
                    bytes = Encoding.UTF8.GetBytes(chars);
                }
                else
                {
                    bytes = new byte[n];
                    Buffer.BlockCopy(buffer, 0, bytes, 0, n);
                }
                if (bytes.Length > 0) Output?.Invoke(bytes);
            }
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or IOException or SocketException) { }
        Close(Connection.IsConnected ? null : "连接已断开");
    }

    public static Encoding GetEncoding(string name)
    {
        try { return name.Length == 0 ? Encoding.UTF8 : Encoding.GetEncoding(name); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    public void Write(string text) => WriteBytes(_encoding.GetBytes(text));

    public void WriteBytes(byte[] bytes)
    {
        if (!IsOpen) return;
        try
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or IOException or SocketException) { Close(ex.Message); }
    }

    public void Resize(int cols, int rows)
    {
        if (!IsOpen) return;
        try { _stream.ChangeWindowSize((uint)cols, (uint)rows, 0, 0); }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException) { }
    }

    void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        var wasOpen = IsOpen;
        _closed = 1;
        Connection.Lost -= Close;
        try { _stream.Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or IOException) { }
        if (_ownsConnection) Connection.Dispose();
        if (wasOpen) Closed?.Invoke(null);
    }
}
