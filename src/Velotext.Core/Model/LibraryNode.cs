// Velotext: base type for every node in the phrase library tree (folders and phrases).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Velotext.Core.Model;

/// <summary>
/// A node in the phrase library tree. Folders hold other nodes; phrases are leaves.
/// Nodes are immutable so the expansion engine can read a consistent snapshot without locks.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Folder), "folder")]
[JsonDerivedType(typeof(Phrase), "phrase")]
public abstract record LibraryNode
{
    /// <summary>Stable unique identifier, kept across edits, moves and renames.</summary>
    public required Guid Id { get; init; }
}
