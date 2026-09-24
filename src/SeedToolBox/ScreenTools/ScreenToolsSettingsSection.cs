using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SeedToolBox.DevTools;
using SeedToolBox.Views;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.ScreenTools;

/// <summary>The 截图与屏幕工具 section of the settings page.</summary>
static class ScreenToolsSettingsSection
{
    public static FrameworkElement Create(ScreenToolService service)
    {
        var s = service.Settings;
        var panel = new StackPanel();

        var delay = new ComboBox { Width = 120 };
        foreach (var d in ScreenToolService.Delays) delay.Items.Add($"{d} 秒");
        delay.SelectedIndex = Math.Max(0, Array.IndexOf(ScreenToolService.Delays, s.CaptureDelay));
        delay.SelectionChanged += (_, _) => { s.CaptureDelay = ScreenToolService.Delays[delay.SelectedIndex]; service.SaveSettings(); };
        panel.Children.Add(Spaced(Ui.Row(Ui.Label("延时截图等待"), delay, Hint("  快捷键在上方「快捷键」中设置"))));

        var template = Ui.Field(260);
        template.Text = s.FileNameTemplate;
        var preview = Hint("");
        void UpdatePreview() => preview.Text = "  例：" + service.FileName(template.Text) + ".png";
        template.TextChanged += (_, _) => UpdatePreview();
        template.LostKeyboardFocus += (_, _) =>
        {
            s.FileNameTemplate = template.Text.Trim();
            service.SaveSettings();
        };
        UpdatePreview();
        panel.Children.Add(Spaced(Ui.Row(Ui.Label("文件名模板"), template, preview)));
        panel.Children.Add(Spaced(Hint("{ } 中是时间格式，如 {yyyyMMdd_HHmmss}、{yyyy-MM-dd HH.mm.ss}")));

        var autoSave = new CheckBox { Content = "每次截图后自动保存 PNG 到文件夹", IsChecked = s.AutoSave, Margin = new Thickness(0, 0, 0, 8) };
        autoSave.Click += (_, _) => { s.AutoSave = autoSave.IsChecked == true; service.SaveSettings(); };
        panel.Children.Add(autoSave);
        var folder = Ui.Field(320);
        folder.Text = service.AutoSaveFolder;
        folder.LostKeyboardFocus += (_, _) =>
        {
            s.AutoSaveFolder = folder.Text.Trim();
            service.SaveSettings();
            folder.Text = service.AutoSaveFolder;
        };
        panel.Children.Add(Spaced(Ui.Row(Ui.Label("保存到"), folder, Ui.Button("浏览…", () =>
        {
            using var dialog = new WinForms.FolderBrowserDialog { SelectedPath = service.AutoSaveFolder, ShowNewFolderButton = true };
            if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
            s.AutoSaveFolder = folder.Text = dialog.SelectedPath;
            service.SaveSettings();
        }), Ui.Button("打开", () =>
        {
            Directory.CreateDirectory(service.AutoSaveFolder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{service.AutoSaveFolder}\"");
        }))));

        var history = Ui.Field(70);
        history.Text = s.HistoryCount.ToString();
        history.LostKeyboardFocus += (_, _) =>
        {
            if (int.TryParse(history.Text, out var n)) { s.HistoryCount = Math.Max(0, Math.Min(1000, n)); service.SaveSettings(); }
            history.Text = s.HistoryCount.ToString();
        };
        panel.Children.Add(Spaced(Ui.Row(Ui.Label("截图历史保留"), history, Hint("  张（0 = 不记录），在工具箱「截图历史」中查看"))));
        return panel;
    }

    static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 0, 0, 10);
        return element;
    }

    static TextBlock Hint(string text) => new() { Text = text, Foreground = DialogWindow.HintBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
}
