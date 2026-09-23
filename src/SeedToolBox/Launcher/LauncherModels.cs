using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Newtonsoft.Json;

namespace SeedToolBox.Launcher;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A launchable entry: program, shortcut, file, folder or URL.</summary>
public class LaunchItem : ObservableObject
{
    string _name = "";
    string _path = "";
    string _iconPath = "";
    string _remarks = "";
    ImageSource? _icon;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Path { get => _path; set { if (Set(ref _path, value)) ResetDisplay(); } }
    public string Arguments { get; set; } = "";
    /// <summary>Empty means the target's own folder.</summary>
    public string WorkingDirectory { get; set; } = "";
    public bool RunAsAdmin { get; set; }
    /// <summary>Custom icon: image/.ico file, or an exe/dll whose icon is used. Empty means the target's icon.</summary>
    public string IconPath { get => _iconPath; set { if (Set(ref _iconPath, value)) ResetDisplay(); } }
    public string Remarks { get => _remarks; set { if (Set(ref _remarks, value)) Raise(nameof(ToolTip)); } }

    [JsonIgnore]
    public ImageSource? Icon => _icon ??= IconHelper.GetIcon(Path, IconPath);

    [JsonIgnore]
    public string ToolTip => Remarks.Length > 0 ? $"{Remarks}\n{Path}" : Path;

    bool _isDragging;

    /// <summary>True while this entry is being dragged (shown faded in place).</summary>
    [JsonIgnore]
    public bool IsDragging { get => _isDragging; set => Set(ref _isDragging, value); }

    void ResetDisplay()
    {
        _icon = null;
        Raise(nameof(Icon));
        Raise(nameof(ToolTip));
    }
}

public class ItemGroup : ObservableObject
{
    string _name = "";

    public string Name { get => _name; set => Set(ref _name, value); }
    public ObservableCollection<LaunchItem> Items { get; set; } = new();

    bool _isDragging;

    /// <summary>True while this entry is being dragged (shown faded in place).</summary>
    [JsonIgnore]
    public bool IsDragging { get => _isDragging; set => Set(ref _isDragging, value); }

    bool _isDropTarget;

    /// <summary>True while an item is dragged over this group.</summary>
    [JsonIgnore]
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }
}

public class WindowSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 440;
    public double Height { get; set; } = 620;
    public bool SizeLocked { get; set; }
}

public class LauncherData
{
    public const string SettingsName = "launcher";

    public ObservableCollection<ItemGroup> Groups { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    /// <summary>Global hotkey that shows/hides the main window, e.g. "Ctrl+Q". Empty disables it.</summary>
    public string Hotkey { get; set; } = "Ctrl+Q";
}
