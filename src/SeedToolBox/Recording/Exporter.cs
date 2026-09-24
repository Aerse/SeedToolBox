using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SeedToolBox.Recording;

/// <summary>Turns a recording into the final file: a trimmed MP4, or a GIF. Times are in 100 ns units.</summary>
static class Exporter
{
    const long Second = 10_000_000;

    /// <summary>Re-encodes [start, end) of the recording; the untouched full range is just copied.</summary>
    public static void Mp4(RecordedClip source, string path, long start, long end, int quality, Action<double> progress, CancellationToken cancel)
    {
        if (start <= 0 && end >= source.Duration)
        {
            File.Copy(source.Path, path, true);
            return;
        }

        using var reader = new VideoReader(source.Path, source.Width, source.Height, true);
        using var writer = Mp4Writer.Create(path, source.Width, source.Height, source.Fps,
            Mp4Writer.Bitrate(source.Width, source.Height, source.Fps, quality), reader.HasAudio);
        reader.Seek(start);

        bool videoDone = false, audioDone = !reader.HasAudio;
        // The frame shown at `start` may begin a little before it; hold each frame until the next arrives
        long pendingTime = -1;
        byte[]? pending = null;
        int row = source.Width * 4;

        void Flush(long until)
        {
            if (pending == null) return;
            long t = Math.Max(0, pendingTime - start);
            long duration = Math.Max(1, Math.Min(until, end) - start - t);
            var handle = GCHandle.Alloc(pending, GCHandleType.Pinned);
            try { writer.WriteFrame(handle.AddrOfPinnedObject(), row, t, duration); }
            finally { handle.Free(); }
        }

        while (!(videoDone && audioDone))
        {
            cancel.ThrowIfCancellationRequested();
            bool more = reader.Read(
                (time, pixels, stride) =>
                {
                    if (videoDone) return;
                    if (time >= end)
                    {
                        Flush(end);
                        pending = null;
                        videoDone = true;
                        return;
                    }
                    // A frame entirely before the start only matters if nothing later covers it
                    if (pending != null && time > start) Flush(time);
                    pending ??= new byte[row * source.Height];
                    Copy(pixels, stride, pending, row, source.Height);
                    pendingTime = time;
                    progress(Math.Max(0, (double)(time - start) / (end - start)));
                },
                (time, samples, frames) =>
                {
                    if (audioDone) return;
                    long chunkEnd = time + frames * Second / Mp4Writer.AudioRate;
                    if (chunkEnd <= start) return;
                    if (time >= end)
                    {
                        audioDone = true;
                        return;
                    }
                    // Cut the chunk to [start, end) with sample precision
                    int skip = (int)Math.Max(0, (start - time) * Mp4Writer.AudioRate / Second);
                    int keep = (int)Math.Min(frames - skip, (end - Math.Max(time, start)) * Mp4Writer.AudioRate / Second);
                    if (keep <= 0) return;
                    if (skip > 0) Array.Copy(samples, skip * Mp4Writer.AudioChannels, samples, 0, keep * Mp4Writer.AudioChannels);
                    writer.WriteAudio(samples, keep, Math.Max(0, time - start));
                });
            if (!more) break;
        }
        if (!videoDone) Flush(end);
        writer.Finish();
    }

    /// <summary>
    /// Exports [start, end) as a GIF sampled at <paramref name="fps"/>. <paramref name="edits"/> remove sampled frames or give them
    /// a fixed delay; they match by source time and later edits override earlier ones.
    /// </summary>
    public static void Gif(RecordedClip source, string path, long start, long end, int fps, double scale, IReadOnlyList<GifFrameEdit>? edits, Action<double> progress, CancellationToken cancel)
    {
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        edits ??= Array.Empty<GifFrameEdit>();

        GifFrameEdit? EditAt(long time)
        {
            for (int i = edits.Count - 1; i >= 0; i--)
                if (time >= edits[i].Start && time < edits[i].End) return edits[i];
            return null;
        }

        // Pass 1: gather colors for one palette shared by every kept frame
        var quantizer = new Quantizer();
        int kept = 0;
        ForEachGifFrame(source, start, end, fps, width, height, cancel, (time, pixels) =>
        {
            if (EditAt(time) is not { Remove: true })
            {
                quantizer.Add(pixels);
                kept++;
            }
            progress(0.4 * (time - start) / (end - start));
        });
        if (kept == 0) throw new InvalidOperationException("所有帧都已被删除");
        quantizer.Build();

        // Pass 2: encode, laying kept frames out back to back with their delays
        using var gif = new GifEncoder(File.Create(path), width, height, quantizer.Palette);
        double output = 0, frame = (double)Second / fps;
        ForEachGifFrame(source, start, end, fps, width, height, cancel, (time, pixels) =>
        {
            var edit = EditAt(time);
            if (edit is not { Remove: true })
            {
                gif.AddFrame(quantizer.Map(pixels), (long)output);
                output += edit is { DelayMs: > 0 } ? edit.DelayMs * 10_000.0 : frame;
            }
            progress(0.4 + 0.6 * (time - start) / (end - start));
        });
        gif.Finish((long)output);
    }

