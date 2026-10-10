// Velotext: where Velotext keeps its files, and the command-line options that change that.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.App.Services;

/// <summary>Options parsed from the command line.</summary>
/// <param name="StartedByWindows">True when Windows started Velotext at sign-in (<c>--autostart</c>): start in the tray.</param>
/// <param name="DataDirectory">Optional data folder (<c>--data-dir</c>), used for isolated test runs.</param>
/// <param name="Verbose">True with <c>--verbose</c>: the log also records operation and clipboard timing (never typed text or phrase content), for troubleshooting.</param>
public sealed record AppOptions(bool StartedByWindows, string? DataDirectory, bool Verbose = false)
{
    /// <summary>Parses the command line. Unknown arguments are ignored.</summary>
    /// <param name="args">Command-line arguments.</param>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var autostart = false;
        var verbose = false;
        string? dataDirectory = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "--AUTOSTART":
                    autostart = true;
                    break;
                case "--DATA-DIR" when i + 1 < args.Count:
                    dataDirectory = args[++i];
                    break;
                case "--VERBOSE":
                    verbose = true;
                    break;
            }
        }

        return new AppOptions(autostart, dataDirectory, verbose);
    }
}

/// <summary>
/// File locations. Phrases and settings live in the roaming profile (%APPDATA%\Velotext) so they follow the
/// user; logs stay on the machine (%LOCALAPPDATA%\Velotext\logs). A data folder from the command line or
/// the VELOTEXT_DATA_DIR variable replaces both, which keeps test runs away from the user's real data.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Resolves the paths.</summary>
    /// <param name="options">Command-line options.</param>
    public AppPaths(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var custom = options.DataDirectory ?? Environment.GetEnvironmentVariable("VELOTEXT_DATA_DIR");
        IsCustom = !string.IsNullOrWhiteSpace(custom);
        DataDirectory = IsCustom
            ? Path.GetFullPath(custom!)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Velotext");
        LogDirectory = IsCustom
            ? Path.Combine(DataDirectory, "logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velotext", "logs");
    }

    /// <summary>True when a custom data folder is in use.</summary>
    public bool IsCustom { get; }

    /// <summary>Folder for the library, settings and usage files.</summary>
    public string DataDirectory { get; }

    /// <summary>Folder for log files.</summary>
    public string LogDirectory { get; }

    /// <summary>Rolling backups of the library.</summary>
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

    /// <summary>The phrase library file.</summary>
    public string LibraryFile => Path.Combine(DataDirectory, "library.json");

    /// <summary>The settings file.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>The usage file (last used phrases).</summary>
    public string UsageFile => Path.Combine(DataDirectory, "usage.json");
}
