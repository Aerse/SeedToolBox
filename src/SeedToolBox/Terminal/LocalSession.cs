using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SeedToolBox.Terminal;

/// <summary>Something a terminal view talks to: a local shell or an SSH channel.</summary>
interface ISession : IDisposable
{
    /// <summary>Raw output, UTF-8; raised on a background thread.</summary>
    event Action<byte[]>? Output;
    /// <summary>The shell or connection ended; the argument is a reason to show, or null.</summary>
    event Action<string?>? Closed;
    void Write(string text);
    /// <summary>Raw bytes, sent as they are (file transfers).</summary>
    void WriteBytes(byte[] data);
    void Resize(int cols, int rows);
    /// <summary>Begins reading output; call after subscribing so nothing is missed.</summary>
    void Start();
    bool IsOpen { get; }
}

/// <summary>Writes on a background thread, in order, so a full pipe or SSH window never stalls the UI thread.</summary>
sealed class WriteQueue
{
    readonly BlockingCollection<byte[]> _queue = new();

    public WriteQueue(Action<byte[]> write, string name) => new Thread(() =>
    {
        foreach (var data in _queue.GetConsumingEnumerable()) write(data);
    }) { IsBackground = true, Name = name }.Start();

    public void Add(byte[] data)
    {
        try { _queue.Add(data); }
        catch (InvalidOperationException) { } // completed: the session is closing
    }

    /// <summary>Lets the writer finish what is queued and exit.</summary>
    public void Complete() => _queue.CompleteAdding();
}

sealed record ShellProfile(string Id, string Name, string Command, string? Arguments = null, string Glyph = "\uE756");

/// <summary>A local shell running in a Windows pseudo console (ConPTY, Windows 10 1809+).</summary>
sealed class LocalSession : ISession
{
    public event Action<byte[]>? Output;
    public event Action<string?>? Closed;

    readonly IntPtr _console;
    readonly SafeFileHandle _input, _output;
    readonly FileStream _writer;
    readonly Process _process;
    readonly WriteQueue _writes;
    int _closed;

    public bool IsOpen => _closed == 0;
    public int ProcessId => _process.Id;

    public LocalSession(ShellProfile profile, int cols, int rows, string? directory = null)
    {
        if (!CreatePipe(out var inRead, out _input, IntPtr.Zero, 0) || !CreatePipe(out _output, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        var hr = CreatePseudoConsole(new Coord((short)Math.Max(2, cols), (short)Math.Max(2, rows)), inRead, outWrite, 0, out _console);
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr, "无法创建伪终端（需要 Windows 10 1809 或更高版本）");
        _writer = new FileStream(_input, FileAccess.Write);
        _writes = new WriteQueue(Send, "ConPTY writer");
        _process = Process.Start(_console, profile.Command + (profile.Arguments != null ? " " + profile.Arguments : ""),
            directory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        // The waiter owns the process handles; closing the pseudo console ends the shell, so it always returns.
        var waiter = new Thread(() => { WaitForSingleObject(_process.Handle, -1); Close(null); _process.Dispose(); }) { IsBackground = true };
        waiter.Start();
    }

    public void Start() => new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY reader" }.Start();

    void ReadLoop()
    {
        using var stream = new FileStream(_output, FileAccess.Read);
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new byte[n];
                Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                Output?.Invoke(chunk);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        Close(null);
    }

    public void Write(string text) => WriteBytes(Encoding.UTF8.GetBytes(text));

    public void WriteBytes(byte[] bytes)
    {
        if (IsOpen) _writes.Add(bytes);
    }

    void Send(byte[] bytes)
    {
        if (!IsOpen) return;
        try { _writer.Write(bytes, 0, bytes.Length); _writer.Flush(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Resize(int cols, int rows)
    {
        if (IsOpen) ResizePseudoConsole(_console, new Coord((short)Math.Max(2, cols), (short)Math.Max(2, rows)));
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
        _writes.Complete();
        // Closing the pseudo console ends the shell and makes the reader see end of file.
        if (_console != IntPtr.Zero) ClosePseudoConsole(_console);
        try { _writer.Dispose(); } catch (IOException) { }
        if (wasOpen) Closed?.Invoke(null);
    }

    // ---------- shells installed on this computer ----------

    public static List<ShellProfile> DetectShells()
    {
        var list = new List<ShellProfile>();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pwsh = FindOnPath("pwsh.exe") ?? new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\PowerShell\7\pwsh.exe" }.FirstOrDefault(File.Exists);
        if (pwsh != null) list.Add(new ShellProfile("pwsh", "PowerShell 7", Quote(pwsh), "-NoLogo", "\uE756"));
        list.Add(new ShellProfile("powershell", "Windows PowerShell", Quote(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe")), "-NoLogo", "\uE756"));
        list.Add(new ShellProfile("cmd", "命令提示符", Quote(Path.Combine(system, "cmd.exe")), null, "\uE756"));
        if (File.Exists(Path.Combine(system, "wsl.exe"))) list.Add(new ShellProfile("wsl", "WSL", Quote(Path.Combine(system, "wsl.exe")), "~", "\uE7F8"));
        var gitBash = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Git\bin\bash.exe",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs\Git\bin\bash.exe",
        }.FirstOrDefault(File.Exists);
        if (gitBash != null) list.Add(new ShellProfile("gitbash", "Git Bash", Quote(gitBash), "--login -i", "\uE7F8"));
        return list;
    }

    static string Quote(string path) => "\"" + path + "\"";

    static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(p => p.Length > 0)
            .Select(p => { try { return Path.Combine(p.Trim(), exe); } catch (ArgumentException) { return ""; } })
            .FirstOrDefault(File.Exists);

    // ---------- Win32 ----------

    sealed class Process : IDisposable
    {
        public IntPtr Handle;
        public IntPtr Thread;
        public int Id;

        public static Process Start(IntPtr console, string commandLine, string directory)
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            var startup = new StartupInfoEx { StartupInfo = { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = 0x100 /* STARTF_USESTDHANDLES: keep the child off our own console */ }, AttributeList = Marshal.AllocHGlobal(size) };
            try
            {
                if (!InitializeProcThreadAttributeList(startup.AttributeList, 1, 0, ref size)) throw new Win32Exception();
                if (!UpdateProcThreadAttribute(startup.AttributeList, 0, (IntPtr)0x00020016, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, Directory.Exists(directory) ? directory : null, ref startup, out var info))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动 " + commandLine);
                return new Process { Handle = info.hProcess, Thread = info.hThread, Id = info.dwProcessId };
            }
            finally
            {
                DeleteProcThreadAttributeList(startup.AttributeList);
                Marshal.FreeHGlobal(startup.AttributeList);
            }
        }

        public void Dispose()
        {
            if (Thread != IntPtr.Zero) { CloseHandle(Thread); Thread = IntPtr.Zero; }
            if (Handle != IntPtr.Zero) { CloseHandle(Handle); Handle = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Coord { public short X, Y; public Coord(short x, short y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")]
    static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")]
    static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")]
    static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string? app, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags,
        IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, int milliseconds);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
