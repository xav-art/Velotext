// Velotext: repairs and limits phrase library content read from disk or from other people's files.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using Velotext.Core.Model;
using Velotext.Core.RichText;

namespace Velotext.Core.Storage;

/// <summary>Raised when a phrase file cannot be imported. The message is safe to show to the user.</summary>
public sealed class InvalidPhraseFileException : Exception
{
    /// <summary>Creates the exception.</summary>
    public InvalidPhraseFileException()
        : this("The file is not a valid Velotext phrase file.")
    {
    }

    /// <summary>Creates the exception with a user-facing message.</summary>
    /// <param name="message">What is wrong, in plain English.</param>
    public InvalidPhraseFileException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a user-facing message and the underlying cause.</summary>
    /// <param name="message">What is wrong, in plain English.</param>
    /// <param name="innerException">The underlying error.</param>
    public InvalidPhraseFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Repairs library content: trims names, gives empty names a placeholder, normalises rich text,
/// drops broken triggers, fills missing timestamps and makes ids unique. For imports it also enforces
/// size limits, so a hostile or broken file cannot exhaust memory or bury the user's library.
/// </summary>
public static class LibrarySanitizer
{
    /// <summary>Deepest folder nesting accepted on import.</summary>
    public const int MaxFolderDepth = 16;

    /// <summary>Most folders and phrases accepted in one import.</summary>
    public const int MaxNodes = 20_000;

    /// <summary>Longest folder name or phrase description, in characters.</summary>
    public const int MaxNameLength = 300;

    /// <summary>Most triggers on one phrase.</summary>
    public const int MaxTriggersPerPhrase = 16;

    /// <summary>Longest phrase content accepted on import, in characters.</summary>
    public const int MaxContentCharacters = 200_000;

    /// <summary>Repairs the user's own library. Ids are kept, except duplicates which get new ids.</summary>
    /// <param name="library">The loaded library.</param>
    /// <param name="now">Timestamp for nodes that have none.</param>
    public static PhraseLibrary Repair(PhraseLibrary library, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(library);
        var context = new Context(now, regenerateIds: false, enforceLimits: false);
        return new PhraseLibrary { Items = SanitizeNodes(library.Items, depth: 0, context) };
    }

    /// <summary>
    /// Prepares nodes from a file for import: all ids are regenerated so they never clash with the
    /// user's library, and size limits are enforced.
    /// </summary>
    /// <param name="nodes">Nodes read from the file.</param>
    /// <param name="now">Timestamp for nodes that have none.</param>
    /// <exception cref="InvalidPhraseFileException">The file exceeds a limit.</exception>
    public static IReadOnlyList<LibraryNode> PrepareImport(IEnumerable<LibraryNode?>? nodes, DateTimeOffset now)
    {
        var context = new Context(now, regenerateIds: true, enforceLimits: true);
        return SanitizeNodes(nodes, depth: 0, context);
    }

    private static List<LibraryNode> SanitizeNodes(IEnumerable<LibraryNode?>? nodes, int depth, Context context)
    {
        var result = new List<LibraryNode>();
        foreach (var node in nodes ?? [])
        {
            if (node is null)
            {
                continue;
            }

            if (context.EnforceLimits && ++context.NodeCount > MaxNodes)
            {
                throw new InvalidPhraseFileException(string.Create(CultureInfo.InvariantCulture, $"The file holds more than {MaxNodes} folders and phrases."));
            }

            switch (node)
            {
                case Folder folder:
                    if (context.EnforceLimits && depth >= MaxFolderDepth)
                    {
                        throw new InvalidPhraseFileException(string.Create(CultureInfo.InvariantCulture, $"Folders are nested more than {MaxFolderDepth} levels deep."));
                    }

                    result.Add(new Folder
                    {
                        Id = context.IdFor(folder.Id),
                        Name = CleanName(folder.Name, "Untitled folder"),
                        Children = SanitizeNodes(folder.Children, depth + 1, context),
                    });
                    break;
                case Phrase phrase:
                    result.Add(SanitizePhrase(phrase, context));
                    break;
            }
        }

        return result;
    }

    private static Phrase SanitizePhrase(Phrase phrase, Context context)
    {
        var content = RichDocumentNormalizer.Normalize(phrase.Content);
        if (context.EnforceLimits && PlainTextRenderer.Render(content).Length > MaxContentCharacters)
        {
            throw new InvalidPhraseFileException(string.Create(CultureInfo.InvariantCulture, $"A phrase is longer than {MaxContentCharacters} characters."));
        }

        var triggers = new List<Trigger>();
        foreach (var trigger in phrase.Triggers ?? [])
        {
            switch (trigger)
            {
                case AutotextTrigger autotext:
                    var text = new string([.. (autotext.Text ?? string.Empty).Where(c => !char.IsControl(c))]);
                    triggers.Add(autotext with { Text = text.Length > AutotextTrigger.MaxLength ? text[..AutotextTrigger.MaxLength] : text });
                    break;
                case HotkeyTrigger hotkey when hotkey.Gesture.Key is > 0 and < 0xFF:
                    triggers.Add(hotkey);
                    break;
            }
        }

        if (context.EnforceLimits && triggers.Count > MaxTriggersPerPhrase)
        {
            throw new InvalidPhraseFileException(string.Create(CultureInfo.InvariantCulture, $"A phrase has more than {MaxTriggersPerPhrase} triggers."));
        }

        var created = phrase.CreatedUtc == default ? context.Now : phrase.CreatedUtc;
        return new Phrase
        {
            Id = context.IdFor(phrase.Id),
            Description = CleanName(phrase.Description, "Untitled phrase"),
            Content = content,
            Triggers = triggers,
            Output = Enum.IsDefined(phrase.Output) ? phrase.Output : OutputTarget.CurrentApplication,
            CreatedUtc = created,
            ModifiedUtc = phrase.ModifiedUtc == default ? created : phrase.ModifiedUtc,
        };
    }

    private static string CleanName(string? name, string fallback)
    {
        var clean = new string([.. (name ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        if (clean.Length > MaxNameLength)
        {
            clean = clean[..MaxNameLength].TrimEnd();
        }

        return clean.Length == 0 ? fallback : clean;
    }

    private sealed class Context(DateTimeOffset now, bool regenerateIds, bool enforceLimits)
    {
        private readonly HashSet<Guid> _seen = [];

        public DateTimeOffset Now { get; } = now;

        public bool EnforceLimits { get; } = enforceLimits;

        public int NodeCount { get; set; }

        public Guid IdFor(Guid original)
        {
            var id = regenerateIds || original == Guid.Empty || _seen.Contains(original) ? Guid.NewGuid() : original;
            _seen.Add(id);
            return id;
        }
    }
}
