// Velotext: settings window code-behind (binds the view model).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using Velotext.App.ViewModels;

namespace Velotext.App.Views;

/// <summary>The settings window. Changes apply immediately; there is no Save button.</summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>Creates the window.</summary>
    /// <param name="viewModel">The settings view model.</param>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
