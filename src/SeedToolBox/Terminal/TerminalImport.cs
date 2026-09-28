using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeedToolBox.DevTools;
using SeedToolBox.DevTools.Api;

namespace SeedToolBox.Terminal;

/// <summary>Reads host lists from OpenSSH, Xshell and FinalShell. Only connection details come across, never saved passwords.</summary>
static class TerminalImport
{
    public static List<HostEntry> SshConfig(string text, string group)
    {
        var hosts = new List<HostEntry>();
        var jumps = new Dictionary<HostEntry, string>();
        HostEntry? current = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var split = line.IndexOfAny(new[] { ' ', '\t', '=' });
            if (split < 0) continue;
            var key = line.Substring(0, split).ToLowerInvariant();
            var value = line.Substring(split + 1).Trim().TrimStart('=').Trim().Trim('"');
            switch (key)
            {
                case "host":
                    current = null;
                    // Wildcard blocks are defaults, not hosts.
                    var name = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(n => n.IndexOfAny(new[] { '*', '?', '!' }) < 0);
                    if (name == null) continue;
                    current = new HostEntry { Name = name, Host = name, Group = group, User = Environment.UserName.ToLowerInvariant() };
                    hosts.Add(current);
                    break;
                case "match": current = null; break;
                case "hostname" when current != null: current.Host = value; break;
                case "port" when current != null && int.TryParse(value, out var port): current.Port = port; break;
                case "user" when current != null: current.User = value; break;
                case "identityfile" when current != null:
                    current.Auth = AuthKinds.Key;
                    current.KeyPath = value.StartsWith("~") ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value.Substring(1).Replace('/', '\\') : value;
                    break;
                case "proxyjump" when current != null: jumps[current] = value.Split(',')[0].Trim(); break;
                case "serveraliveinterval" when current != null && int.TryParse(value, out var alive): current.KeepAliveSeconds = alive; break;
            }
        }
        foreach (var pair in jumps)
        {
            var target = pair.Value.Contains('@') ? pair.Value.Substring(pair.Value.IndexOf('@') + 1) : pair.Value;
            if (hosts.FirstOrDefault(h => h.Name == target || h.Host == target) is { } jump && jump != pair.Key) pair.Key.JumpHostId = jump.Id;
        }
        return hosts;
    }

    /// <summary>Xshell keeps one .xsh (INI) file per session; sub-folders are groups.</summary>
    public static List<HostEntry> Xshell(string folder, string group)
    {
        var hosts = new List<HostEntry>();
        foreach (var file in Directory.GetFiles(folder, "*.xsh", SearchOption.AllDirectories))
        {
            var ini = ReadIni(file);
            string V(string section, string key) => ini.TryGetValue(section + "/" + key, out var v) ? v : "";
            var host = V("CONNECTION", "Host");
            if (host.Length == 0) continue;
            var rel = Path.GetDirectoryName(file)!.Substring(folder.TrimEnd('\\').Length).Trim('\\').Replace('\\', '/');
            var h = new HostEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Host = host,
                Port = int.TryParse(V("CONNECTION", "Port"), out var p) ? p : 22,
                User = V("CONNECTION:AUTHENTICATION", "UserName") is { Length: > 0 } u ? u : "root",
                Group = string.Join("/", new[] { group, rel }.Where(s => s.Length > 0)),
            };
            if (V("CONNECTION:AUTHENTICATION", "Method") == "1" || V("CONNECTION:AUTHENTICATION", "UserKey").Length > 0) h.Auth = AuthKinds.Key;
            hosts.Add(h);
        }
        return hosts;
    }

    static Dictionary<string, string> ReadIni(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bytes = File.ReadAllBytes(file);
        // Xshell writes UTF-16 LE with a BOM; older versions use the ANSI code page.
        var text = bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
            : bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.Default.GetString(bytes);
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("[") && line.EndsWith("]")) section = line.Substring(1, line.Length - 2);
            else if (line.IndexOf('=') is var eq and > 0) result[section + "/" + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return result;
    }

    /// <summary>FinalShell stores each connection as conn/**/*_connect_config.json, with folders in *_folder.json-like files.</summary>
    public static List<HostEntry> FinalShell(string folder, string group)
    {
        var folders = new Dictionary<string, (string Name, string Parent)>();
        foreach (var file in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).Where(f => !f.EndsWith("_connect_config.json")))
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(file));
                if ((string?)o["id"] is { } id && (string?)o["name"] is { } name && o["host"] == null) folders[id] = (name, (string?)o["parent_id"] ?? "");
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        string FolderPath(string id)
        {
            var parts = new List<string>();
            for (var i = 0; i < 10 && folders.TryGetValue(id, out var f); i++) { parts.Insert(0, f.Name); id = f.Parent; }
            return string.Join("/", parts);
        }
        var hosts = new List<HostEntry>();
        foreach (var file in Directory.GetFiles(folder, "*_connect_config.json", SearchOption.AllDirectories))
        {
            JObject o;
            try { o = JObject.Parse(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is JsonException or IOException) { continue; }
            var host = (string?)o["host"] ?? "";
            if (host.Length == 0) continue;
            var sub = FolderPath((string?)o["parent_id"] ?? "");
            hosts.Add(new HostEntry
            {
                Name = (string?)o["name"] ?? host,
                Host = host,
                Port = (int?)o["port"] ?? 22,
                User = (string?)o["user_name"] is { Length: > 0 } u ? u : "root",
                Auth = (int?)o["authentication_type"] == 2 ? AuthKinds.Key : AuthKinds.Password,
                Group = string.Join("/", new[] { group, sub }.Where(s => s.Length > 0)),
                Notes = (string?)o["description"] ?? "",
            });
        }
        return hosts;
    }
}

