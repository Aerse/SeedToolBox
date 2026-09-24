using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Notes;

sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    /// <summary>Index into <see cref="NoteStore.Colors"/>.</summary>
    public int Color { get; set; }
    /// <summary>Shown as a sticky note on the desktop.</summary>
    public bool Open { get; set; }
    public bool Topmost { get; set; }
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 240;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;

    public string Title
    {
        get
        {
            var line = Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return line ?? "（空白笔记）";
        }
    }
}

sealed class NoteData
{
    public string Hotkey { get; set; } = "Alt+Shift+N";
    public string PageHotkey { get; set; } = "";
    public List<Note> Notes { get; set; } = new();
}

/// <summary>Quick notes, saved to Data\notes.json shortly after each change.</summary>
sealed class NoteStore
{
    public const string SettingsName = "notes";

    /// <summary>Background colours of the sticky notes.</summary>
    public static readonly (string Name, uint Argb)[] Colors =
    {
        ("黄", 0xFFFFF4B8), ("绿", 0xFFD9F5D0), ("蓝", 0xFFD6E9FF), ("粉", 0xFFFFDDE6), ("紫", 0xFFE8DDFF), ("灰", 0xFFEDEDED),
    };

    readonly ISettingsStore _settings;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public NoteStore(ISettingsStore settings)
    {
        _settings = settings;
        Data = settings.Load<NoteData>(SettingsName);
        _saveTimer.Tick += (_, _) => Flush();
    }

    public NoteData Data { get; private set; }
    public IReadOnlyList<Note> Notes => Data.Notes;

    /// <summary>A note was added, removed or edited.</summary>
    public event Action? Changed;

    public Note Add()
    {
        var note = new Note { Color = Data.Notes.Count % Colors.Length };
        Data.Notes.Insert(0, note);
        RequestSave();
        return note;
    }

    public void Remove(Note note)
    {
        Data.Notes.Remove(note);
        RequestSave();
    }

    /// <summary>Call after changing a note; saving is debounced.</summary>
    public void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke();
    }

    public void Flush()
    {
        _saveTimer.Stop();
        _settings.Save(SettingsName, Data);
    }

    /// <summary>Replaces everything with data loaded from elsewhere (sync or restore).</summary>
    public void Reload()
    {
        _saveTimer.Stop();
        Data = _settings.Load<NoteData>(SettingsName);
        Changed?.Invoke();
    }
}
