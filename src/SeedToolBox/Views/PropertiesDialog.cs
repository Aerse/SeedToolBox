using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SeedToolBox.Launcher;
using WinForms = System.Windows.Forms;

namespace SeedToolBox.Views;

public static class PropertiesDialog
{
    /// <summary>Edits an item in place. Returns true if the user saved changes.</summary>
    public static bool Show(Window owner, LaunchItem item)
    {
        var name = Field(item.Name);
        var path = Field(item.Path);
        var args = Field(item.Arguments);
        var workDir = Field(item.WorkingDirectory);
        var icon = Field(item.IconPath);
        var remarks = Field(item.Remarks);
        remarks.AcceptsReturn = true;
        remarks.TextWrapping = TextWrapping.Wrap;
        remarks.Height = 60;
        remarks.VerticalContentAlignment = VerticalAlignment.Top;
        remarks.Padding = new Thickness(8, 5, 8, 5);
        remarks.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var admin = new CheckBox { Content = "以管理员身份运行", IsChecked = item.RunAsAdmin, Margin = new Thickness(0, 6, 0, 4) };

        var grid = new Grid { Margin = new Thickness(4, 4, 4, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Window? window = null;
        AddRow(grid, "名称", name);
        AddRow(grid, "目标", path, Browse("浏览...", () => PickFile(window!, path, "所有文件|*.*")));
        AddRow(grid, "参数", args);
        AddRow(grid, "起始位置", workDir, Browse("浏览...", () => PickFolder(workDir)));
        AddRow(grid, "图标", icon, Browse("浏览...", () => PickFile(window!, icon, "图标|*.ico;*.png;*.exe;*.dll|所有文件|*.*")));
        AddRow(grid, "备注", remarks);
        AddRow(grid, "", admin);

        var ok = DialogWindow.OkButton();
        var cancel = DialogWindow.CancelButton();
        var hint = new TextBlock
        {
            Text = "起始位置留空则为目标所在文件夹；图标留空则用目标自身的图标。支持 %环境变量%。",
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["HintTextBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
            Margin = new Thickness(4, 0, 4, 8),
        };

        window = DialogWindow.Create("属性", new StackPanel { Children = { grid, hint } }, ok, cancel);
        window.Owner = owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(path.Text))
            {
                MessageBox.Show(window, "名称和目标不能为空", "SeedToolBox");
                return;
            }
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };

        if (window.ShowDialog() != true) return false;

        item.Name = name.Text.Trim();
        item.Path = path.Text.Trim();
        item.Arguments = args.Text.Trim();
        item.WorkingDirectory = workDir.Text.Trim();
        item.IconPath = icon.Text.Trim();
        item.Remarks = remarks.Text.Trim();
        item.RunAsAdmin = admin.IsChecked == true;
        return true;
    }

    static TextBox Field(string text) => new() { Text = text, Style = DialogWindow.TextBoxStyle, Margin = new Thickness(0, 4, 0, 4) };

    static Button Browse(string text, Action onClick)
    {
        var button = new Button { Content = text, Margin = new Thickness(8, 4, 0, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    static void AddRow(Grid grid, string label, FrameworkElement field, FrameworkElement? extra = null)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(text);
        grid.Children.Add(field);
        if (extra != null)
        {
            Grid.SetRow(extra, row);
            Grid.SetColumn(extra, 2);
            grid.Children.Add(extra);
        }
    }

    static void PickFile(Window owner, TextBox target, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, DereferenceLinks = false };
        var current = Environment.ExpandEnvironmentVariables(target.Text);
        if (File.Exists(current)) dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current));
        if (dialog.ShowDialog(owner) == true) target.Text = dialog.FileName;
    }

    static void PickFolder(TextBox target)
    {
        using var dialog = new WinForms.FolderBrowserDialog { SelectedPath = Environment.ExpandEnvironmentVariables(target.Text) };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK) target.Text = dialog.SelectedPath;
    }
}
