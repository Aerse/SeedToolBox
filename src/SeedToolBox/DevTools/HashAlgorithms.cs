using System;
using System.Linq;
using System.Security.Cryptography;

namespace SeedToolBox.DevTools;

/// <summary>SHA3-256 (FIPS 202) on a plain Keccak-f[1600] sponge.</summary>
sealed class Sha3_256 : HashAlgorithm
{
    const int Rate = 136;
    static readonly ulong[] RoundConstants =
    {
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000, 0x000000000000808B, 0x0000000080000001,
        0x8000000080008081, 0x8000000000008009, 0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003, 0x8000000000008002, 0x8000000000000080,
        0x000000000000800A, 0x800000008000000A, 0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    };
    static readonly int[] Rho = { 1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44 };
    static readonly int[] Pi = { 10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1 };

    readonly ulong[] _state = new ulong[25];
    readonly byte[] _block = new byte[Rate];
    int _filled;

    public Sha3_256() { HashSizeValue = 256; }

    public override void Initialize()
    {
        Array.Clear(_state, 0, _state.Length);
        _filled = 0;
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        while (cbSize > 0)
        {
            int n = Math.Min(cbSize, Rate - _filled);
            Buffer.BlockCopy(array, ibStart, _block, _filled, n);
            _filled += n;
            ibStart += n;
            cbSize -= n;
            if (_filled == Rate) { Absorb(); _filled = 0; }
        }
    }

    protected override byte[] HashFinal()
    {
        Array.Clear(_block, _filled, Rate - _filled);
        _block[_filled] ^= 0x06;
        _block[Rate - 1] ^= 0x80;
        Absorb();
        var hash = new byte[32];
        for (int i = 0; i < 4; i++) BitConverter.GetBytes(_state[i]).CopyTo(hash, i * 8);
        Initialize();
        return hash;
    }

    void Absorb()
    {
        for (int i = 0; i < Rate / 8; i++) _state[i] ^= BitConverter.ToUInt64(_block, i * 8);
        Permute(_state);
    }

    static ulong Rotl(ulong x, int n) => (x << n) | (x >> (64 - n));

    static void Permute(ulong[] st)
    {
        var bc = new ulong[5];
        for (int round = 0; round < 24; round++)
        {
            for (int i = 0; i < 5; i++) bc[i] = st[i] ^ st[i + 5] ^ st[i + 10] ^ st[i + 15] ^ st[i + 20];
            for (int i = 0; i < 5; i++)
            {
                ulong t = bc[(i + 4) % 5] ^ Rotl(bc[(i + 1) % 5], 1);
                for (int j = 0; j < 25; j += 5) st[j + i] ^= t;
            }
            ulong last = st[1];
            for (int i = 0; i < 24; i++)
            {
                int j = Pi[i];
                ulong temp = st[j];
                st[j] = Rotl(last, Rho[i]);
                last = temp;
            }
            for (int j = 0; j < 25; j += 5)
            {
                for (int i = 0; i < 5; i++) bc[i] = st[j + i];
                for (int i = 0; i < 5; i++) st[j + i] ^= ~bc[(i + 1) % 5] & bc[(i + 2) % 5];
            }
            st[0] ^= RoundConstants[round];
        }
    }
}

/// <summary>xxHash64 with seed 0; the hash bytes are the 64-bit value in big-endian order, as printed by xxhsum.</summary>
sealed class XxHash64 : HashAlgorithm
{
    const ulong P1 = 11400714785074694791, P2 = 14029467366897019727, P3 = 1609587929392839161, P4 = 9650029242287828579, P5 = 2870177450012600261;

    ulong _v1, _v2, _v3, _v4, _total;
    readonly byte[] _buffer = new byte[32];
    int _filled;

    public XxHash64()
    {
        HashSizeValue = 64;
        Initialize();
    }

    public override void Initialize()
    {
        unchecked
        {
            _v1 = P1 + P2;
            _v2 = P2;
            _v3 = 0;
            _v4 = 0 - P1;
        }
        _total = 0;
        _filled = 0;
    }

