using System.IO;
using System.Runtime.InteropServices;

namespace SeedToolBox.Core.Native;

/// <summary>
/// Native (C++/Rust) DLLs go in the Native folder next to the exe.
/// After <see cref="Init"/>, a plain <c>[DllImport("xxx.dll")]</c> resolves them from there.
/// </summary>
public static class NativeLibraries
{
    public static void Init()
    {
        if (Directory.Exists(AppPaths.Native))
            SetDllDirectory(AppPaths.Native);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);
}
