using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Terminal;

/// <summary>ZMODEM (rz / sz): the page runs the protocol, this side picks and reads or writes the files.</summary>
sealed partial class TerminalView
{
    readonly Dictionary<int, FileStream> _zwrites = new();
    readonly Dictionary<int, string> _zpaths = new();
    string[] _zfiles = Array.Empty<string>();
    string? _zfolder;

    /// <summary>A transfer is running; keystrokes are held back from the session.</summary>
    public bool Transferring { get; private set; }

    bool OnZmodem(string type, JObject m)
    {
        switch (type)
        {
            case "zout":
                Transferring = true;
                _session?.WriteBytes(Convert.FromBase64String((string?)m["d"] ?? ""));
                return true;
            case "zpick":
                Transferring = true;
                var open = new OpenFileDialog { Title = "选择要上传的文件（rz）", Multiselect = true };
                _zfiles = open.ShowDialog(Window.GetWindow(this)) == true ? open.FileNames : Array.Empty<string>();
                var files = new JArray(_zfiles.Select((f, i) =>
                {
                    var info = new FileInfo(f);
                    return new JObject { ["i"] = i, ["name"] = info.Name, ["size"] = info.Length, ["mtime"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() };
                }));
                Post(new JObject { ["t"] = "zfiles", ["files"] = files });
                return true;
            case "zread":
            {
                var id = (int?)m["id"] ?? 0;
                var index = (int?)m["file"] ?? -1;
                var off = (long?)m["off"] ?? 0;
                var n = Math.Min((int?)m["n"] ?? 65536, 1 << 20);
                var data = Array.Empty<byte>();
                if (index >= 0 && index < _zfiles.Length)
                {
                    try
                    {
                        using var fs = new FileStream(_zfiles[index], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fs.Position = off;
                        var buffer = new byte[n];
                        var read = 0;
                        while (read < n) { var r = fs.Read(buffer, read, n - read); if (r == 0) break; read += r; }
                        data = read == n ? buffer : buffer.Take(read).ToArray();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info("rz read failed: " + ex.Message); }
                }
                Post(new JObject { ["t"] = "zchunk", ["id"] = id, ["d"] = Convert.ToBase64String(data) });
                return true;
            }
            case "zsave":
            {
                Transferring = true;
                var id = (int?)m["id"] ?? 0;
                var name = Path.GetFileName((string?)m["name"] ?? "file");
                foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                string? path = null;
                if (_zfolder == null)
                {
                    // First file of the batch picks the folder; the rest of the batch goes next to it.
                    var save = new SaveFileDialog { Title = "保存 sz 发来的文件", FileName = name };
                    if (save.ShowDialog(Window.GetWindow(this)) == true) { path = save.FileName; _zfolder = Path.GetDirectoryName(path); }
                }
                else path = Unique(Path.Combine(_zfolder, name));
                var ok = false;
                if (path != null)
                {
                    try
                    {
                        _zwrites[id] = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                        _zpaths[id] = path;
                        ok = true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteText("\r\n\x1b[31m无法写入 " + path + "：" + ex.Message + "\x1b[0m\r\n"); }
                }
                Post(new JObject { ["t"] = "zsaved", ["id"] = id, ["ok"] = ok });
                return true;
            }
            case "zwrite":
            {
                if (_zwrites.TryGetValue((int?)m["id"] ?? 0, out var fs))
                {
                    var data = Convert.FromBase64String((string?)m["d"] ?? "");
                    fs.Write(data, 0, data.Length);
                }
                return true;
            }
            case "zclose":
            {
                var id = (int?)m["id"] ?? 0;
                CloseWrite(id, (bool?)m["ok"] ?? false);
                return true;
            }
            case "zdone":
                foreach (var id in _zwrites.Keys.ToList()) CloseWrite(id, false);
                if (_zfolder != null && m["aborted"] == null) WriteText("\x1b[90m已保存到 " + _zfolder + "\x1b[0m\r\n");
                _zfolder = null;
                _zfiles = Array.Empty<string>();
                Transferring = false;
                return true;
        }
        return false;
    }

    void CloseWrite(int id, bool ok)
    {
        if (!_zwrites.TryGetValue(id, out var fs)) return;
        _zwrites.Remove(id);
        fs.Dispose();
        var path = _zpaths[id];
        _zpaths.Remove(id);
        if (!ok) try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
