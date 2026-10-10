// Velotext: the notification-area (tray) icon with its menu, paused state and notifications.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Velotext.App.Interop;

namespace Velotext.App.Services;

/// <summary>Actions the tray menu triggers.</summary>
/// <param name="Open">Show the main window.</param>
/// <param name="TogglePause">Pause or resume expansion.</param>
/// <param name="OpenSettings">Show settings.</param>
/// <param name="Exit">Exit Velotext.</param>
public sealed record TrayActions(Action Open, Action TogglePause, Action OpenSettings, Action Exit);

/// <summary>
/// The tray icon. Click it to open Velotext; right-click for the menu. A grey icon and "(paused)" in the
/// tooltip show that expansion is paused, so the state is never shown by colour alone.
/// </summary>
public sealed class TrayService : INotificationService, IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly Icon _activeIcon;
    private readonly Icon _pausedIcon;

    /// <summary>Creates and shows the tray icon. Call on the UI thread.</summary>
    /// <param name="actions">What the menu items do.</param>
    public TrayService(TrayActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _activeIcon = LoadIcon("tray.ico");
        _pausedIcon = LoadIcon("tray-paused.ico");

        _pauseItem = new MenuItem { Header = "Pause expansion", Command = new RelayCommand(actions.TogglePause) };
        var menu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Open Velotext", Command = new RelayCommand(actions.Open), FontWeight = FontWeights.SemiBold },
                _pauseItem,
                new MenuItem { Header = "Settings", Command = new RelayCommand(actions.OpenSettings) },
                new Separator(),
                new MenuItem { Header = "Exit Velotext", Command = new RelayCommand(actions.Exit) },
            },
        };

        _icon = new TaskbarIcon
        {
            ToolTipText = "Velotext",
            Icon = _activeIcon,
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(actions.Open),
            MenuActivation = PopupActivationMode.RightClick,
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>Shows the paused or active state.</summary>
    /// <param name="paused">True when expansion is paused.</param>
    public void SetPaused(bool paused)
    {
        _icon.Dispatcher.BeginInvoke(() =>
        {
            _icon.Icon = paused ? _pausedIcon : _activeIcon;
            _icon.ToolTipText = paused ? "Velotext (paused)" : "Velotext";
            _pauseItem.Header = paused ? "Resume expansion" : "Pause expansion";
        });
    }

    /// <inheritdoc />
    public void Show(string title, string message, NotificationKind kind)
    {
        _icon.Dispatcher.BeginInvoke(() =>
            _icon.ShowNotification(title, message, kind == NotificationKind.Warning ? NotificationIcon.Warning : NotificationIcon.Info));
    }

    /// <summary>Removes the icon.</summary>
    public void Dispose()
    {
        _icon.Dispose();
        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }

    private static Icon LoadIcon(string name)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Velotext;component/Assets/{name}", UriKind.Absolute))
            ?? throw new InvalidOperationException($"Missing icon resource {name}.");
        using var stream = resource.Stream;
        var size = NativeMethods.GetSystemMetrics(NativeMethods.SmCxSmIcon);
        return new Icon(stream, size, size);
    }
}
