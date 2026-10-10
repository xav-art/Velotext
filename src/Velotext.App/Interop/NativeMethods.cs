// Velotext: the few Win32 functions the application layer needs directly.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Velotext.App.Interop;

/// <summary>Win32 functions used by the application layer.</summary>
internal static partial class NativeMethods
{
    /// <summary>System metric: width of a small icon in pixels (tray icon size at the current DPI).</summary>
    public const int SmCxSmIcon = 49;

    /// <summary>Reads a system metric.</summary>
    /// <param name="index">Metric index.</param>
    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);
}
