// Velotext: helpers for application tests (a WPF thread, fakes for dialogs and pause, a temporary data folder).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    public List<string?> ConfirmDetails { get; } = [];

    public int? ChooseAnswer { get; set; }

    public List<IReadOnlyList<string>> Choices { get; } = [];

    public List<(string Title, string Message)> Errors { get; } = [];

    public bool Confirm(string title, string message, string confirmText, bool destructive, string? details = null)
    {
        Confirmations.Add(title);
        ConfirmDetails.Add(details);
        return ConfirmAnswer;
    }

    public int? Choose(string title, string message, IReadOnlyList<string> options, string confirmText)
    {
        Choices.Add(options);
        return ChooseAnswer;
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

/// <summary>Renders a window's content to a PNG file, on the WPF thread that owns the window.</summary>
internal static class WindowSnapshot
{
    /// <summary>Lets layout and data binding finish.</summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Saves the window's content on its background colour; <paramref name="scale"/> 1.5 gives a sharp image for high-DPI screens.</summary>
    public static void Save(Window window, string path, double scale = 1)
    {
        window.UpdateLayout();
        Pump();
        if (window.Content is not FrameworkElement content)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var width = (int)Math.Ceiling(content.ActualWidth * scale);
        var height = (int)Math.Ceiling(content.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? (Brush)Application.Current.Resources["Velotext.Bg"], null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

/// <summary>A clock that only moves when the test says so.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A temporary data folder with real services wired to it.</summary>
internal sealed class TestEnvironment : IDisposable
{
    /// <param name="seed">Writes files into the data folder before the services load them.</param>
    public TestEnvironment(Action<AppPaths>? seed = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "velotext-app-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Paths = new AppPaths(new AppOptions(StartedByWindows: false, Directory));
        seed?.Invoke(Paths);
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
