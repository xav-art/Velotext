// Velotext: renders a rich document as plain text for targets that cannot take formatting.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace Velotext.Core.RichText;

/// <summary>
/// Renders a <see cref="RichDocument"/> as plain text with Windows line endings. Lists get bullet
/// or number markers, nested content is indented and links keep their address in brackets.
/// </summary>
public static class PlainTextRenderer
{
    private const string NewLine = "\r\n";

    /// <summary>Renders the document.</summary>
    /// <param name="document">The document to render.</param>
    public static string Render(RichDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lines = new List<string>();
        RenderBlocks(document.Blocks, string.Empty, lines);
        return string.Join(NewLine, lines);
    }

    /// <summary>Returns the visible text of inline content, with line breaks as <c>\n</c>.</summary>
    /// <param name="inlines">Inline content.</param>
    public static string InlineText(IEnumerable<Inline> inlines)
    {
        ArgumentNullException.ThrowIfNull(inlines);
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    // Line ends inside a run (content that skipped normalizing) become one line break.
                    text.Append(run.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                    break;
                case LineBreak:
                    text.Append('\n');
                    break;
                case Link link:
                    var linkText = string.Concat(link.Runs.Select(r => r.Text));
                    var url = LinkPolicy.Normalize(link.Url);
                    if (string.IsNullOrWhiteSpace(linkText))
                    {
                        // A link without visible text still shows where it goes.
                        text.Append(url ?? linkText);
                    }
                    else
                    {
                        text.Append(linkText);
                        if (url is not null && !SameTarget(linkText, url))
                        {
                            text.Append(" (").Append(url).Append(')');
                        }
                    }

                    break;
            }
        }

        return text.ToString();
    }

    private static void RenderBlocks(IEnumerable<Block> blocks, string indent, List<string> lines)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (var line in InlineText(paragraph.Inlines).Split('\n'))
                    {
                        lines.Add(line.Length == 0 ? string.Empty : indent + line);
                    }

                    break;
                case ListBlock list:
                    RenderList(list, indent, lines);
                    break;
            }
        }
    }

    private static void RenderList(ListBlock list, string indent, List<string> lines)
    {
        for (var index = 0; index < list.Items.Count; index++)
        {
            var marker = list.Kind == ListKind.Bullet
                ? "• "
                : string.Create(CultureInfo.InvariantCulture, $"{index + 1}. ");
            var hangingIndent = indent + new string(' ', marker.Length);
            var item = list.Items[index];
            var rest = item.Blocks;

            if (item.Blocks.Count > 0 && item.Blocks[0] is Paragraph first)
            {
                var itemLines = InlineText(first.Inlines).Split('\n');
                lines.Add(indent + marker + itemLines[0]);
                lines.AddRange(itemLines.Skip(1).Select(line => line.Length == 0 ? string.Empty : hangingIndent + line));
                rest = [.. item.Blocks.Skip(1)];
            }
            else
            {
                lines.Add(indent + marker.TrimEnd());
            }

            RenderBlocks(rest, hangingIndent, lines);
        }
    }

    private static bool SameTarget(string linkText, string url)
    {
        var text = linkText.Trim();
        return text.Length == 0
            || string.Equals(text, url, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || string.Equals("mailto:" + text, url, StringComparison.OrdinalIgnoreCase)
            || string.Equals("tel:" + text, url, StringComparison.OrdinalIgnoreCase);
    }
}
