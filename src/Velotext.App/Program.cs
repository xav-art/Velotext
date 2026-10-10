// Velotext: process entry point (installer hooks, single instance, then the WPF application).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velopack;
using Velotext.App.Services;
using Velotext.Platform.Windows.Lifecycle;

namespace Velotext.App;

/// <summary>Entry point.</summary>
public static class Program
{
    /// <summary>
    /// Runs the installer hooks first (Velopack calls the app with special arguments during install
    /// and uninstall), then makes sure only one Velotext runs per user and data folder.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => RemoveAutostart())
            .Run();

        var options = AppOptions.Parse(args);
        var paths = new AppPaths(options);
        using var instance = new SingleInstance(paths.DataDirectory);
        if (!instance.IsFirstInstance)
        {
            instance.SignalFirstInstance();
            return 0;
        }

        var app = new App(options, paths, instance);
        app.InitializeComponent();
        return app.Run();
    }

    private static void RemoveAutostart()
    {
        try
        {
            new AutostartService().Set(Environment.ProcessPath ?? "Velotext.exe", enabled: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Uninstall must never fail because of the Run entry.
        }
    }
}
