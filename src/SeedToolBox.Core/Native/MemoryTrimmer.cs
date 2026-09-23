using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SeedToolBox.Core.Native;

public static class MemoryTrimmer
{
    /// <summary>
    /// Collects garbage and pages the working set out. Call when the app goes idle
    /// (e.g. hidden to tray) so it doesn't sit on memory in the background.
    /// </summary>
    public static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(process.Handle, (IntPtr)(-1), (IntPtr)(-1));
    }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumSize, IntPtr maximumSize);
}
