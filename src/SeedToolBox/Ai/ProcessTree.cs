using System;
using System.Diagnostics;

namespace SeedToolBox.Ai;

/// <summary>Ends a process with everything it started; Process.Kill on .NET Framework only ends the process itself.</summary>
static class ProcessTree
{
    public static void Kill(Process process)
    {
        int pid;
        try
        {
            if (process.HasExited) return;
            pid = process.Id;
        }
        catch (InvalidOperationException) { return; }
        try
        {
            using var taskkill = Process.Start(new ProcessStartInfo("taskkill.exe", $"/T /F /PID {pid}") { UseShellExecute = false, CreateNoWindow = true });
            taskkill?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        // taskkill missing or denied: at least end the process itself
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        try { process.WaitForExit(3000); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
