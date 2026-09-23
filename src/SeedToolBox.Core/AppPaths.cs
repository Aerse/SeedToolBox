using System;
using System.IO;

namespace SeedToolBox.Core;

/// <summary>Well-known folders. Everything lives next to the exe so the app stays portable.</summary>
public static class AppPaths
{
    public static string Base { get; } = AppDomain.CurrentDomain.BaseDirectory;
    public static string Data { get; } = Path.Combine(Base, "Data");
    public static string Logs { get; } = Path.Combine(Data, "Logs");
    public static string Plugins { get; } = Path.Combine(Base, "Plugins");
    public static string Native { get; } = Path.Combine(Base, "Native");
}
