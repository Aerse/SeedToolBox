using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FluentFTP;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace SeedToolBox.Terminal;

/// <summary>One entry of a remote folder listing.</summary>
sealed class RemoteFile
{
    public string Name = "", FullName = "";
    public bool IsDirectory, IsLink, IsRegular;
    public long Length;
    public DateTime Modified;
    /// <summary>"drwxr-xr-x" style, or "" when the server does not say.</summary>
    public string Mode = "";
    public string Owner = "";
}

/// <summary>What the file panel needs from a server: SFTP over an SSH connection, or FTP / FTPS.</summary>
interface IRemoteFiles : IDisposable
{
    bool IsConnected { get; }
    string Home { get; }
    bool CanChmod { get; }
    List<RemoteFile> List(string dir);
    bool Exists(string path);
    void CreateDirectory(string path);
    void CreateEmpty(string path);
    void Rename(string from, string to);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void Chmod(string path, short mode);
    Task UploadAsync(Stream source, string remote, Action<long>? progress, CancellationToken cancel);
    Task DownloadAsync(string remote, Stream target, Action<long>? progress, CancellationToken cancel);
}

sealed class SftpFiles : IRemoteFiles
{
    readonly SftpClient _client;

    public SftpFiles(SftpClient client) => _client = client;

    public bool IsConnected => _client.IsConnected;
    public string Home => _client.WorkingDirectory;
    public bool CanChmod => true;

    public List<RemoteFile> List(string dir) =>
        _client.ListDirectory(dir).Where(f => f.Name != "." && f.Name != "..").Select(f => new RemoteFile
        {
            Name = f.Name, FullName = f.FullName, IsDirectory = f.IsDirectory, IsLink = f.IsSymbolicLink, IsRegular = f.IsRegularFile,
            Length = f.Length, Modified = f.LastWriteTime, Mode = Mode(f), Owner = f.UserId.ToString(),
        }).ToList();

    static string Mode(ISftpFile f)
    {
        char B(bool v, char c) => v ? c : '-';
        return (f.IsDirectory ? "d" : f.IsSymbolicLink ? "l" : "-")
            + B(f.OwnerCanRead, 'r') + B(f.OwnerCanWrite, 'w') + B(f.OwnerCanExecute, 'x')
            + B(f.GroupCanRead, 'r') + B(f.GroupCanWrite, 'w') + B(f.GroupCanExecute, 'x')
            + B(f.OthersCanRead, 'r') + B(f.OthersCanWrite, 'w') + B(f.OthersCanExecute, 'x');
    }

    public bool Exists(string path) => _client.Exists(path);
    public void CreateDirectory(string path) => _client.CreateDirectory(path);
    public void CreateEmpty(string path) { using var s = new MemoryStream(); _client.UploadFile(s, path, false); }
    public void Rename(string from, string to) => _client.RenameFile(from, to);
    public void DeleteFile(string path) => _client.DeleteFile(path);
    public void DeleteDirectory(string path) => _client.DeleteDirectory(path);
    public void Chmod(string path, short mode) => _client.ChangePermissions(path, mode);

    public Task UploadAsync(Stream source, string remote, Action<long>? progress, CancellationToken cancel) =>
        _client.UploadFileAsync(source, remote, true, progress == null ? null : new Progress<UploadFileProgressReport>(p => progress((long)p.TotalBytesUploaded)), cancel);

    public Task DownloadAsync(string remote, Stream target, Action<long>? progress, CancellationToken cancel) =>
        _client.DownloadFileAsync(remote, target, progress == null ? null : new Progress<DownloadFileProgressReport>(p => progress((long)p.TotalBytesDownloaded)), cancel);

    public void Dispose()
    {
        try { _client.Dispose(); } catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException) { }
    }
}

/// <summary>FTP and FTPS (explicit TLS). Commands share one control connection, so each client is used by one caller at a time.</summary>
sealed class FtpFiles : IRemoteFiles
{
    readonly HostEntry _host;
    readonly string _password;
    readonly AsyncFtpClient _client;
    readonly SemaphoreSlim _lock = new(1, 1);
    // Transfers get their own connection so the folder tree stays usable meanwhile.
    AsyncFtpClient? _transfer;
    readonly SemaphoreSlim _transferLock = new(1, 1);
    /// <summary>Certificates the user accepted this run, by thumbprint.</summary>
    static readonly HashSet<string> Trusted = new();

    FtpFiles(HostEntry host, string password)
    {
        _host = host;
        _password = password;
        _client = Create();
    }

    public static async Task<FtpFiles> OpenAsync(HostEntry host, string password, CancellationToken cancel)
    {
        var f = new FtpFiles(host, password);
        try
        {
            await f._client.Connect(cancel);
            f.Home = await f._client.GetWorkingDirectory(cancel);
        }
        catch { f.Dispose(); throw; }
        return f;
    }

