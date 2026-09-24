using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SeedToolBox.SystemTools;

/// <summary>Runs commands as administrator through the UAC prompt, the same way the hosts page saves.</summary>
static class Elevation
{
    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Runs <paramref name="file"/> elevated and waits for it. Returns null on success, otherwise the reason.</summary>
    public static string? Run(string file, string arguments)
    {
        var psi = new ProcessStartInfo(file, arguments) { WindowStyle = ProcessWindowStyle.Hidden };
        if (IsAdmin)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
        }
        else
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return "无法启动 " + file;
            if (!p.WaitForExit(30000)) return "操作超时";
            return p.ExitCode == 0 ? null : $"操作失败（错误码 {p.ExitCode}）";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "已取消：需要管理员权限";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>Imports a .reg file, elevated. Uses the 64-bit registry view on 64-bit Windows.</summary>
    public static string? ImportReg(RegFile reg)
    {
        var path = Path.Combine(Path.GetTempPath(), $"SeedToolBox-{Guid.NewGuid():N}.reg");
        try
        {
            File.WriteAllText(path, reg.ToString(), Encoding.Unicode);
            return Run("reg.exe", $"import \"{path}\"" + (Environment.Is64BitOperatingSystem ? " /reg:64" : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs cmd.exe commands elevated, joined with &&.</summary>
    public static string? Cmd(params string[] commands) => Run("cmd.exe", "/c " + string.Join(" && ", commands));

    /// <summary>The 64-bit view on 64-bit Windows, so a 32-bit build still sees the real keys.</summary>
    public static RegistryKey Base(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

    /// <summary>Tells running programs that environment variables changed.</summary>
    public static void BroadcastEnvironmentChange()
    {
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

/// <summary>Builds the text of a .reg file (REGEDIT5 format).</summary>
sealed class RegFile
{
    readonly StringBuilder _text = new("Windows Registry Editor Version 5.00\r\n");
    string? _key;

    public RegFile Key(string fullPath)
    {
        if (_key == fullPath) return this;
        _key = fullPath;
        _text.Append("\r\n[").Append(fullPath).Append("]\r\n");
        return this;
    }

    public RegFile DeleteKey(string fullPath)
    {
        _key = null;
        _text.Append("\r\n[-").Append(fullPath).Append("]\r\n");
        return this;
    }

    public RegFile Delete(string name)
    {
        _text.Append(Name(name)).Append("=-\r\n");
        return this;
    }

    public RegFile Set(string name, RegistryValueKind kind, object? value)
    {
        _text.Append(Name(name)).Append('=');
        switch (kind)
        {
            case RegistryValueKind.String when value is string s && s.IndexOfAny(new[] { '\r', '\n' }) >= 0:
                Hex(1, Encoding.Unicode.GetBytes(s + "\0"));
                break;
            case RegistryValueKind.String:
                _text.Append('"').Append(Escape(value as string ?? "")).Append('"');
                break;
            case RegistryValueKind.DWord:
                _text.Append("dword:").Append(Convert.ToUInt32(value is int i ? unchecked((uint)i) : value).ToString("x8"));
                break;
            case RegistryValueKind.ExpandString:
                Hex(2, Encoding.Unicode.GetBytes((value as string ?? "") + "\0"));
                break;
            case RegistryValueKind.MultiString:
                Hex(7, Encoding.Unicode.GetBytes(string.Concat(((string[]?)value ?? Array.Empty<string>()).Select(s => s + "\0")) + "\0"));
                break;
            case RegistryValueKind.QWord:
                Hex(11, BitConverter.GetBytes(Convert.ToInt64(value)));
                break;
            case RegistryValueKind.Binary:
                Hex(3, (byte[]?)value ?? Array.Empty<byte>());
                break;
            default:
                _text.Append('"').Append(Escape(Convert.ToString(value) ?? "")).Append('"');
                break;
        }
        _text.Append("\r\n");
        return this;
    }

    void Hex(int type, byte[] bytes) => _text.Append(type == 3 ? "hex:" : $"hex({type:x}):").Append(string.Join(",", bytes.Select(b => b.ToString("x2"))));

    static string Name(string name) => name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

    public override string ToString() => _text.ToString();
}
