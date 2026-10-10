// Velotext: foreground window and process helpers (who has focus, which process family a window belongs to).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Platform.Windows.Native;

namespace Velotext.Platform.Windows.Interop;

/// <summary>The window that has keyboard focus and the process that owns it.</summary>
/// <param name="Window">Window handle, or zero when no window has focus.</param>
/// <param name="ProcessId">Owning process id, or zero.</param>
/// <param name="ThreadId">Owning thread id, or zero.</param>
public readonly record struct ForegroundTarget(nint Window, uint ProcessId, uint ThreadId)
{
    /// <summary>
    /// Reads the current foreground window. Store apps run inside a frame window that belongs to a host
    /// process; for them the process is the app's own (the owner of its core window), which is the one
    /// that reads the clipboard.
    /// </summary>
    public static unsafe ForegroundTarget Current()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0)
        {
            return default;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        var buffer = stackalloc char[64];
        var length = NativeMethods.GetClassNameW(window, buffer, 64);
        if (new ReadOnlySpan<char>(buffer, Math.Max(0, length)).SequenceEqual("ApplicationFrameWindow"))
        {
            var core = NativeMethods.FindWindowExW(window, 0, "Windows.UI.Core.CoreWindow", null);
            if (core != 0)
            {
                threadId = NativeMethods.GetWindowThreadProcessId(core, out processId);
            }
        }

        return new ForegroundTarget(window, processId, threadId);
    }

    /// <summary>True when the foreground window belongs to this Velotext process.</summary>
    public bool IsOwnProcess => ProcessId == (uint)Environment.ProcessId;
}

/// <summary>Facts about another process that Velotext may need without touching its memory.</summary>
public static class ProcessInfo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    /// <summary>The executable name of a process, for diagnostic logs only (no content), or its id when unknown.</summary>
    /// <param name="processId">The process id.</param>
    public static string NameOf(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The process that owns a window, by name, for diagnostic logs only.</summary>
    /// <param name="window">The window handle.</param>
    public static string NameOfWindowOwner(nint window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return window == 0 ? "none" : NameOf(processId);
    }

    /// <summary>
    /// True when the process runs as administrator, or when Velotext may not even query it (protected or
    /// higher-integrity processes). Low-level hooks do not see input sent to such processes.
    /// </summary>
    /// <param name="processId">The process id.</param>
    public static unsafe bool IsElevatedOrHidden(uint processId)
    {
        if (processId == 0)
        {
            return true;
        }

        var process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return true;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, TokenQuery, out var token))
            {
                return true;
            }

            try
            {
                int elevated;
                return !NativeMethods.GetTokenInformation(token, TokenElevation, &elevated, sizeof(int), out _) || elevated != 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}

/// <summary>Process tree queries.</summary>
public static class ProcessTree
{
    /// <summary>
    /// Returns true when <paramref name="processId"/> is <paramref name="ancestorId"/> or one of its
    /// descendants (up to <paramref name="maxDepth"/> levels). Browser-based apps read the clipboard from
    /// a child process (for example the WebView2 host of Teams), so the family counts as the target.
    /// </summary>
    /// <param name="processId">The process to check.</param>
    /// <param name="ancestorId">The expected ancestor.</param>
    /// <param name="maxDepth">How many parent links to follow.</param>
    public static bool IsSameOrDescendant(uint processId, uint ancestorId, int maxDepth = 4)
    {
        if (processId == 0 || ancestorId == 0)
        {
            return false;
        }

        if (processId == ancestorId)
        {
            return true;
        }

        var parents = SnapshotParents();
        var current = processId;
        for (var depth = 0; depth < maxDepth && parents.TryGetValue(current, out var parent) && parent != 0 && parent != current; depth++)
        {
            if (parent == ancestorId)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private static Dictionary<uint, uint> SnapshotParents()
    {
        var parents = new Dictionary<uint, uint>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == -1 || snapshot == 0)
        {
            return parents;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry32>() };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return parents;
            }

            do
            {
                parents[entry.ProcessId] = entry.ParentProcessId;
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return parents;
    }
}
