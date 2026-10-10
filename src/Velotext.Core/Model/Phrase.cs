// Velotext: a reusable piece of formatted text with the triggers that insert it.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text.Json.Serialization;
using Velotext.Core.RichText;

namespace Velotext.Core.Model;

/// <summary>A reusable piece of formatted text and the triggers that insert it.</summary>
public sealed record Phrase : LibraryNode
{
    /// <summary>Short human-readable name shown in the tree and in search results. Stored as <c>name</c>.</summary>
    [JsonPropertyName("name")]
    public required string Description { get; init; }

    /// <summary>The formatted text that gets inserted.</summary>
    public RichDocument Content { get; init; } = RichDocument.Empty;

    /// <summary>Autotext and hotkey triggers that insert this phrase. A phrase may have none.</summary>
    public IReadOnlyList<Trigger> Triggers { get; init; } = [];

    /// <summary>Where the phrase goes when a trigger fires.</summary>
    public OutputTarget Output { get; init; } = OutputTarget.ActiveApplication;

    /// <summary>When the phrase was created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>When the phrase was last changed (UTC).</summary>
    public DateTimeOffset ModifiedUtc { get; init; }
}
