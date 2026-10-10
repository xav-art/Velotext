// Velotext: composition root: starts logging, services, the expansion runtime, the tray icon and the main window.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Serilog;
using Velotext.App.Services;
using Velotext.App.ViewModels;
using Velotext.App.Views;
using Velotext.Platform.Windows.Lifecycle;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Velotext.App;

/// <summary>
/// The application. It wires every service by hand (no container needed for a single-window app), so
/// the start-up order is explicit: logging, settings, library, theme, expansion, tray, window.
/// </summary>
public sealed partial class App : Application, IDisposable
{
    private readonly AppOptions _options;
    private readonly AppPaths _paths;
    private readonly SingleInstance _instance;
    private ILoggerFactory? _loggers;
    private ILogger? _logger;
    private SettingsService? _settings;
    private LibraryService? _library;
    private UsageService? _usage;
    private ThemeService? _theme;
    private ExpansionRuntime? _runtime;
    private TrayService? _tray;
    private DialogService? _dialogs;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private AutostartService? _autostart;
    private bool _exiting;

    /// <summary>Creates the application.</summary>
    /// <param name="options">Command-line options.</param>
    /// <param name="paths">File locations.</param>
    /// <param name="instance">The single-instance guard (to listen for activation requests).</param>
    public App(AppOptions options, AppPaths paths, SingleInstance instance)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    private static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Velotext.exe");

    /// <summary>True when Velotext runs from its installed location (not from a build folder).</summary>
    private static bool IsInstalled => ExecutablePath.Contains(@"\Velotext\current\", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartLogging();
        RegisterErrorHandlers();
        var time = TimeProvider.System;

        var loggers = _loggers!;
        _settings = new SettingsService(_paths, loggers.CreateLogger<SettingsService>());
        _settings.Load();
        _theme = new ThemeService(this);
        _theme.Apply(_settings.Current.Theme);
        _dialogs = new DialogService();

        _library = new LibraryService(_paths, time, loggers.CreateLogger<LibraryService>());
        try
        {
            _library.Load();
        }
        catch (LibraryUnavailableException ex)
        {
            // Starting anyway would show an empty library and could save it over the real one.
            LogLibraryUnavailable(_logger!, ex);
            _dialogs.ShowError(
                "Velotext cannot open your phrases",
                "Another app is using your library file. Wait a moment, then start Velotext again. If this keeps happening, restart Windows.");
            Shutdown(1);
            return;
        }

        _usage = new UsageService(_paths, time, loggers.CreateLogger<UsageService>());
        _usage.Load();

        _tray = new TrayService(new TrayActions(ShowMainWindow, TogglePause, ShowSettings, ExitApplication));
        _runtime = new ExpansionRuntime(_library, _settings, _usage, _tray, loggers, time);
        _runtime.PausedChanged += (_, _) => Dispatcher.BeginInvoke(() => _tray?.SetPaused(_runtime?.IsPaused ?? false));
        _runtime.Start();

        _autostart = new AutostartService();
        if (IsInstalled)
        {
            _autostart.Reconcile(ExecutablePath, _settings.Current.StartWithWindows);
        }

        _mainViewModel = new MainViewModel(_library, _settings, _usage, _runtime, _dialogs, new WpfUiDispatcher(Dispatcher), time);
        _mainViewModel.SettingsRequested += (_, _) => ShowSettings();
        _mainWindow = new MainWindow(_mainViewModel);
        _mainWindow.CloseRequested += OnMainWindowClosing;
        MainWindow = _mainWindow;

        // Tell the user once per failure streak when the window is hidden; the window shows its own banner.
        var saveWarningShown = false;
        _library.SaveStateChanged += (_, state) => Dispatcher.BeginInvoke(() =>
        {
            if (state == SaveState.Saved)
            {
                saveWarningShown = false;
            }
            else if (state == SaveState.Failed && !saveWarningShown && _mainWindow is { IsVisible: false })
            {
                saveWarningShown = true;
                _tray?.Show("Phrases not saved", "Velotext cannot save your phrases. Open Velotext for details.", NotificationKind.Warning);
            }
        });
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(ShowMainWindow));
        SessionEnding += (_, _) => SaveEverything();

