using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeedToolBox.Launcher;

/// <summary>Finds items across all groups by name, pinyin or target file name.</summary>
public static class ItemSearch
{
    /// <summary>Lowercase with whitespace removed, so "Ji Shi" matches like "jishi".</summary>
    public static string Normalize(string query) =>
        new string(query.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    public static List<LaunchItem> Find(IEnumerable<ItemGroup> groups, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0) return new List<LaunchItem>();

        return groups
            .SelectMany(g => g.Items)
            .Distinct()
            .Select(item => (item, score: Score(item, normalizedQuery)))
            .Where(x => x.score >= 0)
            .OrderBy(x => x.score)
            .ThenByDescending(x => x.item.RunCount)
            .ThenBy(x => x.item.Name, StringComparer.CurrentCulture)
            .Select(x => x.item)
            .ToList();
    }

    /// <summary>Lower is better; -1 means no match.</summary>
    static int Score(LaunchItem item, string q)
    {
        var name = item.Name.ToLowerInvariant();
        if (name.StartsWith(q, StringComparison.Ordinal)) return 0;
        if (name.Contains(q)) return 1;

        var (initials, full) = Pinyin.Keys(item.Name);
        if (initials.StartsWith(q, StringComparison.Ordinal) || full.StartsWith(q, StringComparison.Ordinal)) return 2;
        if (initials.Contains(q) || full.Contains(q)) return 3;

        // e.g. "notepad" finds an item renamed to 记事本
        if (TargetName(item.Path).Contains(q)) return 4;
        return -1;
    }

    static string TargetName(string path)
    {
        try
        {
            if (IconHelper.IsUrl(path)) return new Uri(path).Host.ToLowerInvariant();
            return Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "";
        }
    }
}
