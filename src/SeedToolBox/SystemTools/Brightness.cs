using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using SeedToolBox.Core.Services;

namespace SeedToolBox.SystemTools;

/// <summary>A screen whose brightness can be set: a laptop panel through WMI, or an external monitor through DDC/CI.</summary>
sealed class Display
{
    public string Name { get; init; } = "";
    public int Brightness { get; set; }
    internal string? WmiInstance { get; init; }
    internal IntPtr Physical { get; init; }
    internal uint Min { get; init; }
    internal uint Max { get; init; } = 100;
}

/// <summary>Monitor brightness. DDC/CI calls can take a second or more, so call these off the UI thread.</summary>
static class Brightness
{
    static readonly object Lock = new();
    static List<Display>? _displays;

    public static List<Display> Displays(bool refresh = false)
    {
        lock (Lock)
        {
            if (_displays != null && !refresh) return _displays;
            if (_displays != null) foreach (var d in _displays.Where(d => d.Physical != IntPtr.Zero)) DestroyPhysicalMonitor(d.Physical);
            _displays = Wmi().Concat(Ddc()).ToList();
            return _displays;
        }
    }

    static IEnumerable<Display> Wmi()
    {
        var list = new List<Display>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    list.Add(new Display { Name = "内置屏幕", WmiInstance = (string)o["InstanceName"], Brightness = Convert.ToInt32(o["CurrentBrightness"]) });
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { } // desktops have no such class
        return list;
    }

    static IEnumerable<Display> Ddc()
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) => { handles.Add(monitor); return true; }, IntPtr.Zero);
        var list = new List<Display>();
        int index = 0;
        foreach (var monitor in handles)
        {
            index++;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) continue;
            var physical = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) continue;
            foreach (var p in physical)
            {
                if (GetMonitorBrightness(p.hPhysicalMonitor, out var min, out var current, out var max) && max > min)
                {
                    var name = string.IsNullOrWhiteSpace(p.szPhysicalMonitorDescription) || p.szPhysicalMonitorDescription.Contains("Generic") ? $"显示器 {index}" : $"显示器 {index}（{p.szPhysicalMonitorDescription}）";
                    list.Add(new Display { Name = name, Physical = p.hPhysicalMonitor, Min = min, Max = max, Brightness = (int)Math.Round(100.0 * (current - min) / (max - min)) });
                }
                else DestroyPhysicalMonitor(p.hPhysicalMonitor); // no DDC/CI support
            }
        }
        return list;
    }

    /// <summary>Sets 0–100.</summary>
    public static void Set(Display display, int percent)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        lock (Lock)
        {
            if (display.WmiInstance != null)
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", $"SELECT * FROM WmiMonitorBrightnessMethods WHERE InstanceName='{display.WmiInstance.Replace(@"\", @"\\").Replace("'", @"\'")}'");
                foreach (ManagementObject o in searcher.Get())
                    using (o) o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
            }
            else SetMonitorBrightness(display.Physical, (uint)(display.Min + (display.Max - display.Min) * percent / 100.0));
            display.Brightness = percent;
        }
    }

    /// <summary>Moves every screen by <paramref name="delta"/> points; returns the new level of the first one, or null when none can be set.</summary>
    public static int? Change(int delta)
    {
        var displays = Displays();
        foreach (var d in displays)
        {
            try { Set(d, d.Brightness + delta); }
            catch (Exception ex) when (ex is ManagementException or COMException) { Log.Error("Failed to set the brightness", ex); }
        }
        return displays.Count == 0 ? null : displays[0].Brightness;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr monitor);
}
