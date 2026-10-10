// Velotext: export and import of folders and phrases in Velotext's own file format.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Velotext.Core.Model;

namespace Velotext.Core.Storage;

/// <summary>A Velotext phrase file (.velotext): folders and phrases shared between users.</summary>
public sealed record ExchangeDocument
{
    /// <summary>Identifies the file type.</summary>
    public const string FormatName = "velotext-phrases";

    /// <summary>Format version written by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>File type marker; must be <see cref="FormatName"/>.</summary>
    public string Format { get; init; } = FormatName;

    /// <summary>Format version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>When the file was exported (UTC).</summary>
    public DateTimeOffset ExportedUtc { get; init; }

    /// <summary>The exported folders and phrases.</summary>
    public IReadOnlyList<LibraryNode> Items { get; init; } = [];
}

/// <summary>Writes and reads Velotext phrase files. Reading treats the file as untrusted input.</summary>
public static class PhraseExchange
{
    /// <summary>File extension of Velotext phrase files.</summary>
    public const string FileExtension = ".velotext";

    /// <summary>Largest file accepted on import (20 MB).</summary>
    public const int MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>Writes the nodes to <paramref name="destination"/>.</summary>
    /// <param name="destination">Writable stream.</param>
    /// <param name="items">Folders and phrases to export.</param>
    /// <param name="now">Export timestamp.</param>
    public static void Export(Stream destination, IEnumerable<LibraryNode> items, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(items);
        var document = new ExchangeDocument { ExportedUtc = now, Items = [.. items] };
        JsonSerializer.Serialize(destination, document, VelotextJsonContext.Default.ExchangeDocument);
    }

    /// <summary>Reads a phrase file and prepares its nodes for insertion (new ids, limits enforced).</summary>
    /// <param name="source">Readable stream.</param>
    /// <param name="now">Timestamp for nodes that have none.</param>
    /// <exception cref="InvalidPhraseFileException">The file is not a valid phrase file or exceeds a limit.</exception>
    public static IReadOnlyList<LibraryNode> Import(Stream source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bytes = ReadCapped(source);

        ExchangeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(bytes, VelotextJsonContext.Default.ExchangeDocument);
        }
        catch (JsonException ex)
        {
            throw new InvalidPhraseFileException("The file is not a valid Velotext phrase file.", ex);
        }

        if (document is null || !string.Equals(document.Format, ExchangeDocument.FormatName, StringComparison.Ordinal))
        {
            throw new InvalidPhraseFileException("The file is not a Velotext phrase file.");
        }

        if (document.Version > ExchangeDocument.CurrentVersion)
        {
            throw new InvalidPhraseFileException(string.Create(
                CultureInfo.InvariantCulture,
                $"The file was made by a newer Velotext (format {document.Version}). Update Velotext to import it."));
        }

        return LibrarySanitizer.PrepareImport(document.Items, now);
    }

    private static byte[] ReadCapped(Stream source)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
            {
                throw new InvalidPhraseFileException("The file is larger than 20 MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