    static ulong Rotl(ulong x, int n) => (x << n) | (x >> (64 - n));
    static ulong Round(ulong acc, ulong input) { unchecked { return Rotl(acc + input * P2, 31) * P1; } }
    static ulong Merge(ulong h, ulong v) { unchecked { return (h ^ Round(0, v)) * P1 + P4; } }

    void Stripe(byte[] data, int offset)
    {
        _v1 = Round(_v1, BitConverter.ToUInt64(data, offset));
        _v2 = Round(_v2, BitConverter.ToUInt64(data, offset + 8));
        _v3 = Round(_v3, BitConverter.ToUInt64(data, offset + 16));
        _v4 = Round(_v4, BitConverter.ToUInt64(data, offset + 24));
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        _total += (ulong)cbSize;
        if (_filled > 0)
        {
            int n = Math.Min(cbSize, 32 - _filled);
            Buffer.BlockCopy(array, ibStart, _buffer, _filled, n);
            _filled += n;
            ibStart += n;
            cbSize -= n;
            if (_filled < 32) return;
            Stripe(_buffer, 0);
            _filled = 0;
        }
        while (cbSize >= 32)
        {
            Stripe(array, ibStart);
            ibStart += 32;
            cbSize -= 32;
        }
        Buffer.BlockCopy(array, ibStart, _buffer, 0, cbSize);
        _filled = cbSize;
    }

    protected override byte[] HashFinal()
    {
        unchecked
        {
            ulong h = _total >= 32
                ? Merge(Merge(Merge(Merge(Rotl(_v1, 1) + Rotl(_v2, 7) + Rotl(_v3, 12) + Rotl(_v4, 18), _v1), _v2), _v3), _v4)
                : P5;
            h += _total;
            int i = 0;
            for (; i + 8 <= _filled; i += 8) h = Rotl(h ^ Round(0, BitConverter.ToUInt64(_buffer, i)), 27) * P1 + P4;
            if (i + 4 <= _filled) { h = Rotl(h ^ (BitConverter.ToUInt32(_buffer, i) * P1), 23) * P2 + P3; i += 4; }
            for (; i < _filled; i++) h = Rotl(h ^ (_buffer[i] * P5), 11) * P1;
            h ^= h >> 33;
            h *= P2;
            h ^= h >> 29;
            h *= P3;
            h ^= h >> 32;
            Initialize();
            return BigEndian(h, 8);
        }
    }

    public static byte[] BigEndian(ulong value, int bytes)
    {
        var result = new byte[bytes];
        for (int i = bytes - 1; i >= 0; i--) { result[i] = (byte)value; value >>= 8; }
        return result;
    }
}

/// <summary>CRC-32 (IEEE, as in zip); the hash bytes are big-endian.</summary>
sealed class Crc32Hash : HashAlgorithm
{
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    uint _crc = 0xFFFFFFFF;

    public Crc32Hash() { HashSizeValue = 32; }

    public override void Initialize() => _crc = 0xFFFFFFFF;

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        uint crc = _crc;
        for (int i = ibStart; i < ibStart + cbSize; i++) crc = Table[(crc ^ array[i]) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    protected override byte[] HashFinal()
    {
        var result = XxHash64.BigEndian(_crc ^ 0xFFFFFFFF, 4);
        Initialize();
        return result;
    }
}

/// <summary>CRC-64 with the ECMA-182 polynomial in the reflected form used by xz and 7-Zip (CRC-64/XZ); big-endian bytes.</summary>
sealed class Crc64Hash : HashAlgorithm
{
    static readonly ulong[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        ulong c = (ulong)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xC96C5795D7870F42 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    ulong _crc = ulong.MaxValue;

    public Crc64Hash() { HashSizeValue = 64; }

    public override void Initialize() => _crc = ulong.MaxValue;

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        ulong crc = _crc;
        for (int i = ibStart; i < ibStart + cbSize; i++) crc = Table[(crc ^ array[i]) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    protected override byte[] HashFinal()
    {
        var result = XxHash64.BigEndian(_crc ^ ulong.MaxValue, 8);
        Initialize();
        return result;
    }
}
