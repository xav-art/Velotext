// Velotext: behaviour of the small modal dialog used for confirmations, errors and the link address.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;

namespace Velotext.App.Views;

/// <summary>A small modal dialog. Use the static helpers rather than creating it directly.</summary>
public sealed partial class MessageDialog : Window
{
    private MessageDialog(Window? owner, string title, string message, string confirmText, string? cancelText)
    {
        InitializeComponent();
        Owner = owner is { IsVisible: true } ? owner : null;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmText;
        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.IsCancel = true;
        }
        else
        {
            CancelButton.Content = cancelText;
        }
    }

    /// <summary>Asks a question. Returns true when the user chose the confirm button.</summary>
    /// <param name="owner">Owner window.</param>
    /// <param name="title">The question.</param>
    /// <param name="message">The consequence.</param>
    /// <param name="confirmText">The verb on the confirm button.</param>
    public static bool Confirm(Window? owner, string title, string message, string confirmText)
    {
        var dialog = new MessageDialog(owner, title, message, confirmText, "Cancel");
        dialog.Loaded += (_, _) => dialog.CancelButton.Focus();
        return dialog.ShowDialog() == true;
    }

    /// <summary>Shows a message with a single Close button.</summary>
    /// <param name="owner">Owner window.</param>
    /// <param name="title">What happened.</param>
    /// <param name="message">What to do.</param>
    public static void Show(Window? owner, string title, string message) =>
        new MessageDialog(owner, title, message, "Close", null).ShowDialog();

    /// <summary>Asks for a line of text.</summary>
    /// <param name="owner">Owner window.</param>
    /// <param name="title">What to enter.</param>
    /// <param name="message">Help text.</param>
    /// <param name="initial">Initial value.</param>
    /// <param name="confirmText">The verb on the confirm button.</param>
    /// <returns>The text, or <c>null</c> when cancelled.</returns>
    public static string? Prompt(Window? owner, string title, string message, string? initial, string confirmText)
    {
        var dialog = new MessageDialog(owner, title, message, confirmText, "Cancel");
        dialog.InputBox.Visibility = Visibility.Visible;
        dialog.InputBox.Text = initial ?? string.Empty;
        System.Windows.Automation.AutomationProperties.SetName(dialog.InputBox, title);
        dialog.Loaded += (_, _) =>
        {
            dialog.InputBox.Focus();
            dialog.InputBox.SelectAll();
        };
        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
