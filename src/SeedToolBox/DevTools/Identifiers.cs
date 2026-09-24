using System;
using System.Security.Cryptography;
using System.Text;

namespace SeedToolBox.DevTools;

/// <summary>UUID v1/v5/v7, ULID and NanoID generation.</summary>
static class Identifiers
{
    public static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
    public static readonly Guid UrlNamespace = new("6ba7b811-9dad-11d1-80b4-00c04fd430c8");
    public static readonly Guid OidNamespace = new("6ba7b812-9dad-11d1-80b4-00c04fd430c8");
    public static readonly Guid X500Namespace = new("6ba7b814-9dad-11d1-80b4-00c04fd430c8");

    static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
    static readonly object Gate = new();
    static long _lastV1, _lastV7Ms, _lastUlidMs;
    static int _v7Counter;
    static readonly byte[] UlidRandom = new byte[10];
    static readonly byte[] Node = NewNode(out ClockSequence);
    static readonly int ClockSequence;

    static byte[] Random(int count)
    {
        var bytes = new byte[count];
        Rng.GetBytes(bytes);
        return bytes;
    }

    /// <summary>A random node id with the multicast bit set, as RFC 9562 suggests instead of a MAC address.</summary>
    static byte[] NewNode(out int clockSequence)
    {
        var bytes = Random(8);
        bytes[0] |= 0x01;
        clockSequence = ((bytes[6] << 8) | bytes[7]) & 0x3FFF;
        return new[] { bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5] };
    }

    /// <summary>Builds a Guid from the 16 bytes in RFC (big-endian) order.</summary>
    public static Guid FromRfcBytes(byte[] b) => new(
        (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3],
        (short)((b[4] << 8) | b[5]),
        (short)((b[6] << 8) | b[7]),
        b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);

    public static byte[] RfcBytes(Guid id)
    {
        var hex = id.ToString("N");
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    static Guid Stamp(byte[] b, int version)
    {
        b[6] = (byte)((b[6] & 0x0F) | (version << 4));
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return FromRfcBytes(b);
    }

    /// <summary>Time-based UUID: 100 ns ticks since 1582-10-15, strictly increasing within this process.</summary>
    public static Guid V1()
    {
        long ticks;
        lock (Gate)
        {
            ticks = DateTime.UtcNow.Ticks - new DateTime(1582, 10, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;
            if (ticks <= _lastV1) ticks = _lastV1 + 1;
            _lastV1 = ticks;
        }
        var b = new byte[16];
        uint low = (uint)ticks;
        b[0] = (byte)(low >> 24); b[1] = (byte)(low >> 16); b[2] = (byte)(low >> 8); b[3] = (byte)low;
        b[4] = (byte)(ticks >> 40); b[5] = (byte)(ticks >> 32);
        b[6] = (byte)((ticks >> 56) & 0x0F); b[7] = (byte)(ticks >> 48);
        b[8] = (byte)(ClockSequence >> 8); b[9] = (byte)ClockSequence;
        Node.CopyTo(b, 10);
        return Stamp(b, 1);
    }

    /// <summary>Name-based UUID with SHA-1.</summary>
    public static Guid V5(Guid ns, string name)
    {
        var nsBytes = RfcBytes(ns);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[nsBytes.Length + nameBytes.Length];
        nsBytes.CopyTo(data, 0);
        nameBytes.CopyTo(data, nsBytes.Length);
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(data);
        Array.Resize(ref hash, 16);
        return Stamp(hash, 5);
    }

    /// <summary>Unix-millisecond UUID; a 12-bit counter in rand_a keeps ids from the same millisecond in order.</summary>
    public static Guid V7()
    {
        var b = Random(16);
        long ms;
        int counter;
        lock (Gate)
        {
            ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (ms <= _lastV7Ms)
            {
                ms = _lastV7Ms;
                if (++_v7Counter > 0xFFF) { ms++; _v7Counter = ((b[6] & 0x07) << 8) | b[7]; }
            }
            else _v7Counter = ((b[6] & 0x07) << 8) | b[7];
            _lastV7Ms = ms;
            counter = _v7Counter;
        }
        for (int i = 0; i < 6; i++) b[i] = (byte)(ms >> (40 - i * 8));
        b[6] = (byte)(counter >> 8);
        b[7] = (byte)counter;
        return Stamp(b, 7);
    }

    const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>ULID: 48-bit Unix milliseconds and 80 random bits, monotonic within a millisecond.</summary>
    public static string Ulid()
    {
        var bytes = new byte[16];
        lock (Gate)
        {
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (ms <= _lastUlidMs)
            {
                ms = _lastUlidMs;
                int i = UlidRandom.Length - 1;
                while (i >= 0 && ++UlidRandom[i] == 0) i--;
                if (i < 0) { ms++; Rng.GetBytes(UlidRandom); }
            }
            else Rng.GetBytes(UlidRandom);
            _lastUlidMs = ms;
            for (int i = 0; i < 6; i++) bytes[i] = (byte)(ms >> (40 - i * 8));
            UlidRandom.CopyTo(bytes, 6);
        }
        return EncodeUlid(bytes);
    }

    public static string EncodeUlid(byte[] bytes)
    {
        ulong hi = 0, lo = 0;
        for (int i = 0; i < 8; i++) hi = (hi << 8) | bytes[i];
        for (int i = 8; i < 16; i++) lo = (lo << 8) | bytes[i];
        var chars = new char[26];
        for (int i = 25; i >= 0; i--)
        {
            chars[i] = Crockford[(int)(lo & 31)];
            lo = (lo >> 5) | (hi << 59);
            hi >>= 5;
        }
        return new string(chars);
    }

    const string NanoAlphabet = "useandom-26T198340PX75pxJACKVERYMINDBUSHWOLF_GQZbfghjklqvwyzrict";

    /// <summary>NanoID with the standard URL-safe alphabet of 64 symbols.</summary>
    public static string NanoId(int length)
    {
        var bytes = Random(length);
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = NanoAlphabet[bytes[i] & 63];
        return new string(chars);
    }
}
