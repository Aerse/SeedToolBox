using System;
using System.Windows.Media.Imaging;

namespace SeedToolBox.ScreenTools;

public sealed partial class ScreenToolService
{
    ScrollCaptureSession? _scroll;

    /// <summary>Opens the image in an editor window; with <paramref name="done"/> the result is handed back.</summary>
    public void Edit(BitmapSource image, Action<BitmapSource>? done = null)
    {
        var window = new ImageEditorWindow(image, this, done);
        window.Show();
        window.Activate();
    }

    internal void StartScrollCapture(System.Drawing.Rectangle region, double scale)
    {
        if (_scroll != null || region.Width < 16 || region.Height < 16) return;
        _scroll = new ScrollCaptureSession(region, scale);
        _scroll.Finished += image =>
        {
            _scroll = null;
            if (image != null) Edit(image);
        };
        _scroll.Start();
    }
}
