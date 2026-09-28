using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SeedToolBox.Sync;

/// <summary>
/// Encrypts what goes to the WebDAV server with the sync password: PBKDF2-SHA256 key, AES-256-CBC, HMAC-SHA256.
/// Layout: "STB2" | salt(16) | iv(16) | revision(8) | ciphertext | hmac(32) over everything before it plus the file
/// name, so a file can't be swapped for another one or, with the revision, for an older copy of itself.
/// "STB1" files (no revision, not bound to a name) from older versions are still read.
/// </summary>
sealed class SyncCrypto
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("STB2");
    static readonly byte[] LegacyMagic = Encoding.ASCII.GetBytes("STB1");
    const int Iterations = 120_000;

    readonly string _password;
    /// <summary>Derived keys by salt; deriving takes a while and the same few salts come back every sync.</summary>
    readonly Dictionary<string, (byte[] Aes, byte[] Mac)> _keys = new();
    byte[]? _salt;

    public SyncCrypto(string password) => _password = password;

    (byte[] Aes, byte[] Mac) Keys(byte[] salt)
    {
        var id = Convert.ToBase64String(salt);
        if (_keys.TryGetValue(id, out var keys)) return keys;
        using var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(_password), salt, Iterations, HashAlgorithmName.SHA256);
        var bytes = kdf.GetBytes(64);
        keys = (bytes.Take(32).ToArray(), bytes.Skip(32).ToArray());
        _keys[id] = keys;
        return keys;
    }

    /// <param name="name">The file's path on the server, bound into the MAC.</param>
    /// <param name="revision">Grows with every upload of the file, so a rolled-back copy can be told apart.</param>
    public byte[] Encrypt(byte[] plain, string name, long revision = 0)
    {
        // One salt per session keeps it to a single key derivation
        _salt ??= Random(16);
        var (aesKey, macKey) = Keys(_salt);
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.GenerateIV();
        using var output = new MemoryStream();
        output.Write(Magic, 0, Magic.Length);
        output.Write(_salt, 0, _salt.Length);
        output.Write(aes.IV, 0, aes.IV.Length);
        output.Write(BitConverter.GetBytes(revision), 0, 8);
        using (var encryptor = aes.CreateEncryptor())
        {
            var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            output.Write(cipher, 0, cipher.Length);
        }
        var mac = Mac(macKey, output.ToArray(), name);
        output.Write(mac, 0, mac.Length);
        return output.ToArray();
    }

    public byte[] Decrypt(byte[] data, string name) => Decrypt(data, name, out _, out _);

    /// <param name="legacy">An STB1 file, which should be rewritten in the current format.</param>
    public byte[] Decrypt(byte[] data, string name, out long revision, out bool legacy)
    {
        legacy = data.Length >= 4 && data.Take(4).SequenceEqual(LegacyMagic);
        int header = legacy ? 4 + 16 + 16 : 4 + 16 + 16 + 8;
        if (data.Length < header + 16 + 32 || !legacy && !data.Take(4).SequenceEqual(Magic))
            throw new SyncException("云端文件不是本程序加密的，或已损坏（其他电脑的程序版本太旧也会这样，请都更新到最新版）");
        var salt = data.Skip(4).Take(16).ToArray();
        var (aesKey, macKey) = Keys(salt);
        var body = data.Take(data.Length - 32).ToArray();
        var mac = legacy ? Mac(macKey, body, null) : Mac(macKey, body, name);
        if (!FixedEquals(mac, data.Skip(data.Length - 32).ToArray())) throw new SyncException("同步密码不对，和其他电脑上设置的不一样，或者云端文件被替换过");
        revision = legacy ? 0 : BitConverter.ToInt64(data, 36);
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = data.Skip(20).Take(16).ToArray();
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, header, data.Length - header - 32);
    }

    static byte[] Mac(byte[] key, byte[] body, string? name)
    {
        using var hmac = new HMACSHA256(key);
        if (name == null) return hmac.ComputeHash(body);
        var bound = body.Concat(Encoding.UTF8.GetBytes("|" + name)).ToArray();
        return hmac.ComputeHash(bound);
    }

    static bool FixedEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    static byte[] Random(int length)
    {
        var bytes = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return bytes;
    }

    /// <summary>Protects a secret kept in the local settings with the Windows account (DPAPI).</summary>
    public static string Protect(string secret) => secret.Length == 0 ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }
}

sealed class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
}
