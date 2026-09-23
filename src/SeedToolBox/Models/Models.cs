using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using SeedToolBox.Services;

namespace SeedToolBox.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A launchable entry: program, shortcut, file, folder or URL.</summary>
public class LaunchItem : ObservableObject
{
    string _name = "";
    ImageSource? _icon;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";

    [JsonIgnore]
    public ImageSource? Icon => _icon ??= IconHelper.GetIcon(Path);
}

public class ItemGroup : ObservableObject
{
    string _name = "";

    public string Name { get => _name; set => Set(ref _name, value); }
    public ObservableCollection<LaunchItem> Items { get; set; } = new();
}

public class WindowSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 440;
    public double Height { get; set; } = 620;
    public bool SizeLocked { get; set; }
}

public class AppData
{
    public ObservableCollection<ItemGroup> Groups { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
}
