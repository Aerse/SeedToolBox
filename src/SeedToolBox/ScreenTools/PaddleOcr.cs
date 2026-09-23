using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;

namespace SeedToolBox.ScreenTools;

/// <summary>
/// PaddleOCR (PP-OCRv4 Chinese/English) run offline with ONNX Runtime: a DB model finds the text lines,
/// a CRNN model reads each one. Only horizontal text is handled, which is all a screen has.
/// </summary>
static class PaddleOcr
{
    static readonly string Folder = Path.Combine(AppPaths.Base, "Ocr");
    static readonly object Lock = new();
    static bool? _available;
    static InferenceSession? _detector, _recognizer;
    static string[] _keys = new string[0];

    /// <summary>False when the models or the runtime are missing or won't load (e.g. an old Windows).</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (Lock)
            {
                if (_available == null)
                {
                    try
                    {
                        _available = File.Exists(Path.Combine(Folder, "det.onnx")) && Load();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("PaddleOCR unavailable", ex);
                        _available = false;
                    }
                }
                return _available.Value;
            }
        }
    }

    // Separate method so a missing runtime fails here, inside the try, rather than when the caller is compiled
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool Load()
    {
        var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount) };
        _detector = new InferenceSession(Path.Combine(Folder, "det.onnx"), options);
        _recognizer = new InferenceSession(Path.Combine(Folder, "rec.onnx"), options);
        // Index 0 is the CTC blank, then the dictionary, then a space
        _keys = new[] { "" }.Concat(File.ReadAllLines(Path.Combine(Folder, "keys.txt"))).Concat(new[] { " " }).ToArray();
        return true;
    }

    /// <summary>Text lines with their boxes in image pixels. Takes a while: call off the UI thread.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static List<(Rect Box, string Text)> Recognize(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 3;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var result = new List<(Rect, string)>();
        lock (Lock)
        {
            foreach (var box in Detect(pixels, width, height))
            {
                var text = Read(pixels, width, box);
                if (text.Trim().Length > 0) result.Add((box, text));
            }
        }
        return result;
    }

    static List<Rect> Detect(byte[] pixels, int width, int height)
    {
        // Small text is found far more reliably enlarged; sides must be multiples of 32
        double ratio = Math.Min(2, 1920.0 / Math.Max(width, height));
        int w = Math.Max(32, (int)Math.Round(width * ratio / 32) * 32);
        int h = Math.Max(32, (int)Math.Round(height * ratio / 32) * 32);
        double rx = (double)w / width, ry = (double)h / height;

        var mean = new[] { 0.485f, 0.456f, 0.406f };
        var std = new[] { 0.229f, 0.224f, 0.225f };
        var input = new DenseTensor<float>(new[] { 1, 3, h, w });
        var buffer = input.Buffer.Span;
        var color = new float[3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Sample(pixels, width, height, (x + 0.5) / rx - 0.5, (y + 0.5) / ry - 0.5, color);
                for (int c = 0; c < 3; c++)
                    buffer[(c * h + y) * w + x] = (color[c] / 255f - mean[c]) / std[c];
            }
        }

        float[] map;
        using (var output = _detector!.Run(new[] { NamedOnnxValue.CreateFromTensor(_detector.InputMetadata.Keys.First(), input) }))
            map = output.First().AsEnumerable<float>().ToArray();

        // Pixels above the threshold are text; each connected region is one line
        var boxes = new List<Rect>();
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int start = 0; start < map.Length; start++)
        {
            if (seen[start] || map[start] < 0.3f) continue;
            int left = w, top = h, right = 0, bottom = 0, count = 0;
            double score = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                score += map[i];
                count++;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            if (score / count < 0.5 || Math.Min(right - left, bottom - top) < 2) continue;

            // The model marks a shrunken core of each line; grow it back (DB "unclip", ratio 1.5)
            double bw = right - left + 1, bh = bottom - top + 1;
            double d = bw * bh * 1.5 / (2 * (bw + bh));
            double x0 = Math.Max(0, (left - d) / rx), y0 = Math.Max(0, (top - d) / ry);
            double x1 = Math.Min(width, (right + 1 + d) / rx), y1 = Math.Min(height, (bottom + 1 + d) / ry);
            if (x1 - x0 >= 3 && y1 - y0 >= 3) boxes.Add(new Rect(x0, y0, x1 - x0, y1 - y0));
        }
        return boxes;

        void Visit(int i)
        {
            if (seen[i] || map[i] < 0.3f) return;
            seen[i] = true;
            stack.Push(i);
        }
    }

    const int LineHeight = 48;

    /// <summary>One line, read with greedy CTC decoding. Empty if the model isn't confident.</summary>
    static string Read(byte[] pixels, int width, Rect box)
    {
        int height = pixels.Length / 3 / width;
        double scale = LineHeight / box.Height;
        int w = Math.Max(LineHeight / 4, Math.Min(4000, (int)Math.Ceiling(box.Width * scale)));
        var input = new DenseTensor<float>(new[] { 1, 3, LineHeight, w });
        var buffer = input.Buffer.Span;
        var color = new float[3];
        for (int y = 0; y < LineHeight; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Sample(pixels, width, height, box.X + (x + 0.5) / scale - 0.5, box.Y + (y + 0.5) / scale - 0.5, color);
                for (int c = 0; c < 3; c++)
                    buffer[(c * LineHeight + y) * w + x] = color[c] / 127.5f - 1;
            }
        }

        using var output = _recognizer!.Run(new[] { NamedOnnxValue.CreateFromTensor(_recognizer.InputMetadata.Keys.First(), input) });
        var probabilities = output.First().AsTensor<float>();
        int steps = probabilities.Dimensions[1], classes = probabilities.Dimensions[2];
        var text = new System.Text.StringBuilder();
        int last = 0, found = 0;
        double confidence = 0;
        for (int t = 0; t < steps; t++)
        {
            int best = 0;
            float max = float.MinValue;
            for (int k = 0; k < classes; k++)
            {
                float p = probabilities[0, t, k];
                if (p > max) { max = p; best = k; }
            }
            if (best != 0 && best != last && best < _keys.Length)
            {
                text.Append(_keys[best]);
                confidence += max;
                found++;
            }
            last = best;
        }
        return found > 0 && confidence / found >= 0.5 ? text.ToString() : "";
    }

    /// <summary>Bilinear sample at (x, y), clamped to the image.</summary>
    static void Sample(byte[] pixels, int width, int height, double x, double y, float[] color)
    {
        x = Math.Max(0, Math.Min(width - 1, x));
        y = Math.Max(0, Math.Min(height - 1, y));
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(width - 1, x0 + 1), y1 = Math.Min(height - 1, y0 + 1);
        float fx = (float)(x - x0), fy = (float)(y - y0);
        int a = (y0 * width + x0) * 3, b = (y0 * width + x1) * 3, c = (y1 * width + x0) * 3, d = (y1 * width + x1) * 3;
        for (int i = 0; i < 3; i++)
        {
            float top = pixels[a + i] + (pixels[b + i] - pixels[a + i]) * fx;
            float bottom = pixels[c + i] + (pixels[d + i] - pixels[c + i]) * fx;
            color[i] = top + (bottom - top) * fy;
        }
    }
}