sealed partial class TerminalPage
{
    ContextMenu ImportMenu()
    {
        var menu = new ContextMenu();
        void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("从 ~/.ssh/config 导入", () =>
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            if (!File.Exists(path))
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择 OpenSSH 配置文件", InitialDirectory = System.IO.Path.GetDirectoryName(path) };
                if (dlg.ShowDialog(Owner) != true) return;
                path = dlg.FileName;
            }
            Import(() => TerminalImport.SshConfig(File.ReadAllText(path), "ssh config"), "~/.ssh/config");
        });
        Item("从 Xshell 导入…", () =>
        {
            var guess = Directory.Exists(DocumentsNetSarang()) ? DocumentsNetSarang() : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (PickFolder("选择 Xshell 的 Sessions 文件夹（或导出的会话文件夹）", guess) is { } folder)
                Import(() => TerminalImport.Xshell(folder, "Xshell"), "Xshell");
        });
        Item("从 FinalShell 导入…", () =>
        {
            if (PickFolder("选择 FinalShell 安装目录下的 conn 文件夹", @"C:\") is { } folder)
                Import(() => TerminalImport.FinalShell(folder, "FinalShell"), "FinalShell");
        });
        return menu;
    }

    static string DocumentsNetSarang()
    {
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NetSarang Computer");
        if (!Directory.Exists(root)) return root;
        return Directory.GetDirectories(root).Select(v => System.IO.Path.Combine(v, "Xshell", "Sessions")).Where(Directory.Exists).OrderByDescending(p => p).FirstOrDefault() ?? root;
    }

    static string? PickFolder(string description, string initial)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = description, SelectedPath = initial };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    void Import(Func<List<HostEntry>> read, string source)
    {
        List<HostEntry> found;
        try { found = read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Ui.SetStatus(_status, "导入失败：" + ex.Message, true);
            return;
        }
        var added = found.Where(h => !_data.Hosts.Any(x => x.Host.Equals(h.Host, StringComparison.OrdinalIgnoreCase) && x.Port == h.Port && x.User == h.User)).ToList();
        if (found.Count == 0) { Ui.SetStatus(_status, $"在 {source} 里没有找到主机", true); return; }
        if (added.Count == 0) { Ui.SetStatus(_status, $"{source} 里的 {found.Count} 台主机都已经存在"); return; }
        _data.Hosts.AddRange(added);
        ScheduleSave();
        RefreshTree();
        var msg = $"从 {source} 导入了 {added.Count} 台主机" + (found.Count > added.Count ? $"，跳过已存在的 {found.Count - added.Count} 台" : "") + "。密码不会导入，第一次连接时会询问。";
        Ui.SetStatus(_status, msg);
        ApiDialogs.ShowText(Owner, "导入完成", msg + "\n\n" + string.Join("\n", added.Select(h => $"{h.Title}  {h.Address}  {h.Group}")));
    }
}