    AsyncFtpClient Create()
    {
        var user = _host.User.Length > 0 ? _host.User : "anonymous";
        var password = _host.User.Length > 0 ? _password : "anonymous@";
        var c = new AsyncFtpClient(_host.Host, user, password, _host.Port);
        c.Encoding = SshSession.GetEncoding(_host.Encoding);
        c.Config.ConnectTimeout = 15000;
        c.Config.ReadTimeout = 30000;
        c.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        if (_host.Protocol == Protocols.Ftps)
        {
            c.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            c.ValidateCertificate += (_, e) => e.Accept = e.PolicyErrors == SslPolicyErrors.None || Trust(e.Certificate);
        }
        return c;
    }

    bool Trust(System.Security.Cryptography.X509Certificates.X509Certificate cert)
    {
        var id = cert.GetCertHashString();
        lock (Trusted) if (Trusted.Contains(id)) return true;
        var ok = Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
            $"{_host.Host} 的证书没有通过校验（可能是自签名证书）。\n\n颁发给：{cert.Subject}\n颁发者：{cert.Issuer}\n指纹：{id}\n\n仍然连接吗？",
            "FTPS 证书", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
        if (ok) lock (Trusted) Trusted.Add(id);
        return ok;
    }

    T Do<T>(Func<AsyncFtpClient, Task<T>> action)
    {
        _lock.Wait();
        try
        {
            if (!_client.IsConnected) _client.Connect().GetAwaiter().GetResult();
            return action(_client).GetAwaiter().GetResult();
        }
        finally { _lock.Release(); }
    }

    void Do(Func<AsyncFtpClient, Task> action) => Do(async c => { await action(c); return true; });

    // Commands reconnect when the server dropped an idle control connection.
    public bool IsConnected => !_disposed;
    bool _disposed;
    public string Home { get; private set; } = "/";
    public bool CanChmod => true;

    public List<RemoteFile> List(string dir) =>
        Do(c => c.GetListing(dir, FtpListOption.AllFiles)).Where(f => f.Name != "." && f.Name != "..").Select(f => new RemoteFile
        {
            Name = f.Name,
            FullName = f.FullName,
            IsDirectory = f.Type == FtpObjectType.Directory,
            IsLink = f.Type == FtpObjectType.Link,
            IsRegular = f.Type == FtpObjectType.File,
            Length = f.Size < 0 ? 0 : f.Size,
            Modified = f.Modified,
            Mode = f.RawPermissions is { Length: 10 } p ? p : "",
            Owner = f.RawOwner ?? "",
        }).ToList();

    public bool Exists(string path) => Do(async c => await c.DirectoryExists(path) || await c.FileExists(path));
    public void CreateDirectory(string path) => Do(c => c.CreateDirectory(path));
    public void CreateEmpty(string path) => Do(c => c.UploadBytes(Array.Empty<byte>(), path, FtpRemoteExists.Skip));
    public void Rename(string from, string to) => Do(c => c.Rename(from, to));
    public void DeleteFile(string path) => Do(c => c.DeleteFile(path));
    public void DeleteDirectory(string path) => Do(c => c.DeleteDirectory(path));
    public void Chmod(string path, short mode) => Do(c => c.Chmod(path, mode));

    async Task<AsyncFtpClient> TransferClient(CancellationToken cancel)
    {
        _transfer ??= Create();
        if (!_transfer.IsConnected) await _transfer.Connect(cancel);
        return _transfer;
    }

    public async Task UploadAsync(Stream source, string remote, Action<long>? progress, CancellationToken cancel)
    {
        await _transferLock.WaitAsync(cancel);
        try
        {
            var c = await TransferClient(cancel);
            var status = await c.UploadStream(source, remote, FtpRemoteExists.Overwrite, false, progress == null ? null : new Progress<FtpProgress>(p => progress(p.TransferredBytes)), cancel);
            if (status == FtpStatus.Failed) throw new IOException("服务器拒绝了上传");
        }
        finally { _transferLock.Release(); }
    }

    public async Task DownloadAsync(string remote, Stream target, Action<long>? progress, CancellationToken cancel)
    {
        await _transferLock.WaitAsync(cancel);
        try
        {
            var c = await TransferClient(cancel);
            if (!await c.DownloadStream(target, remote, 0, progress == null ? null : new Progress<FtpProgress>(p => progress(p.TransferredBytes)), cancel))
                throw new IOException("下载失败");
        }
        finally { _transferLock.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var c in new[] { _client, _transfer })
            try { c?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or FluentFTP.Exceptions.FtpException) { }
    }
}
