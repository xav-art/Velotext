// Velotext: the root of a user's phrase library and helpers to walk it.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.RichText;

namespace Velotext.Core.Model;

/// <summary>A user's complete phrase library: an ordered list of top-level folders and phrases.</summary>
public sealed record PhraseLibrary
{
    /// <summary>Schema version written by this build. Bumped when the file layout changes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the stored file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Top-level folders and phrases, in display order.</summary>
    public IReadOnlyList<LibraryNode> Items { get; init; } = [];

    /// <summary>An empty library.</summary>
    public static PhraseLibrary Empty { get; } = new();

    /// <summary>
    /// Creates the library a first-time user starts with: one folder holding a welcome phrase
    /// that demonstrates formatting and an autotext trigger.
    /// </summary>
    /// <param name="now">Creation timestamp to stamp on the new nodes.</param>
    public static PhraseLibrary CreateStarter(DateTimeOffset now)
    {
        var welcome = new Phrase
        {
            Id = Guid.NewGuid(),
            Description = "Welcome to Velotext",
            Content = new RichDocument
            {
                Blocks =
                [
                    new Paragraph { Inlines = [new TextRun { Text = "Velotext inserts this text when you type ", Style = TextStyle.None }, new TextRun { Text = "VTDEMO", Style = TextStyle.Bold }, new TextRun { Text = "." }] },
                    new ListBlock
                    {
                        Kind = ListKind.Bullet,
                        Items =
                        [
                            ListItem.FromText("Velotext keeps formatting such as bold and lists."),
                            ListItem.FromText("Press Backspace right away to undo the expansion."),
                        ],
                    },
                ],
            },
            Triggers = [new AutotextTrigger { Text = "VTDEMO" }],
            CreatedUtc = now,
            ModifiedUtc = now,
        };

        return new PhraseLibrary
        {
            Items = [new Folder { Id = Guid.NewGuid(), Name = "My phrases", Children = [welcome] }],
        };
    }

    /// <summary>Enumerates every phrase in the library, depth first, in display order.</summary>
    public IEnumerable<Phrase> EnumeratePhrases() => EnumeratePhrases(Items);

    /// <summary>Enumerates every folder in the library, depth first, in display order.</summary>
    public IEnumerable<Folder> EnumerateFolders() => EnumerateFolders(Items);

    /// <summary>Finds a phrase by id, or returns <c>null</c>.</summary>
    /// <param name="id">The phrase id.</param>
    public Phrase? FindPhrase(Guid id) => EnumeratePhrases().FirstOrDefault(p => p.Id == id);

    private static IEnumerable<Phrase> EnumeratePhrases(IEnumerable<LibraryNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case Phrase phrase:
                    yield return phrase;
                    break;
                case Folder folder:
                    foreach (var child in EnumeratePhrases(folder.Children))
                    {
                        yield return child;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<Folder> EnumerateFolders(IEnumerable<LibraryNode> nodes)
    {
        foreach (var folder in nodes.OfType<Folder>())
        {
            yield return folder;
            foreach (var child in EnumerateFolders(folder.Children))
            {
                yield return child;
            }
        }
    }
}
