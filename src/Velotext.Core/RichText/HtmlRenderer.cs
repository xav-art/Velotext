// Velotext: renders a rich document as an HTML fragment and as a Windows clipboard HTML payload.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace Velotext.Core.RichText;

/// <summary>
/// Renders a <see cref="RichDocument"/> as HTML. Paragraphs carry zero margins so pasted text keeps
/// single line spacing in Word, Outlook and browser editors. No fonts or colours are emitted, so the
/// target application's own style applies.
/// </summary>
public static class HtmlRenderer
{
    private const string ParagraphOpen = "<p style=\"margin:0\">";
    private const string ListStyle = " style=\"margin-top:0;margin-bottom:0\"";

    /// <summary>Renders the document as an HTML fragment (no html or body element).</summary>
    /// <param name="document">The document to render.</param>
    public static string RenderFragment(RichDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var html = new StringBuilder();
        RenderBlocks(document.Blocks, html);
        return html.ToString();
    }

    /// <summary>
    /// Wraps an HTML fragment in the Windows clipboard "HTML Format" envelope. The byte offsets in the
    /// header are computed for UTF-8, which is the encoding the format requires.
    /// </summary>
    /// <param name="fragment">The HTML fragment to wrap.</param>
    public static string ToClipboardFormat(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        const string HeaderTemplate =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string Prefix =
            "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"></head><body>\r\n<!--StartFragment-->";
        const string Suffix = "<!--EndFragment-->\r\n</body></html>";

        var utf8 = Encoding.UTF8;
        var headerLength = utf8.GetByteCount(string.Format(CultureInfo.InvariantCulture, HeaderTemplate, 0, 0, 0, 0));
        var startHtml = headerLength;
        var startFragment = startHtml + utf8.GetByteCount(Prefix);
        var endFragment = startFragment + utf8.GetByteCount(fragment);
        var endHtml = endFragment + utf8.GetByteCount(Suffix);

        return string.Format(CultureInfo.InvariantCulture, HeaderTemplate, startHtml, endHtml, startFragment, endFragment)
            + Prefix + fragment + Suffix;
    }

    private static void RenderBlocks(IEnumerable<Block> blocks, StringBuilder html)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    html.Append(ParagraphOpen);
                    if (paragraph.Inlines.Count == 0 || PlainTextRenderer.InlineText(paragraph.Inlines).Length == 0)
                    {
                        html.Append("<br>");
                    }
                    else
                    {
                        RenderInlines(paragraph.Inlines, html);

                        // Browsers ignore a final <br> in a block, so a paragraph that ends with a line
                        // break needs a second one to keep the empty line. Word honors the first one, so the
                        // extra break sits in the conditional that Word itself writes for this case.
                        if (paragraph.Inlines[^1] is LineBreak)
                        {
                            html.Append("<![if !supportLineBreakNewLine]><br><![endif]>");
                        }
                    }

                    html.Append("</p>");
                    break;
                case ListBlock list:
                    RenderList(list, html);
                    break;
            }
        }
    }

    private static void RenderList(ListBlock list, StringBuilder html)
    {
        var tag = list.Kind == ListKind.Bullet ? "ul" : "ol";
        html.Append('<').Append(tag).Append(ListStyle).Append('>');
        foreach (var item in list.Items)
        {
            html.Append("<li>");
            var rest = item.Blocks.AsEnumerable();
            if (item.Blocks.Count > 0 && item.Blocks[0] is Paragraph first)
            {
                RenderInlines(first.Inlines, html);
                rest = item.Blocks.Skip(1);
            }

            RenderBlocks(rest, html);
            html.Append("</li>");
        }

        html.Append("</").Append(tag).Append('>');
    }

    private static void RenderInlines(IEnumerable<Inline> inlines, StringBuilder html)
    {
        // A leading space would collapse, so the start of a paragraph counts as "after a space".
        var spacing = new SpaceState { PreviousWasSpace = true };
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    RenderRun(run, html, spacing);
                    break;
                case LineBreak:
                    html.Append("<br>");
                    spacing.PreviousWasSpace = true;
                    break;
                case Link link:
                    var url = LinkPolicy.Normalize(link.Url);
                    if (url is not null)
                    {
                        html.Append("<a href=\"").Append(Encode(url)).Append("\">");
                    }

                    foreach (var run in link.Runs)
                    {
                        RenderRun(run, html, spacing);
                    }

                    if (url is not null)
                    {
                        html.Append("</a>");
                    }

                    break;
            }
        }
    }

    private static void RenderRun(TextRun run, StringBuilder html, SpaceState spacing)
    {
        if (run.Text.Length == 0)
        {
            return;
        }

        var style = run.Style;
        if (style.HasFlag(TextStyle.Bold))
        {
            html.Append("<strong>");
        }

        if (style.HasFlag(TextStyle.Italic))
        {
            html.Append("<em>");
        }

        if (style.HasFlag(TextStyle.Underline))
        {
            html.Append("<u>");
        }

        if (style.HasFlag(TextStyle.Strikethrough))
        {
            html.Append("<s>");
        }

        html.Append(EncodeText(run.Text, spacing));

        if (style.HasFlag(TextStyle.Strikethrough))
        {
            html.Append("</s>");
        }

        if (style.HasFlag(TextStyle.Underline))
        {
            html.Append("</u>");
        }

        if (style.HasFlag(TextStyle.Italic))
        {
            html.Append("</em>");
        }

        if (style.HasFlag(TextStyle.Bold))
        {
            html.Append("</strong>");
        }
    }

    /// <summary>
    /// Encodes text content and keeps runs of spaces and tabs visible, since HTML collapses them.
    /// Only the second and later spaces of a run become non-breaking, so normal line wrapping still works.
    /// </summary>
    private static string EncodeText(string text, SpaceState spacing)
    {
        var encoded = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case ' ':
                    encoded.Append(spacing.PreviousWasSpace ? "&nbsp;" : " ");
                    spacing.PreviousWasSpace = true;
                    continue;
                case '\t':
                    encoded.Append("&nbsp;&nbsp;&nbsp;&nbsp;");
                    spacing.PreviousWasSpace = true;
                    continue;
                case '\r':
                    continue;
                case '\n':
                    encoded.Append("<br>");
                    spacing.PreviousWasSpace = true;
                    continue;
                default:
                    encoded.Append(Encode(c));
                    spacing.PreviousWasSpace = false;
                    continue;
            }
        }

        return encoded.ToString();
    }

    private static string Encode(string value)
    {
        var encoded = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            encoded.Append(Encode(c));
        }

        return encoded.ToString();
    }

    /// <summary>Tracks whether the last emitted character was white space, across the runs of one paragraph.</summary>
    private sealed class SpaceState
    {
        public bool PreviousWasSpace { get; set; }
    }

    private static string Encode(char c) => c switch
    {
        '&' => "&amp;",
        '<' => "&lt;",
        '>' => "&gt;",
        '"' => "&quot;",
        '\'' => "&#39;",
        _ => c.ToString(),
    };
}
