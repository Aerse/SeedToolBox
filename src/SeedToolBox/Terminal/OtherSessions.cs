using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SeedToolBox.Terminal;

static class Protocols
{
    public const string Ssh = "ssh", Telnet = "telnet", Serial = "serial", Ftp = "ftp", Ftps = "ftps";

    public static bool IsFiles(string p) => p is Ftp or Ftps;
}

/// <summary>Shared plumbing for byte-stream sessions: charset conversion to UTF-8 and closing once.</summary>
abstract class ByteSession : ISession
{
    public event Action<byte[]>? Output;
    public event Action<string?>? Closed;
    protected readonly Encoding Encoding;
    readonly Decoder? _decoder;
    int _closed;

    protected ByteSession(string encoding)
    {
        Encoding = SshSession.GetEncoding(encoding);
        if (Encoding.CodePage != 65001) _decoder = Encoding.GetDecoder();
    }

    public bool IsOpen => _closed == 0;

    protected void Emit(byte[] buffer, int count)
    {
        if (count <= 0) return;
        byte[] bytes;
        if (_decoder != null)
        {
            var chars = new char[_decoder.GetCharCount(buffer, 0, count)];
            _decoder.GetChars(buffer, 0, count, chars, 0);
            bytes = Encoding.UTF8.GetBytes(chars);
        }
        else
        {
            bytes = new byte[count];
            Buffer.BlockCopy(buffer, 0, bytes, 0, count);
        }
        if (bytes.Length > 0) Output?.Invoke(bytes);
    }

    protected void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Closed?.Invoke(reason);
    }

    public void Write(string text) => WriteBytes(Encoding.GetBytes(text));
    public abstract void WriteBytes(byte[] data);
    public virtual void Resize(int cols, int rows) { }
    public abstract void Start();
    public abstract void Dispose();
}

/// <summary>Telnet (RFC 854) with terminal type, window size and suppress-go-ahead; answers login prompts once when the host has a user and password.</summary>
sealed class TelnetSession : ByteSession
{
    const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    const byte ECHO = 1, SGA = 3, TTYPE = 24, NAWS = 31;

    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly object _writeLock = new();
    readonly string _user, _password;
    int _cols, _rows;
    bool _naws;
    readonly HashSet<byte> _local = new(), _remote = new(); // options enabled on our side and on theirs
    bool _sentUser, _sentPassword;
    readonly StringBuilder _recent = new();

