// Velotext: per-user "start with Windows" through the HKCU Run key (no admin rights needed).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Microsoft.Win32;

namespace Velotext.Platform.Windows.Lifecycle;

/// <summary>Whether Velotext starts with Windows.</summary>
public enum AutostartState
{
    /// <summary>Not registered.</summary>
    Off = 0,

    /// <summary>Registered and allowed.</summary>
    On = 1,

    /// <summary>Registered, but the user turned it off in Task Manager's Startup apps.</summary>
    DisabledInTaskManager = 2,
}

/// <summary>
/// Manages the per-user Run entry. Task Manager keeps its own on/off flag per entry under
/// "StartupApproved"; turning autostart on from Velotext clears a Task Manager "disabled" flag,
/// because that is an explicit user choice made in Velotext.
/// </summary>
public sealed class AutostartService
{
    /// <summary>The standard per-user Run key.</summary>
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The per-user key where Task Manager stores startup on/off flags.</summary>
    public const string DefaultApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string _runKey;
    private readonly string _approvedKey;
    private readonly string _valueName;

    /// <summary>Creates the service. Key paths are relative to HKEY_CURRENT_USER; tests pass private paths.</summary>
    /// <param name="valueName">Name of the Run value.</param>
    /// <param name="runKey">Run key path.</param>
    /// <param name="approvedKey">StartupApproved key path.</param>
    public AutostartService(string valueName = "Velotext", string runKey = DefaultRunKey, string approvedKey = DefaultApprovedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        _valueName = valueName;
        _runKey = runKey;
        _approvedKey = approvedKey;
    }

    /// <summary>Builds the command line stored in the Run value.</summary>
    /// <param name="executablePath">Full path of the Velotext executable.</param>
    public static string CommandFor(string executablePath) => $"\"{executablePath}\" --autostart";

    /// <summary>Reads the current state for <paramref name="executablePath"/>.</summary>
    /// <param name="executablePath">Full path of the Velotext executable.</param>
    public AutostartState GetState(string executablePath)
    {
        using var run = Registry.CurrentUser.OpenSubKey(_runKey);
        if (run?.GetValue(_valueName) is not string command
            || !string.Equals(command, CommandFor(executablePath), StringComparison.OrdinalIgnoreCase))
        {
            return AutostartState.Off;
        }

        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey);
        return approved?.GetValue(_valueName) is byte[] { Length: > 0 } flags && (flags[0] & 0x1) != 0
            ? AutostartState.DisabledInTaskManager
            : AutostartState.On;
    }

    /// <summary>Turns autostart on or off for <paramref name="executablePath"/>.</summary>
    /// <param name="executablePath">Full path of the Velotext executable.</param>
    /// <param name="enabled">True to start with Windows.</param>
    public void Set(string executablePath, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        using var run = Registry.CurrentUser.CreateSubKey(_runKey, writable: true);
        if (enabled)
        {
            run.SetValue(_valueName, CommandFor(executablePath), RegistryValueKind.String);
            using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true);
            if (approved?.GetValue(_valueName) is byte[] { Length: > 0 } flags && (flags[0] & 0x1) != 0)
            {
                approved.SetValue(_valueName, new byte[12] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }
        }
        else
        {
            run.DeleteValue(_valueName, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true);
            approved?.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Keeps the Run entry pointing at the running executable when autostart is wanted, for example
    /// after an update moved the files. Does nothing when the user disabled it in Task Manager.
    /// </summary>
    /// <param name="executablePath">Full path of the running executable.</param>
    /// <param name="wanted">The user's "start with Windows" setting.</param>
    public void Reconcile(string executablePath, bool wanted)
    {
        var state = GetState(executablePath);
        if (wanted && state == AutostartState.Off)
        {
            using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey);
            var userDisabled = approved?.GetValue(_valueName) is byte[] { Length: > 0 } flags && (flags[0] & 0x1) != 0;
            if (!userDisabled)
            {
                Set(executablePath, enabled: true);
            }
        }
        else if (!wanted && state != AutostartState.Off)
        {
            Set(executablePath, enabled: false);
        }
    }
}
