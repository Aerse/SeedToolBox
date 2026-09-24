using System;

namespace SeedToolBox.Launcher;

/// <summary>A search box prefix command shown above the item results, e.g. a calculator result.</summary>
public sealed class SearchCommand : ObservableObject
{
    string _subtitle;
    bool _isHighlighted;

    public SearchCommand(string glyph, string title, string subtitle, Action? run)
    {
        Glyph = glyph;
        Title = title;
        _subtitle = subtitle;
        Run = run;
    }

    public string Glyph { get; }
    public string Title { get; }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    /// <summary>Null when there is nothing to do yet, e.g. an incomplete expression.</summary>
    public Action? Run { get; }
    /// <summary>Run by Ctrl+Enter, e.g. opening a found file's folder.</summary>
    public Action? RunAlt { get; set; }
    public bool IsHighlighted { get => _isHighlighted; set => Set(ref _isHighlighted, value); }
}
