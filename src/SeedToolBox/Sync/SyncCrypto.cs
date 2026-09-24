using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SeedToolBox.Sync;

/// <summary>
/// Encrypts what goes to the WebDAV server with the sync password: PBKDF2-SHA256 key, AES-256-CBC, HMAC-SHA256.
/// Layout: "STB1" | salt(16) | iv(16) | ciphertext | hmac(32) over everything before it.
/// </summary>
sealed class SyncCrypto
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("STB1");
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

    public byte[] Encrypt(byte[] plain)
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
        using (var encryptor = aes.CreateEncryptor())
        {
            var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            output.Write(cipher, 0, cipher.Length);
        }
        using var hmac = new HMACSHA256(macKey);
        var mac = hmac.ComputeHash(output.ToArray());
        output.Write(mac, 0, mac.Length);
        return output.ToArray();
    }

    public byte[] Decrypt(byte[] data)
    {
        if (data.Length < 4 + 16 + 16 + 16 + 32 || !data.Take(4).SequenceEqual(Magic))
            throw new SyncException("云端文件不是本程序加密的，或已损坏");
        var salt = data.Skip(4).Take(16).ToArray();
        var (aesKey, macKey) = Keys(salt);
        using (var hmac = new HMACSHA256(macKey))
        {
            var mac = hmac.ComputeHash(data, 0, data.Length - 32);
            if (!FixedEquals(mac, data.Skip(data.Length - 32).ToArray())) throw new SyncException("同步密码不对，和其他电脑上设置的不一样");
        }
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = data.Skip(20).Take(16).ToArray();
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 36, data.Length - 36 - 32);
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
