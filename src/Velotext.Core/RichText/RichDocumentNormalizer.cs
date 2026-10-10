// Velotext: cleans rich documents (merges runs, removes empty runs and control characters, caps nesting).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.Core.RichText;

/// <summary>
/// Puts a <see cref="RichDocument"/> into canonical form: adjacent runs with the same style are merged,
/// empty runs disappear, line feeds inside runs become <see cref="LineBreak"/> inlines and control
/// characters (other than Tab) are removed. Lists nested deeper than <see cref="MaxListDepth"/> are
/// flattened into their parent. Used after editing and when reading files from other people.
/// </summary>
public static class RichDocumentNormalizer
{
    /// <summary>Deepest list nesting that is kept.</summary>
    public const int MaxListDepth = 8;

    /// <summary>Returns the canonical form of a document.</summary>
    /// <param name="document">The document to clean.</param>
    public static RichDocument Normalize(RichDocument? document) =>
        document is null ? RichDocument.Empty : new RichDocument { Blocks = NormalizeBlocks(document.Blocks, depth: 0) };

    private static List<Block> NormalizeBlocks(IEnumerable<Block?>? blocks, int depth)
    {
        var result = new List<Block>();
        foreach (var block in blocks ?? [])
        {
            switch (block)
            {
                case Paragraph paragraph:
                    result.Add(new Paragraph { Inlines = NormalizeInlines(paragraph.Inlines) });
                    break;
                case ListBlock list when depth >= MaxListDepth:
                    // Too deep: keep the text, drop the extra level.
                    foreach (var item in list.Items ?? [])
                    {
                        result.AddRange(NormalizeBlocks(item?.Blocks, depth));
                    }

                    break;
                case ListBlock list:
                    var items = (list.Items ?? [])
                        .Where(item => item is not null)
                        .Select(item => new ListItem { Blocks = NormalizeBlocks(item.Blocks, depth + 1) })
                        .ToList();
                    if (items.Count > 0)
                    {
                        result.Add(new ListBlock { Kind = Enum.IsDefined(list.Kind) ? list.Kind : ListKind.Bullet, Items = items });
                    }

                    break;
            }
        }

        return result;
    }

    private static List<Inline> NormalizeInlines(IEnumerable<Inline?>? inlines)
    {
        var result = new List<Inline>();
        foreach (var inline in inlines ?? [])
        {
            switch (inline)
            {
                case TextRun run:
                    AppendText(result, run.Text, run.Style);
                    break;
                case LineBreak:
                    result.Add(LineBreak.Instance);
                    break;
                case Link link:
                    var runs = new List<Inline>();
                    foreach (var run in link.Runs ?? [])
                    {
                        if (run is not null)
                        {
                            AppendText(runs, run.Text, run.Style);
                        }
                    }

                    var textRuns = runs.OfType<TextRun>().ToList();
                    if (textRuns.Count > 0)
                    {
                        result.Add(new Link { Url = (link.Url ?? string.Empty).Trim(), Runs = textRuns });
                    }

                    break;
            }
        }

        return result;
    }

    private static void AppendText(List<Inline> result, string? text, TextStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                result.Add(LineBreak.Instance);
            }

            var clean = new string([.. lines[i].Where(c => c == '\t' || !char.IsControl(c))]);
            if (clean.Length == 0)
            {
                continue;
            }

            style &= TextStyle.Bold | TextStyle.Italic | TextStyle.Underline | TextStyle.Strikethrough;
            if (result.Count > 0 && result[^1] is TextRun previous && previous.Style == style)
            {
                result[^1] = previous with { Text = previous.Text + clean };
            }
            else
            {
                result.Add(new TextRun { Text = clean, Style = style });
            }
        }
    }
}
