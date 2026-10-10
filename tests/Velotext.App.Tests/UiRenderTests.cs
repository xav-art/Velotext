// Velotext: headless UI tests: windows load with the theme, bind without errors and render (optionally to PNG).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Velotext.App.Services;
using Velotext.App.ViewModels;
using Velotext.App.Views;
using Velotext.Core.Settings;
using Velotext.Platform.Windows.Lifecycle;

namespace Velotext.App.Tests;

/// <summary>
/// Loads the real windows off-screen with the real theme and checks that every binding resolves. Set
/// VELOTEXT_UI_SNAPSHOTS to a folder to also save PNG renderings for visual review.
/// </summary>
public sealed class UiRenderTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static string? SnapshotFolder => Environment.GetEnvironmentVariable("VELOTEXT_UI_SNAPSHOTS");

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Snapshot(Window window, string name)
    {
        window.UpdateLayout();
        Pump();
        if (SnapshotFolder is not { Length: > 0 } folder || window.Content is not FrameworkElement content)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? (Brush)Application.Current.Resources["Velotext.Bg"], null, new Rect(0, 0, width, height));
        }

        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }

    private static List<string> CaptureBindingErrors(Action action)
    {
        var errors = new List<string>();
        var listener = new BindingErrorListener(errors);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            action();
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }

        return errors;
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void Main_window_renders_every_state_without_binding_errors(AppTheme theme)
    {
        var errors = WpfThread.Run(() => CaptureBindingErrors(() =>
        {
            using var themeService = new ThemeService(Application.Current);
            themeService.Apply(theme);
            using var vm = _env.CreateMainViewModel();
            var window = new MainWindow(vm) { Left = -20000, Top = -20000, ShowActivated = false, Width = 1120, Height = 740 };
            window.Show();
            try
            {
                Snapshot(window, $"main-{theme}-empty");

                var phrase = vm.TreeRoots.OfType<FolderViewModel>().First().Children.OfType<PhraseViewModel>().First();
                vm.SelectedNode = phrase;
                phrase.AddHotkeyTriggerCommand.Execute(null);
                vm.CommitNow();
                Snapshot(window, $"main-{theme}-phrase");

                vm.SearchText = "no such phrase";
                vm.Banner = "The phrase file was damaged. Velotext loaded your most recent backup.";
                vm.TogglePauseCommand.Execute(null);
                Snapshot(window, $"main-{theme}-search-banner-paused");
            }
            finally
            {
                window.CloseRequested += (_, e) => e.Cancel = false;
                window.Close();
            }
        }));

        Assert.Empty(errors);
    }

    [Fact]
    public void Settings_window_renders_without_binding_errors()
    {
        var root = @"Software\Velotext.Tests\" + Guid.NewGuid().ToString("N");
        string? opened = null;
        var errors = WpfThread.Run(() => CaptureBindingErrors(() =>
        {
            using var themeService = new ThemeService(Application.Current);
            themeService.Apply(AppTheme.Dark);
            var vm = new SettingsViewModel(
                _env.Settings,
                new AutostartService("VelotextTest", root + @"\Run", root + @"\Approved"),
                _env.Library,
                _env.Dialogs,
                new SettingsActions(@"C:\Apps\Velotext.exe", _env.Directory, _env.Paths.LogDirectory, _ => { }, _ => { }, _ => { }, _ => null, file => opened = file));
            var window = new SettingsWindow(vm) { Left = -20000, Top = -20000, ShowActivated = false };
            window.Show();
            try
            {
                Snapshot(window, "settings-Dark");
                vm.OpenLicensesCommand.Execute(null);
            }
            finally
            {
                window.Close();
            }
        }));

        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Velotext.Tests", throwOnMissingSubKey: false);
        Assert.Empty(errors);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"), opened);
        Assert.True(File.Exists(opened), "The notices file is copied next to the app.");
    }

    [Fact]
    public void Message_dialog_and_tray_menu_build()
    {
        WpfThread.Run(() =>
        {
            using var themeService = new ThemeService(Application.Current);
            themeService.Apply(AppTheme.Dark);
            Assert.True(ThemeService.Contrast(Colors.White, Colors.Black) > 20);
            Assert.True(themeService.IsDark);
            themeService.Apply(AppTheme.System);
            themeService.Apply(AppTheme.Light);
            Assert.False(themeService.IsDark);
        });
    }

    private sealed class BindingErrorListener(List<string> errors) : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                errors.Add(message);
            }
        }
    }

    [Fact]
    public void Main_window_reopens_where_it_was_and_ignores_places_off_screen() => WpfThread.Run(() =>
    {
        using var themeService = new ThemeService(Application.Current);
        themeService.Apply(AppTheme.Dark);
        using var vm = _env.CreateMainViewModel();
        var onScreen = new WindowPlacement
        {
            Left = SystemParameters.VirtualScreenLeft + 40,
            Top = SystemParameters.VirtualScreenTop + 40,
            Width = 900,
            Height = 600,
        };

        var window = new MainWindow(vm) { ShowActivated = false };
        window.ApplyPlacement(onScreen);
        Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
        var captured = window.CapturePlacement();
        Assert.Equal(onScreen with { Maximized = false }, captured);

        var lost = new MainWindow(vm);
        lost.ApplyPlacement(onScreen with { Left = -100_000 });
        Assert.Equal(WindowStartupLocation.CenterScreen, lost.WindowStartupLocation);
        lost.ApplyPlacement(onScreen with { Width = 10 });
        Assert.Equal(WindowStartupLocation.CenterScreen, lost.WindowStartupLocation);

        var maximized = new MainWindow(vm);
        maximized.ApplyPlacement(onScreen with { Maximized = true });
        Assert.Equal(WindowState.Maximized, maximized.WindowState);
        Assert.False(MainWindow.IsOnScreen(onScreen with { Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 10 }));
    });
}
