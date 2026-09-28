using System;
using System.IO;
using System.Text;
using System.Windows;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Launcher;

/// <summary>Chinese character to pinyin lookup, backed by Assets/pinyin.bin (see tools/gen_pinyin.py).</summary>
public static class Pinyin
{
    const int First = 0x4E00, Last = 0x9FFF;

    static string[]? _syllables;
    static ushort[]? _table;
    static bool _loaded;

    /// <summary>Toneless pinyin for a character, e.g. 记 → "ji". Null if it isn't a known character.</summary>
    public static string? Of(char c)
    {
        if (c < First || c > Last) return null;
        EnsureLoaded();
        if (_table == null) return null;
        int index = _table[c - First];
        return index == 0 ? null : _syllables![index - 1];
    }

    /// <summary>
    /// Search keys: initials ("jsb") and full pinyin ("jishiben"). Letters/digits pass through lowercased;
    /// other words add only their first letter to the initials, so "Visual Studio Code" gives "vsc".
    /// </summary>
    public static (string Initials, string Full) Keys(string text)
    {
        var initials = new StringBuilder(text.Length);
        var full = new StringBuilder(text.Length * 3);
        char prev = ' ';
        foreach (var c in text)
        {
            if (Of(c) is { } py)
            {
                initials.Append(py[0]);
                full.Append(py);
            }
            else if (char.IsLetterOrDigit(c))
            {
                var lower = char.ToLowerInvariant(c);
                // A word starts after a non-letter, or at a capital in CamelCase
                bool wordStart = !char.IsLetter(prev) || Of(prev) != null || (char.IsUpper(c) && char.IsLower(prev));
                if (char.IsDigit(c) || wordStart) initials.Append(lower);
                full.Append(lower);
            }
            prev = c;
        }
        return (initials.ToString(), full.ToString());
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/pinyin.bin"))?.Stream;
            if (stream == null) return;
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            var syllables = new string[reader.ReadInt32()];
            for (int i = 0; i < syllables.Length; i++)
            {
                var sb = new StringBuilder();
                for (char ch; (ch = reader.ReadChar()) != '\n';) sb.Append(ch);
                syllables[i] = sb.ToString();
            }

            var table = new ushort[Last - First + 1];
            for (int i = 0; i < table.Length; i++) table[i] = reader.ReadUInt16();

            _syllables = syllables;
            _table = table;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load pinyin table", ex);
        }
    }
}
