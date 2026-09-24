using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace SeedToolBox.ScreenTools;

/// <summary>Colour parsing, HSL conversion and WCAG contrast.</summary>
static class ColorTools
{
    static readonly Regex Numbers = new(@"-?\d+(\.\d+)?", RegexOptions.Compiled);

    /// <summary>Accepts #RGB, #RRGGBB, #AARRGGBB (with or without #), rgb(r,g,b), hsl(h,s%,l%) and "r,g,b".</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text!.Trim().ToLowerInvariant();
        var numbers = Numbers.Matches(s);
        if (s.StartsWith("hsl"))
        {
            if (numbers.Count < 3) return false;
            double h = Num(numbers[0].Value), sat = Num(numbers[1].Value), l = Num(numbers[2].Value);
            if (sat > 1 || l > 1) { sat /= 100; l /= 100; }
            color = FromHsl(h, Clamp01(sat), Clamp01(l));
            return true;
        }
        if (s.StartsWith("rgb") || s.Contains(",") || (s.Contains(" ") && numbers.Count == 3))
        {
            if (numbers.Count < 3) return false;
            color = Color.FromRgb(Byte(numbers[0].Value), Byte(numbers[1].Value), Byte(numbers[2].Value));
            return true;
        }
        var hex = s.TrimStart('#');
        if (hex.StartsWith("0x")) hex = hex.Substring(2);
        if (!Regex.IsMatch(hex, "^[0-9a-f]+$")) return false;
        if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length == 8) hex = hex.Substring(2);
        if (hex.Length != 6) return false;
        var v = int.Parse(hex, NumberStyles.HexNumber);
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    static byte Byte(string s) => (byte)Math.Max(0, Math.Min(255, Math.Round(Num(s))));
    static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    public static string Rgb(Color c) => $"rgb({c.R}, {c.G}, {c.B})";

    public static string Hsl(Color c)
    {
        var (h, s, l) = ToHsl(c);
        return $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)";
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return (h, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x)
            : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
        static byte B(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255)));
    }

    /// <summary>WCAG 2 relative luminance.</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
