using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.Host;

/// <summary>Draws the tray menu like the WPF menus: white, rounded highlight, thin separators.</summary>
sealed class FluentMenuRenderer : WinForms.ToolStripProfessionalRenderer
{
    static readonly Color Background = Color.White;
    static readonly Color Border = Color.FromArgb(0xE5, 0xE5, 0xE5);
    static readonly Color Hover = Color.FromArgb(0xF0, 0xF0, 0xF0);
    static readonly Color Text = Color.FromArgb(0x1A, 0x1A, 0x1A);
    static readonly Color Hint = Color.FromArgb(0x8A, 0x8A, 0x8A);
    static readonly Color Disabled = Color.FromArgb(0xA0, 0xA0, 0xA0);

    public FluentMenuRenderer() { RoundedEdges = false; }

    /// <summary>Applies the look to a menu and all its submenus.</summary>
    public static void Apply(WinForms.ToolStripDropDown menu)
    {
        menu.Renderer = new FluentMenuRenderer();
        menu.Font = new Font("Microsoft YaHei UI", 9f);
        if (menu is WinForms.ToolStripDropDownMenu dropDown)
        {
            dropDown.ShowImageMargin = false;
            dropDown.ShowCheckMargin = true;
        }
        menu.Padding = new WinForms.Padding(4);
        menu.Opening += (_, _) =>
        {
            foreach (WinForms.ToolStripItem item in menu.Items)
            {
                if (item is not WinForms.ToolStripMenuItem menuItem) continue;
                menuItem.Padding = new WinForms.Padding(0, 5, 0, 5);
                if (menuItem.HasDropDownItems && menuItem.DropDown.Renderer is not FluentMenuRenderer) Apply(menuItem.DropDown);
            }
        };
        menu.HandleCreated += (_, _) => RoundCorners(menu.Handle);
    }

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e) => e.Graphics.Clear(Background);

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var rect = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rect, 4);
        using var brush = new SolidBrush(Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Border);
        e.Graphics.DrawLine(pen, -4, y, e.Item.Width + 4, y);
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        // Shortcut text is drawn with the same call; keep it grey
        bool shortcut = e.Item is WinForms.ToolStripMenuItem { ShortcutKeyDisplayString: { } s } && e.Text == s;
        e.TextColor = !e.Item.Enabled ? Disabled : shortcut ? Hint : Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
    {
        using var font = new Font("Segoe MDL2 Assets", 9f);
        WinForms.TextRenderer.DrawText(e.Graphics, "\uE73E", font, e.ImageRectangle, Text,
            WinForms.TextFormatFlags.HorizontalCenter | WinForms.TextFormatFlags.VerticalCenter | WinForms.TextFormatFlags.NoPadding);
    }

    protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Hint;
        base.OnRenderArrow(e);
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Windows 11 rounds the popup and gives it a shadow; older versions ignore this
    static void RoundCorners(IntPtr handle)
    {
        int value = 3; // DWMWCP_ROUNDSMALL
        try { DwmSetWindowAttribute(handle, 33, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
    }
}
