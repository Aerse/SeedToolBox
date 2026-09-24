using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeedToolBox.Core.Services;
using SeedToolBox.DevTools;
using SeedToolBox.Views;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>Toolbox page with thumbnails of the recent screenshots.</summary>
sealed class CaptureHistoryPage : DockPanel
{
    const int ThumbWidth = 220;

    readonly ScreenToolService _service;
    readonly WrapPanel _items = new();
    readonly TextBlock _status = Ui.Status();
    readonly TextBlock _empty;
    bool _dirty = true;
    int _generation;

    public CaptureHistoryPage(ScreenToolService service)
    {
        _service = service;
        var header = Ui.Header("截图历史", "最近复制、保存或贴出的截图，保存在程序目录的 Data\\captures");
        SetDock(header, Dock.Top);
        Children.Add(header);

        var actions = Ui.Row(
            Ui.Button("刷新", Reload),
            Ui.Button("打开文件夹", () =>
            {
                Directory.CreateDirectory(CaptureHistory.Folder);
                Process.Start("explorer.exe", $"\"{CaptureHistory.Folder}\"");
            }),
            Ui.Button("清空", () =>
            {
                if (MessageBox.Show(Window.GetWindow(this), "删除全部截图历史？", "SeedToolBox", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    _service.History.Clear();
            }),
            _status);
        SetDock(actions, Dock.Top);
        Children.Add(actions);

        _empty = new TextBlock { Text = "还没有截图", Foreground = DialogWindow.HintBrush, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        SetDock(_empty, Dock.Top);
        Children.Add(_empty);

        Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _items,
        });

        _service.History.Changed += () =>
        {
            _dirty = true;
            if (IsVisible) Reload();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Reload(); };
    }

    async void Reload()
    {
        _dirty = false;
        int generation = ++_generation;
        var files = _service.History.Files();
        if (_service.Settings.HistoryCount <= 0)
            Ui.SetStatus(_status, "截图历史已在设置中关闭");
        else
            Ui.SetStatus(_status, files.Count > 0 ? $"{files.Count} 张" : "");
        _empty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Thumbnails decode off the UI thread; frozen bitmaps can cross over
        var thumbs = await Task.Run(() => files.Select(f => (Path: f, Thumb: TryLoad(f))).ToList());
        if (generation != _generation) return;
        _items.Children.Clear();
        foreach (var (path, thumb) in thumbs)
            if (thumb != null) _items.Children.Add(Item(path, thumb));
    }

    static BitmapSource? TryLoad(string path)
    {
        try { return CaptureHistory.Load(path, ThumbWidth); }
        catch (Exception ex)
        {
            Log.Error($"Failed to load {path}", ex);
            return null;
        }
    }

    FrameworkElement Item(string path, BitmapSource thumb)
    {
        var image = new Image
        {
            Source = thumb,
            Width = ThumbWidth,
            Height = 130,
            Stretch = Stretch.Uniform,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "双击用默认程序打开",
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Open(path); };

        var name = Path.GetFileNameWithoutExtension(path);
        var time = DateTime.TryParseExact(name.Length >= 15 ? name.Substring(0, 15) : name, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var t)
            ? t.ToString("yyyy-MM-dd HH:mm:ss") : name;

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (text, action) in new (string, Action)[]
        {
            ("复制", () => Use(path, image => { ScreenToolService.CopyImage(image); Ui.SetStatus(_status, "已复制"); })),
            ("打开", () => Open(path)),
            ("贴图", () => Use(path, image =>
            {
                var p = WinForms.Cursor.Position;
                _service.Pin(image, p.X - image.PixelWidth / 2, p.Y - image.PixelHeight / 2);
            })),
            ("编辑", () => Use(path, image => _service.Edit(image))),
            ("删除", () => _service.History.Delete(path)),
        })
        {
            var button = Ui.Button(text, action);
            button.Margin = new Thickness(0, 0, 4, 0);
            button.Padding = new Thickness(8, 2, 8, 2);
            buttons.Children.Add(button);
        }

        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 10, 10),
            Child = new StackPanel
            {
                Children =
                {
                    image,
                    new TextBlock { Text = time, Foreground = DialogWindow.HintBrush, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) },
                    buttons,
                },
            },
        };
    }

    void Use(string path, Action<BitmapSource> action)
    {
        BitmapSource image;
        try { image = CaptureHistory.Load(path); }
        catch (Exception ex)
        {
            Log.Error($"Failed to load {path}", ex);
            Ui.SetStatus(_status, $"无法读取：{ex.Message}", true);
            return;
        }
        action(image);
    }

    void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error($"Failed to open {path}", ex);
            Ui.SetStatus(_status, $"无法打开：{ex.Message}", true);
        }
    }
}