    /// <summary>Samples the video at a fixed rate, delivering scaled BGRA frames.</summary>
    static void ForEachGifFrame(RecordedClip source, long start, long end, int fps, int width, int height, CancellationToken cancel, Action<long, byte[]> onFrame)
    {
        using var reader = new VideoReader(source.Path, source.Width, source.Height, false);
        reader.Seek(start);

        int sourceRow = source.Width * 4;
        var frame = new byte[sourceRow * source.Height];
        bool hasFrame = false;
        long index = 0;
        using var scaler = width == source.Width && height == source.Height ? null : new Scaler(source.Width, source.Height, width, height);

        // Every output time up to `until` shows the frame held so far
        void Emit(long until)
        {
            while (hasFrame)
            {
                long t = start + index * Second / fps;
                if (t >= until || t >= end) return;
                cancel.ThrowIfCancellationRequested();
                onFrame(t, scaler?.Scale(frame) ?? (byte[])frame.Clone());
                index++;
            }
        }

        bool done = false;
        while (!done && reader.Read((time, pixels, stride) =>
        {
            if (time >= end)
            {
                done = true;
                return;
            }
            Emit(time);
            Copy(pixels, stride, frame, sourceRow, source.Height);
            hasFrame = true;
        }, null))
        {
        }
        Emit(end);
    }

    static void Copy(IntPtr pixels, int stride, byte[] target, int row, int height)
    {
        for (int y = 0; y < height; y++)
            Marshal.Copy(pixels + y * stride, target, y * row, row);
    }

    /// <summary>High-quality downscaling through GDI+.</summary>
    sealed class Scaler : IDisposable
    {
        readonly int _sourceWidth, _sourceHeight, _width, _height;
        readonly Bitmap _target;
        readonly Graphics _graphics;

        public Scaler(int sourceWidth, int sourceHeight, int width, int height)
        {
            _sourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;
            _width = width;
            _height = height;
            _target = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            _graphics = Graphics.FromImage(_target);
            _graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            _graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            _graphics.CompositingMode = CompositingMode.SourceCopy;
        }

        public byte[] Scale(byte[] pixels)
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var source = new Bitmap(_sourceWidth, _sourceHeight, _sourceWidth * 4, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
                using var attributes = new ImageAttributes();
                // Clamp at the edges instead of blending in transparent black
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                _graphics.DrawImage(source, new Rectangle(0, 0, _width, _height), 0, 0, _sourceWidth, _sourceHeight, GraphicsUnit.Pixel, attributes);
            }
            finally
            {
                handle.Free();
            }

            var result = new byte[_width * _height * 4];
            var data = _target.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { Copy(data.Scan0, data.Stride, result, _width * 4, _height); }
            finally { _target.UnlockBits(data); }
            return result;
        }

        public void Dispose()
        {
            _graphics.Dispose();
            _target.Dispose();
        }
    }
}

/// <summary>A GIF frame edit over source times [Start, End): remove the frames, or show each for DelayMs.</summary>
sealed class GifFrameEdit
{
    public long Start { get; set; }
    public long End { get; set; }
    public bool Remove { get; set; }
    public int DelayMs { get; set; }
}