        if (!_options.StartedByWindows)
        {
            ShowMainWindow();
        }

        LogStarted(_logger!, typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?", _options.StartedByWindows, IsInstalled);
    }

    /// <summary>Saves everything and releases hooks, hotkeys, the tray icon and log files.</summary>
    public void Dispose()
    {
        SaveEverything();
        _mainViewModel?.Dispose();
        _runtime?.Dispose();
        _library?.Dispose();
        _usage?.Dispose();
        _tray?.Dispose();
        _theme?.Dispose();
        _mainViewModel = null;
        _runtime = null;
        _library = null;
        _usage = null;
        _tray = null;
        _theme = null;
        if (_loggers is not null)
        {
            LogStopped(_logger!);
            _loggers.Dispose();
            _loggers = null;
            Log.CloseAndFlush();
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    private void StartLogging()
    {
        Directory.CreateDirectory(_paths.LogDirectory);
        var configuration = new LoggerConfiguration();
        configuration = _options.Verbose ? configuration.MinimumLevel.Debug() : configuration.MinimumLevel.Information();
        Log.Logger = configuration
            .WriteTo.File(
                Path.Combine(_paths.LogDirectory, "velotext-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        _loggers = LoggerFactory.Create(builder => builder.AddSerilog(dispose: false));
        _logger = _loggers.CreateLogger<App>();
    }

    private void RegisterErrorHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogFatal(_logger!, args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogUnobserved(_logger!, args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app (and the user's expansions) alive; log the details and tell the user once.
        LogUiError(_logger!, e.Exception);
        e.Handled = true;
        _dialogs?.ShowError(
            "Velotext had an internal error",
            "Velotext still runs and your phrases are safe. If this happens again, open Settings and click Open log folder. Send the newest log file to your IT contact.");
    }

    private void ShowMainWindow() => _mainWindow?.ShowAndActivate();

    private void TogglePause() => _runtime?.SetPaused(!_runtime.IsPaused);

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(
            _settings!,
            _autostart!,
            _library!,
            _dialogs!,
            new SettingsActions(
                ExecutablePath,
                _paths.DataDirectory,
                _paths.LogDirectory,
                OpenFolder,
                theme => _theme!.Apply(theme),
                library =>
                {
                    _library!.BackupNow();
                    _mainViewModel!.ReplaceLibrary(library);
                },
                gesture => _runtime!.DescribePauseHotkeyProblem(gesture)));
        EventHandler<bool> conflictChanged = (_, _) => Dispatcher.BeginInvoke(viewModel.RefreshPauseHotkeyProblem);
        _runtime!.PauseHotkeyConflictChanged += conflictChanged;
        _settingsWindow = new SettingsWindow(viewModel) { Owner = _mainWindow is { IsVisible: true } ? _mainWindow : null };
        _settingsWindow.Closed += (_, _) =>
        {
            _runtime.PauseHotkeyConflictChanged -= conflictChanged;
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        _mainViewModel?.CommitNow();
        _mainWindow?.Hide();
        if (_settings is { Current.TrayHintShown: false })
        {
            _tray?.Show("Velotext is still running", "Velotext still runs in the notification area. To open it, click the Velotext icon. If you cannot see the icon, click Show hidden icons on the taskbar.", NotificationKind.Info);
            _settings.Update(s => s with { TrayHintShown = true });
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        _settingsWindow?.Close();
        _mainWindow?.Close();
        Shutdown();
    }

    private void SaveEverything()
    {
        _mainViewModel?.CommitNow();
        _library?.Flush();
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _dialogs?.ShowError("The folder did not open", folder);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Velotext {Version} started (by Windows: {Autostart}, installed: {Installed}).")]
    private static partial void LogStarted(ILogger logger, string version, bool autostart, bool installed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Velotext stopped.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The library file stayed locked by another program; Velotext did not start.")]
    private static partial void LogLibraryUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled error.")]
    private static partial void LogFatal(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unobserved task error.")]
    private static partial void LogUnobserved(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled UI error.")]
    private static partial void LogUiError(ILogger logger, Exception exception);
}
