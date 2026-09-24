using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SeedToolBox.DevTools;

/// <summary>Column sorting and CSV export for the result lists on the tool pages.</summary>
static class ListTools
{
    /// <summary>
    /// Sorts by a column when its header is clicked; clicking again reverses the order.
    /// A column bound to "XxxText" sorts by "Xxx" when the item has it, so sizes sort as numbers.
    /// <paramref name="templated"/> maps headers of template columns to the property they show.
    /// </summary>
    public static void Sortable(ListView list, params (string Header, string Path)[] templated)
    {
        string? current = null;
        var direction = ListSortDirection.Ascending;
        list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;
            var path = SortPath(list, column, templated);
            if (path == null) return;
            direction = path == current && direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            current = path;
            var view = CollectionViewSource.GetDefaultView(list.ItemsSource ?? list.Items);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(path, direction));
        }));
    }

    static string? SortPath(ListView list, GridViewColumn column, (string Header, string Path)[] templated)
    {
        var header = column.Header as string ?? "";
        var mapped = templated.FirstOrDefault(t => t.Header == header).Path;
        if (mapped != null) return mapped;
        if ((column.DisplayMemberBinding as Binding)?.Path?.Path is not { Length: > 0 } path) return null;
        var item = Items(list).FirstOrDefault();
        if (path.EndsWith("Text") && item?.GetType().GetProperty(path.Substring(0, path.Length - 4)) != null)
            return path.Substring(0, path.Length - 4);
        return path;
    }

    static IEnumerable<object> Items(ListView list) => ((IEnumerable?)list.ItemsSource ?? list.Items).Cast<object>();

    /// <summary>A button that saves the columns with plain bindings as a UTF-8 CSV that Excel opens.</summary>
    public static Button ExportButton(ListView list, TextBlock status, string fileName, params (string Header, string Path)[] templated) => Ui.Button("导出 CSV", () =>
    {
        var columns = ((GridView)list.View).Columns
            .Select(c => (Header: c.Header as string ?? "", Path: templated.FirstOrDefault(t => t.Header == (c.Header as string)).Path ?? (c.DisplayMemberBinding as Binding)?.Path?.Path))
            .Where(c => c.Header.Length > 0 && c.Path != null)
            .ToList();
        var items = CollectionViewSource.GetDefaultView(list.ItemsSource ?? list.Items).Cast<object>().ToList();
        if (items.Count == 0) { Ui.SetStatus(status, "列表是空的", true); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = "CSV 文件|*.csv" };
        if (dialog.ShowDialog(Window.GetWindow(list)) != true) return;
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", columns.Select(c => Quote(c.Header))));
        foreach (var item in items)
            csv.AppendLine(string.Join(",", columns.Select(c => Quote(Convert.ToString(item.GetType().GetProperty(c.Path!)?.GetValue(item)) ?? ""))));
        try
        {
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            Ui.SetStatus(status, $"已导出 {items.Count} 行到 {dialog.FileName}");
        }
        catch (Exception ex) { Ui.SetStatus(status, "导出失败：" + ex.Message, true); }
    });

    static string Quote(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
