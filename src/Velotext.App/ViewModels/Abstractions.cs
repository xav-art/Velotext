// Velotext: UI seams (dialogs, dispatcher) that keep view models testable without windows.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.App.ViewModels;

/// <summary>Dialogs and file pickers.</summary>
public interface IDialogService
{
    /// <summary>Asks the user to confirm an action.</summary>
    /// <param name="title">A question with the verb and the object, for example "Delete this phrase?".</param>
    /// <param name="message">The consequence.</param>
    /// <param name="confirmText">The verb on the confirm button, for example "Delete phrase".</param>
    /// <param name="destructive">True when the action removes data.</param>
    /// <returns>True when the user confirmed.</returns>
    bool Confirm(string title, string message, string confirmText, bool destructive);

    /// <summary>Shows an error with what happened and what to do.</summary>
    /// <param name="title">What failed.</param>
    /// <param name="message">What to do.</param>
    void ShowError(string title, string message);

    /// <summary>Asks for a file to open.</summary>
    /// <param name="filter">File type filter, in the Windows dialog format.</param>
    /// <param name="initialDirectory">Optional start folder.</param>
    /// <returns>The chosen path, or <c>null</c>.</returns>
    string? PickFileToOpen(string filter, string? initialDirectory = null);

    /// <summary>Asks for a file to save to.</summary>
    /// <param name="filter">File type filter, in the Windows dialog format.</param>
    /// <param name="defaultName">Suggested file name.</param>
    /// <returns>The chosen path, or <c>null</c>.</returns>
    string? PickFileToSave(string filter, string defaultName);
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues work for the UI thread (runs at once when already on it).</summary>
    /// <param name="action">The work.</param>
    void Post(Action action);
}
