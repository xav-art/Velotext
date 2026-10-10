// Velotext: where an expanded phrase is delivered.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.Core.Model;

/// <summary>Where an expanded phrase is delivered.</summary>
public enum OutputTarget
{
    /// <summary>Insert the phrase at the caret of the application that has focus (default).</summary>
    CurrentApplication = 0,

    /// <summary>Copy the phrase to the clipboard without inserting it.</summary>
    Clipboard = 1,
}
