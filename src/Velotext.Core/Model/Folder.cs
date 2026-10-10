// Velotext: a named folder that groups phrases and other folders.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.Core.Model;

/// <summary>A named container in the library tree. Folders can nest to any depth.</summary>
public sealed record Folder : LibraryNode
{
    /// <summary>Display name shown in the tree.</summary>
    public required string Name { get; init; }

    /// <summary>Child folders and phrases, in display order.</summary>
    public IReadOnlyList<LibraryNode> Children { get; init; } = [];
}