    TelnetSession(TcpClient tcp, HostEntry host, string password, int cols, int rows) : base(host.Encoding)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _user = host.User;
        _password = password;
        _cols = cols;
        _rows = rows;
    }

    public static async Task<TelnetSession> OpenAsync(HostEntry host, string password, int cols, int rows, CancellationToken cancel)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            var connect = tcp.ConnectAsync(host.Host, host.Port);
            if (await Task.WhenAny(connect, Task.Delay(15000, cancel)) != connect)
            {
                cancel.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"连接 {host.Host}:{host.Port} 超时");
            }
            await connect;
        }
        catch (SocketException ex) { tcp.Dispose(); throw new InvalidOperationException($"连不上 {host.Host}:{host.Port}：{ex.Message}", ex); }
        catch { tcp.Dispose(); throw; }
        return new TelnetSession(tcp, host, password, cols, rows);
    }

    public override void Start() => new Thread(ReadLoop) { IsBackground = true, Name = "Telnet reader" }.Start();

    void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        var text = new byte[16 * 1024];
        int state = 0; // 0 data, 1 IAC, 2 option verb, 3 subnegotiation, 4 IAC inside subnegotiation
        byte verb = 0;
        var sub = new MemoryStream();
        try
        {
            int n;
            while ((n = _stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var t = 0;
                for (var i = 0; i < n; i++)
                {
                    var b = buffer[i];
                    switch (state)
                    {
                        case 0:
                            if (b == IAC) state = 1; else text[t++] = b;
                            break;
                        case 1:
                            if (b == IAC) { text[t++] = IAC; state = 0; }
                            else if (b is DO or DONT or WILL or WONT) { verb = b; state = 2; }
                            else if (b == SB) { sub.SetLength(0); state = 3; }
                            else state = 0;
                            break;
                        case 2:
                            Negotiate(verb, b);
                            state = 0;
                            break;
                        case 3:
                            if (b == IAC) state = 4; else sub.WriteByte(b);
                            break;
                        case 4:
                            if (b == SE) { Subnegotiation(sub.ToArray()); state = 0; }
                            else { sub.WriteByte(b); state = 3; }
                            break;
                    }
                }
                if (t > 0)
                {
                    AutoLogin(text, t);
                    Emit(text, t);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
        Close("连接已关闭");
    }

    void Negotiate(byte verb, byte option)
    {
        // Only answer requests that change an option's state (RFC 854), so hosts that repeat themselves don't loop.
        switch (verb)
        {
            case DO when option is TTYPE or SGA or NAWS:
                if (!_local.Add(option)) break;
                Send(IAC, WILL, option);
                if (option == NAWS) { _naws = true; SendSize(); }
                break;
            case DO:
                Send(IAC, WONT, option);
                break;
            case DONT:
                if (!_local.Remove(option)) break;
                Send(IAC, WONT, option);
                if (option == NAWS) _naws = false;
                break;
            case WILL when option is ECHO or SGA:
                if (_remote.Add(option)) Send(IAC, DO, option);
                break;
            case WILL:
                Send(IAC, DONT, option);
                break;
            case WONT:
                if (_remote.Remove(option)) Send(IAC, DONT, option);
                break;
        }
    }

    void Subnegotiation(byte[] data)
    {
        // Terminal type asked (SEND = 1): answer IS (0) "XTERM-256COLOR".
        if (data.Length >= 2 && data[0] == TTYPE && data[1] == 1)
        {
            var name = Encoding.ASCII.GetBytes("XTERM-256COLOR");
            var reply = new byte[name.Length + 6];
            reply[0] = IAC; reply[1] = SB; reply[2] = TTYPE; reply[3] = 0;
            Buffer.BlockCopy(name, 0, reply, 4, name.Length);
            reply[reply.Length - 2] = IAC; reply[reply.Length - 1] = SE;
            Send(reply);
        }
    }

    void SendSize()
    {
        if (!_naws) return;
        var s = new MemoryStream();
        s.Write(new byte[] { IAC, SB, NAWS }, 0, 3);
        foreach (var v in new[] { _cols >> 8, _cols & 0xFF, _rows >> 8, _rows & 0xFF })
        {
            s.WriteByte((byte)v);
            if (v == IAC) s.WriteByte(IAC);
        }
        s.Write(new byte[] { IAC, SE }, 0, 2);
        Send(s.ToArray());
    }

    void AutoLogin(byte[] data, int count)
    {
        if (_sentPassword || _user.Length == 0 && _password.Length == 0) return;
        _recent.Append(Encoding.GetString(data, 0, count));
        if (_recent.Length > 200) _recent.Remove(0, _recent.Length - 200);
        var tail = _recent.ToString().TrimEnd().ToLowerInvariant();
        if (!_sentUser && _user.Length > 0 && (tail.EndsWith("login:") || tail.EndsWith("username:") || tail.EndsWith("user:")))
        {
            _sentUser = true;
            _recent.Clear();
            ThreadPool.QueueUserWorkItem(_ => Write(_user + "\r"));
        }
        else if (_password.Length > 0 && tail.EndsWith("password:"))
        {
            _sentPassword = true;
            _recent.Clear();
            ThreadPool.QueueUserWorkItem(_ => Write(_password + "\r"));
        }
    }

    void Send(params byte[] data)
    {
        try { lock (_writeLock) _stream.Write(data, 0, data.Length); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { Close(ex.Message); }
    }

    public override void WriteBytes(byte[] data)
    {
        if (!IsOpen) return;
        // IAC in the data is doubled.
        var count = 0;
        foreach (var b in data) if (b == IAC) count++;
        if (count > 0)
        {
            var escaped = new byte[data.Length + count];
            var j = 0;
            foreach (var b in data) { escaped[j++] = b; if (b == IAC) escaped[j++] = IAC; }
            data = escaped;
        }
        Send(data);
    }

    public override void Resize(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        if (IsOpen) SendSize();
    }

    public override void Dispose()
    {
        Close(null);
        try { _tcp.Close(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }
}

/// <summary>A serial port (COM) console.</summary>
sealed class SerialSession : ByteSession
{
    readonly SerialPort _port;

    public SerialSession(HostEntry host) : base(host.Encoding)
    {
        _port = new SerialPort(host.SerialPort, host.BaudRate, ParseParity(host.Parity), host.DataBits, ParseStopBits(host.StopBits))
        {
            Handshake = host.FlowControl switch { "rtscts" => Handshake.RequestToSend, "xonxoff" => Handshake.XOnXOff, _ => Handshake.None },
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 3000,
            DtrEnable = true,
            RtsEnable = host.FlowControl != "rtscts",
        };
        try { _port.Open(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _port.Dispose();
            throw new InvalidOperationException($"打不开串口 {host.SerialPort}：{ex.Message}", ex);
        }
    }

    public static string[] PortNames()
    {
        try { return SerialPort.GetPortNames(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    static Parity ParseParity(string p) => p switch { "odd" => Parity.Odd, "even" => Parity.Even, "mark" => Parity.Mark, "space" => Parity.Space, _ => Parity.None };
    static StopBits ParseStopBits(string s) => s switch { "1.5" => StopBits.OnePointFive, "2" => StopBits.Two, _ => StopBits.One };

    public override void Start() => new Thread(ReadLoop) { IsBackground = true, Name = "Serial reader" }.Start();

    void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            var stream = _port.BaseStream;
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) Emit(buffer, n);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException) { }
        Close("串口已关闭");
    }

    public override void WriteBytes(byte[] data)
    {
        if (!IsOpen) return;
        try { _port.BaseStream.Write(data, 0, data.Length); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException) { Close(ex.Message); }
    }

    public override void Dispose()
    {
        Close(null);
        try { _port.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _port.Dispose();
    }
}
