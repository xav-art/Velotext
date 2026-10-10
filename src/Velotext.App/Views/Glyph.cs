// Velotext: an icon-font glyph that screen readers skip (the surrounding control carries the name).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Velotext.App.Views;

/// <summary>
/// Shows one icon-font character. It has no automation peer, so screen readers do not read the private-use
/// character; the button or row around it provides the accessible name.
/// </summary>
public sealed class Glyph : TextBlock
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => null!;
}
