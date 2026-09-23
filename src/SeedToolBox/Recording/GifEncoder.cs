using System;
using System.IO;

namespace SeedToolBox.Recording;

/// <summary>A finished recording waiting to be trimmed and exported.</summary>
sealed class RecordedClip
{
    public RecordedClip(string path, int width, int height, int fps, long duration)
    {
        Path = path;
        Width = width;
        Height = height;
        Fps = fps;
        Duration = duration;
    }

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    /// <summary>100 ns units.</summary>
    public long Duration { get; }
}

/// <summary>
/// Writes an animated GIF with one global palette. Each frame stores only the rectangle that
/// changed, with unchanged pixels transparent; identical frames extend the previous delay.
/// </summary>
sealed class GifEncoder : IDisposable
{
    const byte Transparent = 255;

    readonly Stream _stream;
    readonly int _width, _height;
    byte[]? _previous;      // what the viewer currently shows
    byte[]? _pending;       // frame waiting for its delay to be known
    int _left, _top, _right, _bottom;
    long _pendingTime;
    int _writtenCentiseconds;

    public GifEncoder(Stream stream, int width, int height, byte[] palette)
    {
        _stream = stream;
        _width = width;
        _height = height;

        Write("GIF89a");
        Short(width);
        Short(height);
        _stream.WriteByte(0xF7); // global table, 8 bits, 256 entries
        _stream.WriteByte(0);
        _stream.WriteByte(0);
        _stream.Write(palette, 0, 768);

        // NETSCAPE2.0: loop forever
        _stream.Write(new byte[] { 0x21, 0xFF, 11 }, 0, 3);
        Write("NETSCAPE2.0");
        _stream.Write(new byte[] { 3, 1, 0, 0, 0 }, 0, 5);
    }

    /// <param name="indexes">One palette index per pixel (never 255).</param>
    /// <param name="time">When the frame appears, 100 ns units from the start.</param>
    public void AddFrame(byte[] indexes, long time)
    {
        if (_pending != null)
        {
            if (Same(_pending, indexes)) return;
            Flush(time);
        }
        _pending = indexes;
        _pendingTime = time;
    }

    /// <param name="end">Total length, which sets the last frame's delay.</param>
    public void Finish(long end)
    {
        if (_pending != null) Flush(Math.Max(end, _pendingTime + 1));
        _stream.WriteByte(0x3B);
        _stream.Flush();
    }

    static bool Same(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    void Flush(long until)
    {
        var frame = _pending!;
        _pending = null;

        // Rounding the cumulative time keeps the total exact; browsers treat < 2 cs as slow
        int endCentiseconds = (int)(until / 100_000);
        int delay = Math.Max(2, endCentiseconds - _writtenCentiseconds);
        _writtenCentiseconds += delay;

        byte[] data;
        if (_previous == null)
        {
            _left = 0;
            _top = 0;
            _right = _width;
            _bottom = _height;
            data = frame;
        }
        else
        {
            ChangedRect(frame);
            data = new byte[(_right - _left) * (_bottom - _top)];
            int k = 0;
            for (int y = _top; y < _bottom; y++)
            {
                for (int x = _left; x < _right; x++)
                {
                    int i = y * _width + x;
                    data[k++] = frame[i] == _previous[i] ? Transparent : frame[i];
                }
            }
        }
        _previous = frame;

        // Graphic control: disposal 1 (leave in place), transparent index
        _stream.Write(new byte[] { 0x21, 0xF9, 4, (1 << 2) | 1 }, 0, 4);
        Short(delay);
        _stream.WriteByte(Transparent);
        _stream.WriteByte(0);

        _stream.WriteByte(0x2C);
        Short(_left);
        Short(_top);
        Short(_right - _left);
        Short(_bottom - _top);
        _stream.WriteByte(0);
        Lzw.Encode(_stream, data);
    }

    void ChangedRect(byte[] frame)
    {
        int left = _width, top = _height, right = 0, bottom = 0;
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;
            for (int x = 0; x < _width; x++)
            {
                if (frame[row + x] == _previous![row + x]) continue;
                if (x < left) left = x;
                if (x >= right) right = x + 1;
                if (y < top) top = y;
                bottom = y + 1;
            }
        }
        // Different indexes are guaranteed by the caller, but keep a 1-pixel frame just in case
        if (right == 0)
        {
            left = top = 0;
            right = bottom = 1;
        }
        _left = left;
        _top = top;
        _right = right;
        _bottom = bottom;
    }

    void Short(int value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
    }

    void Write(string ascii)
    {
        foreach (char c in ascii) _stream.WriteByte((byte)c);
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>GIF variable-length LZW with 8-bit input.</summary>
    static class Lzw
    {
        const int MaxCodes = 4096, HashSize = 5003;

        public static void Encode(Stream stream, byte[] data)
        {
            stream.WriteByte(8);
            var output = new BlockWriter(stream);
            var hashKeys = new int[HashSize];
            var hashCodes = new int[HashSize];

            const int clear = 256, end = 257;
            int bits = 9, next = 258;
            void Reset()
            {
                Array.Clear(hashKeys, 0, HashSize);
                bits = 9;
                next = 258;
            }

            output.Write(clear, bits);
            if (data.Length == 0)
            {
                output.Write(end, bits);
                output.Finish();
                return;
            }

            int prefix = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                int c = data[i];
                // Key 0 means empty, so store key + 1
                int key = ((prefix << 8) | c) + 1;
                int h = ((c << 12) ^ prefix) % HashSize;
                while (hashKeys[h] != 0 && hashKeys[h] != key) h = (h + 1) % HashSize;
                if (hashKeys[h] == key)
                {
                    prefix = hashCodes[h];
                    continue;
                }

                output.Write(prefix, bits);
                prefix = c;
                if (next < MaxCodes)
                {
                    hashKeys[h] = key;
                    hashCodes[h] = next;
                    // The decoder grows its width one code later than the encoder assigns it
                    if (next == 1 << bits) bits++;
                    next++;
                }
                else
                {
                    output.Write(clear, bits);
                    Reset();
                }
            }
            output.Write(prefix, bits);
            // The decoder adds an entry for this last code too, which may widen the end code
            if (next < MaxCodes && next == 1 << bits) bits++;
            output.Write(end, bits);
            output.Finish();
        }
    }

    /// <summary>Packs codes LSB-first into 255-byte sub-blocks.</summary>
    sealed class BlockWriter
    {
        readonly Stream _stream;
        readonly byte[] _block = new byte[255];
        int _count, _buffer, _bitCount;

        public BlockWriter(Stream stream) => _stream = stream;

        public void Write(int code, int bits)
        {
            _buffer |= code << _bitCount;
            _bitCount += bits;
            while (_bitCount >= 8)
            {
                Byte((byte)_buffer);
                _buffer >>= 8;
                _bitCount -= 8;
            }
        }

        void Byte(byte b)
        {
            _block[_count++] = b;
            if (_count == 255) FlushBlock();
        }

        void FlushBlock()
        {
            if (_count == 0) return;
            _stream.WriteByte((byte)_count);
            _stream.Write(_block, 0, _count);
            _count = 0;
        }

        public void Finish()
        {
            if (_bitCount > 0) Byte((byte)_buffer);
            _buffer = _bitCount = 0;
            FlushBlock();
            _stream.WriteByte(0);
        }
    }
}
