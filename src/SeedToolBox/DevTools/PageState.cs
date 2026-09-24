using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Newtonsoft.Json;
using SeedToolBox.Core;
using SeedToolBox.Core.Services;

namespace SeedToolBox.DevTools;

/// <summary>
/// Remembers what was typed and chosen on the tool pages between sessions.
/// Editable text boxes, check boxes and combo boxes are stored by their order on the page.
/// </summary>
static class PageState
{
    const int MaxText = 100_000;
    static readonly string FilePath = Path.Combine(AppPaths.Data, "pages.json");
    static Dictionary<string, List<string?>>? _all;
    static DispatcherTimer? _timer;

    static Dictionary<string, List<string?>> All
    {
        get
        {
            if (_all != null) return _all;
            try { _all = File.Exists(FilePath) ? JsonConvert.DeserializeObject<Dictionary<string, List<string?>>>(File.ReadAllText(FilePath)) : null; }
            catch (Exception ex) { Log.Error("Reading page state failed", ex); }
            return _all ??= new();
        }
    }

    /// <summary>Restores <paramref name="page"/> once it is loaded, and saves it whenever it changes.</summary>
    public static void Attach(FrameworkElement page, string id)
    {
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            page.Loaded -= OnLoaded;
            var controls = Controls(page).ToList();
            if (All.TryGetValue(id, out var saved) && saved.Count == controls.Count)
                for (int i = 0; i < controls.Count; i++) Restore(controls[i], saved[i]);
            foreach (var c in controls)
            {
                if (c is TextBox box) box.TextChanged += (_, _) => Changed(page, id);
                else if (c is ToggleButton toggle) toggle.Click += (_, _) => Changed(page, id);
                else if (c is Selector selector) selector.SelectionChanged += (_, _) => Changed(page, id);
            }
        }
        page.Loaded += OnLoaded;
    }

    static IEnumerable<Control> Controls(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBox { IsReadOnly: false } box) { yield return box; continue; }
            if (child is CheckBox or RadioButton or ComboBox) { yield return (Control)child; continue; }
            if (child is ItemsControl and not TabControl) continue;
            foreach (var c in Controls(child)) yield return c;
        }
    }

    static void Restore(Control control, string? value)
    {
        if (value == null) return;
        switch (control)
        {
            case TextBox box: box.Text = value; break;
            case ToggleButton toggle when bool.TryParse(value, out var on): toggle.IsChecked = on; break;
            case ComboBox combo when int.TryParse(value, out var index) && index >= 0 && index < combo.Items.Count: combo.SelectedIndex = index; break;
        }
    }

    static string? Value(Control control) => control switch
    {
        TextBox box => box.Text.Length <= MaxText ? box.Text : null,
        ToggleButton toggle => (toggle.IsChecked == true).ToString(),
        ComboBox combo => combo.SelectedIndex.ToString(),
        _ => null,
    };

    static readonly Dictionary<string, FrameworkElement> Pending = new();

    static void Changed(FrameworkElement page, string id)
    {
        Pending[id] = page;
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Flush();
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Writes pending changes now; also called on exit.</summary>
    public static void Flush()
    {
        _timer?.Stop();
        if (Pending.Count == 0) return;
        foreach (var pair in Pending) All[pair.Key] = Controls(pair.Value).Select(Value).ToList();
        Pending.Clear();
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(All));
        }
        catch (Exception ex) { Log.Error("Saving page state failed", ex); }
    }
}
