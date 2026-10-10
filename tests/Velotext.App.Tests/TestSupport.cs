// Velotext: helpers for application tests (a WPF thread, fakes for dialogs and pause, a temporary data folder).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Velotext.App.Services;
using Velotext.App.ViewModels;

namespace Velotext.App.Tests;

/// <summary>Runs code on one shared STA thread that owns the WPF <see cref="Application"/>.</summary>
internal static class WpfThread
{
    private static readonly Lazy<Dispatcher> s_dispatcher = new(Start);

    public static void Run(Action action) => s_dispatcher.Value.Invoke(action);

    public static T Run<T>(Func<T> func) => s_dispatcher.Value.Invoke(func);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "WPF test thread" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}

/// <summary>Records dialog calls and answers with preset values.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;

    public string? OpenPath { get; set; }

    public string? SavePath { get; set; }

    public List<string> Confirmations { get; } = [];

    public List<(string Title, string Message)> Errors { get; } = [];

    public bool Confirm(string title, string message, string confirmText, bool destructive)
    {
        Confirmations.Add(title);
        return ConfirmAnswer;
    }

    public void ShowError(string title, string message) => Errors.Add((title, message));

    public string? PickFileToOpen(string filter, string? initialDirectory = null) => OpenPath;

    public string? PickFileToSave(string filter, string defaultName) => SavePath;
}

/// <summary>Runs posted work at once.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>A pause switch without an expansion engine.</summary>
internal sealed class FakePause : IPauseControl
{
    public event EventHandler<bool>? PausedChanged;

    public bool IsPaused { get; private set; }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        PausedChanged?.Invoke(this, paused);
    }
}

/// <summary>A temporary data folder with real services wired to it.</summary>
internal sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Directory = Path.Combine(Path.GetTempPath(), "velotext-app-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Paths = new AppPaths(new AppOptions(StartedByWindows: false, Directory));
        Settings = new SettingsService(Paths, NullLogger<SettingsService>.Instance);
        Settings.Load();
        Library = new LibraryService(Paths, TimeProvider.System, NullLogger<LibraryService>.Instance);
        Library.Load();
        Usage = new UsageService(Paths, TimeProvider.System, NullLogger<UsageService>.Instance);
        Usage.Load();
    }

    public string Directory { get; }

    public AppPaths Paths { get; }

    public SettingsService Settings { get; }

    public LibraryService Library { get; }

    public UsageService Usage { get; }

    public FakeDialogs Dialogs { get; } = new();

    public FakePause Pause { get; } = new();

    public MainViewModel CreateMainViewModel() =>
        new(Library, Settings, Usage, Pause, Dialogs, new ImmediateDispatcher(), TimeProvider.System);

    public void Dispose()
    {
        Library.Dispose();
        Usage.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A log or backup file can still be closing; the OS cleans temp folders.
        }
    }
}
