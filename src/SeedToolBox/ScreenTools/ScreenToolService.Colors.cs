using System;
using System.Linq;
using System.Windows.Media;

namespace SeedToolBox.ScreenTools;

public sealed partial class ScreenToolService
{
    public const int ColorHistoryCount = 30;

    /// <summary>Raised when the colour history or the favourites change.</summary>
    public event Action? ColorsChanged;

    public void AddColor(Color color)
    {
        var hex = ColorTools.Hex(color);
        var list = Settings.ColorHistory;
        list.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex);
        if (list.Count > ColorHistoryCount) list.RemoveRange(ColorHistoryCount, list.Count - ColorHistoryCount);
        SaveSettings();
        ColorsChanged?.Invoke();
    }

    public bool IsFavorite(Color color) =>
        Settings.FavoriteColors.Any(c => string.Equals(c, ColorTools.Hex(color), StringComparison.OrdinalIgnoreCase));

    public void SetFavorite(Color color, bool favorite)
    {
        var hex = ColorTools.Hex(color);
        Settings.FavoriteColors.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        if (favorite) Settings.FavoriteColors.Add(hex);
        SaveSettings();
        ColorsChanged?.Invoke();
    }

    public void ClearColorHistory()
    {
        Settings.ColorHistory.Clear();
        SaveSettings();
        ColorsChanged?.Invoke();
    }
}
