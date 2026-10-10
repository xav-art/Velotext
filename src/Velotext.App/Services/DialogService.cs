// Velotext: WPF implementation of dialogs and file pickers, plus the UI dispatcher.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Velotext.App.ViewModels;
using Velotext.App.Views;

namespace Velotext.App.Services;

/// <summary>Shows dialogs owned by the active Velotext window.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    /// <inheritdoc />
    public bool Confirm(string title, string message, string confirmText, bool destructive) =>
        MessageDialog.Confirm(Owner, title, message, confirmText);

    /// <inheritdoc />
    public void ShowError(string title, string message) => MessageDialog.Show(Owner, title, message);

    /// <inheritdoc />
    public string? PickFileToOpen(string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickFileToSave(string filter, string defaultName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}

/// <summary>Posts work to the WPF dispatcher.</summary>
/// <param name="dispatcher">The UI dispatcher.</param>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