/// <summary>Median-cut palette of up to 255 colors (index 255 is left for transparency).</summary>
sealed class Quantizer
{
    const int Bins = 32768;
    readonly long[] _count = new long[Bins], _r = new long[Bins], _g = new long[Bins], _b = new long[Bins];
    readonly byte[] _lookup = new byte[Bins];

    public byte[] Palette { get; } = new byte[256 * 3];

    static int Bin(byte[] p, int i) => ((p[i + 2] >> 3) << 10) | ((p[i + 1] >> 3) << 5) | (p[i] >> 3);

    public void Add(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int bin = Bin(bgra, i);
            _count[bin]++;
            _b[bin] += bgra[i];
            _g[bin] += bgra[i + 1];
            _r[bin] += bgra[i + 2];
        }
    }

    public void Build()
    {
        var used = new List<int>();
        for (int i = 0; i < Bins; i++) if (_count[i] > 0) used.Add(i);
        if (used.Count == 0) used.Add(0);

        var boxes = new List<List<int>> { used };
        while (boxes.Count < 255)
        {
            // Split the box with the most pixels spread over the widest range
            int best = -1;
            double bestScore = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                double score = Pixels(boxes[i]) * (double)Range(boxes[i], out _);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            if (best < 0) break;

            var box = boxes[best];
            Range(box, out int channel);
            int shift = channel == 0 ? 10 : channel == 1 ? 5 : 0;
            box.Sort((a, b) => ((a >> shift) & 31).CompareTo((b >> shift) & 31));
            long half = Pixels(box) / 2, seen = 0;
            int cut = 1;
            for (int i = 0; i < box.Count - 1; i++)
            {
                seen += _count[box[i]];
                cut = i + 1;
                if (seen >= half) break;
            }
            boxes[best] = box.GetRange(0, cut);
            boxes.Add(box.GetRange(cut, box.Count - cut));
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            long n = 0, r = 0, g = 0, b = 0;
            foreach (int bin in boxes[i])
            {
                n += _count[bin];
                r += _r[bin];
                g += _g[bin];
                b += _b[bin];
            }
            n = Math.Max(1, n);
            Palette[i * 3] = (byte)(r / n);
            Palette[i * 3 + 1] = (byte)(g / n);
            Palette[i * 3 + 2] = (byte)(b / n);
        }

        // Nearest palette entry for every 15-bit color
        for (int bin = 0; bin < Bins; bin++)
        {
            int r = ((bin >> 10) << 3) + 4, g = (((bin >> 5) & 31) << 3) + 4, b = ((bin & 31) << 3) + 4;
            if (_count[bin] > 0)
            {
                r = (int)(_r[bin] / _count[bin]);
                g = (int)(_g[bin] / _count[bin]);
                b = (int)(_b[bin] / _count[bin]);
            }
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                int dr = r - Palette[i * 3], dg = g - Palette[i * 3 + 1], db = b - Palette[i * 3 + 2];
                int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            _lookup[bin] = (byte)best;
        }
    }

    long Pixels(List<int> box)
    {
        long n = 0;
        foreach (int bin in box) n += _count[bin];
        return n;
    }

    /// <summary>Widest channel range in 5-bit units; channel 0 = red, 1 = green, 2 = blue.</summary>
    static int Range(List<int> box, out int channel)
    {
        int rMin = 31, rMax = 0, gMin = 31, gMax = 0, bMin = 31, bMax = 0;
        foreach (int bin in box)
        {
            int r = bin >> 10, g = (bin >> 5) & 31, b = bin & 31;
            if (r < rMin) rMin = r;
            if (r > rMax) rMax = r;
            if (g < gMin) gMin = g;
            if (g > gMax) gMax = g;
            if (b < bMin) bMin = b;
            if (b > bMax) bMax = b;
        }
        int rr = rMax - rMin, gr = gMax - gMin, br = bMax - bMin;
        channel = gr >= rr && gr >= br ? 1 : rr >= br ? 0 : 2;
        return Math.Max(rr, Math.Max(gr, br));
    }

    public byte[] Map(byte[] bgra)
    {
        var result = new byte[bgra.Length / 4];
        for (int i = 0, j = 0; j < result.Length; i += 4, j++) result[j] = _lookup[Bin(bgra, i)];
        return result;
    }
}
