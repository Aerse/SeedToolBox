using System.IO;
using System.Runtime.InteropServices;

namespace SeedToolBox.Core.Native;

/// <summary>
/// Native (C++/Rust) DLLs go in Native\x64 and Native\x86 next to the exe.
/// After <see cref="Init"/>, a plain <c>[DllImport("xxx.dll")]</c> resolves them from there.
/// </summary>
public static class NativeLibraries
{
    public static void Init()
    {
        // One subfolder per architecture, matching the running process
        var folder = Path.Combine(AppPaths.Native, System.Environment.Is64BitProcess ? "x64" : "x86");
        if (Directory.Exists(folder))
            SetDllDirectory(folder);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);
}
