// Velotext: turns HTML from the clipboard (for example copied from a browser) into Velotext rich text.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Velotext.Core.RichText;

namespace Velotext.App.Editing;

/// <summary>
/// Converts clipboard HTML to rich text, keeping only what Velotext stores: paragraphs, line breaks,
/// bulleted and numbered lists, bold, italic, underline, strikethrough and links. Scripts, styles,
/// images and everything else are dropped. Uses AngleSharp, a standards-compliant HTML parser.
/// </summary>
public static class HtmlImporter
{
    private const string StartMarker = "<!--StartFragment-->";
    private const string EndMarker = "<!--EndFragment-->";

    private static readonly HashSet<string> s_skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "meta", "template", "noscript", "img", "svg", "object", "iframe", "button", "input", "select", "textarea",
    };

    private static readonly HashSet<string> s_blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "section", "article", "header", "footer", "aside", "main", "nav", "address", "figure", "figcaption", "tr", "td", "th", "dt", "dd", "hr",
    };

    /// <summary>Returns the fragment part of a Windows "HTML Format" payload, or the whole text when there are no markers.</summary>
    /// <param name="clipboardHtml">The clipboard payload.</param>
    public static string ExtractFragment(string clipboardHtml)
    {
        ArgumentNullException.ThrowIfNull(clipboardHtml);
        var start = clipboardHtml.IndexOf(StartMarker, StringComparison.OrdinalIgnoreCase);
        var end = clipboardHtml.IndexOf(EndMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            return clipboardHtml[(start + StartMarker.Length)..end];
        }

        var html = clipboardHtml.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        return html >= 0 ? clipboardHtml[html..] : clipboardHtml;
    }

    /// <summary>Parses HTML into rich text.</summary>
    /// <param name="html">An HTML fragment or document.</param>
    public static RichDocument Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");
        var builder = new Builder();
        builder.Visit(document.Body!, TextStyle.None, link: null, preformatted: false);
        return RichDocumentNormalizer.Normalize(new RichDocument { Blocks = builder.Finish() });
    }

    private static TextStyle StyleFromAttribute(IElement element)
    {
        var css = element.GetAttribute("style");
        if (string.IsNullOrEmpty(css))
        {
            return TextStyle.None;
        }

        var style = TextStyle.None;
        foreach (var declaration in css.Split(';'))
        {
            var parts = declaration.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var name = parts[0].Trim().ToUpperInvariant();
            var value = parts[1].Trim().ToUpperInvariant();
            switch (name)
            {
                case "FONT-WEIGHT" when value is "BOLD" or "BOLDER" or "600" or "700" or "800" or "900":
                    style |= TextStyle.Bold;
                    break;
                case "FONT-STYLE" when value is "ITALIC" or "OBLIQUE":
                    style |= TextStyle.Italic;
                    break;
                case "TEXT-DECORATION" or "TEXT-DECORATION-LINE":
                    if (value.Contains("UNDERLINE", StringComparison.Ordinal))
                    {
                        style |= TextStyle.Underline;
                    }

                    if (value.Contains("LINE-THROUGH", StringComparison.Ordinal))
                    {
                        style |= TextStyle.Strikethrough;
                    }

                    break;
            }
        }

        return style;
    }

    /// <summary>Builds blocks while walking the DOM.</summary>
    private sealed class Builder
    {
        private readonly Stack<List<Block>> _targets = new();
        private List<Inline>? _paragraph;
        private List<TextRun>? _linkRuns;
        private string? _linkUrl;

        public Builder() => _targets.Push([]);

        public List<Block> Finish()
        {
            FlushParagraph();
            return _targets.Peek();
        }

        public void Visit(INode node, TextStyle style, string? link, bool preformatted)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText text:
                        AddText(text.Data, style, link, preformatted);
                        break;
                    case IElement element:
                        VisitElement(element, style, link, preformatted);
                        break;
                }
            }
        }

        private void VisitElement(IElement element, TextStyle style, string? link, bool preformatted)
        {
            var tag = element.LocalName;
            if (s_skipped.Contains(tag) || (element.GetAttribute("style")?.Contains("mso-list:Ignore", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return;
            }

            style |= StyleFromAttribute(element);
            switch (tag.ToUpperInvariant())
            {
                case "BR":
                    Inlines().Add(LineBreak.Instance);
                    return;
                case "B" or "STRONG":
                    style |= TextStyle.Bold;
                    break;
                case "I" or "EM":
                    style |= TextStyle.Italic;
                    break;
                case "U" or "INS":
                    style |= TextStyle.Underline;
                    break;
                case "S" or "STRIKE" or "DEL":
                    style |= TextStyle.Strikethrough;
                    break;
                case "A" when element.GetAttribute("href") is { Length: > 0 } href && LinkPolicy.Normalize(href) is not null:
                    link = href;
                    break;
                case "UL" or "OL":
                    VisitList(element, style, preformatted);
                    return;
                case "LI":
                    // A list item outside a list: treat it as its own paragraph.
                    FlushParagraph();
                    Visit(element, style, link, preformatted);
                    FlushParagraph();
                    return;
            }

            if (tag.Length == 2 && tag[0] is 'h' or 'H' && char.IsDigit(tag[1]))
            {
                style |= TextStyle.Bold;
            }

            if (s_blocks.Contains(tag))
            {
                FlushParagraph();
                Visit(element, style, link, preformatted || tag.Equals("pre", StringComparison.OrdinalIgnoreCase));
                FlushParagraph();
                return;
            }

            Visit(element, style, link, preformatted);
        }

        private void VisitList(IElement list, TextStyle style, bool preformatted)
        {
            FlushParagraph();
            var items = new List<ListItem>();
            foreach (var child in list.Children)
            {
                if (child.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase))
                {
                    _targets.Push([]);
                    Visit(child, style, link: null, preformatted);
                    FlushParagraph();
                    items.Add(new ListItem { Blocks = _targets.Pop() });
                }
                else if (child.LocalName is "ul" or "ol")
                {
                    // A list nested directly in a list belongs to the previous item.
                    _targets.Push(items.Count > 0 ? [.. items[^1].Blocks] : []);
                    VisitList(child, style, preformatted);
                    var blocks = _targets.Pop();
                    if (items.Count > 0)
                    {
                        items[^1] = new ListItem { Blocks = blocks };
                    }
                    else
                    {
                        items.Add(new ListItem { Blocks = blocks });
                    }
                }
            }

            if (items.Count > 0)
            {
                _targets.Peek().Add(new ListBlock { Kind = list.LocalName.Equals("ol", StringComparison.OrdinalIgnoreCase) ? ListKind.Numbered : ListKind.Bullet, Items = items });
            }
        }

        private void AddText(string data, TextStyle style, string? link, bool preformatted)
        {
            var text = preformatted ? data.Replace("\r\n", "\n", StringComparison.Ordinal) : CollapseWhitespace(data);
            if (text.Length == 0)
            {
                return;
            }

            if (!preformatted && (_paragraph is null || EndsWithSpace()) && text[0] == ' ')
            {
                text = text[1..];
                if (text.Length == 0)
                {
                    return;
                }
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    Inlines().Add(LineBreak.Instance);
                }

                if (lines[i].Length == 0)
                {
                    continue;
                }

                var run = new TextRun { Text = lines[i].Replace(' ', ' '), Style = style };
                if (link is not null)
                {
                    AddLinkRun(link, run);
                }
                else
                {
                    CloseLink();
                    Inlines().Add(run);
                }
            }
        }

        private void AddLinkRun(string url, TextRun run)
        {
            if (_linkRuns is null || !string.Equals(_linkUrl, url, StringComparison.Ordinal))
            {
                CloseLink();
                _linkRuns = [];
                _linkUrl = url;
                Inlines().Add(new Link { Url = url, Runs = _linkRuns });
            }

            _linkRuns.Add(run);
        }

        private void CloseLink()
        {
            _linkRuns = null;
            _linkUrl = null;
        }

        private bool EndsWithSpace() => _paragraph switch
        {
            null or [] => true,
            [.., TextRun last] => last.Text.EndsWith(' '),
            [.., LineBreak] => true,
            _ => false,
        };

        private List<Inline> Inlines() => _paragraph ??= [];

        private void FlushParagraph()
        {
            CloseLink();
            if (_paragraph is null)
            {
                return;
            }

            // Trailing spaces before a block end are not visible in HTML either.
            if (_paragraph is [.., TextRun last] && last.Text.EndsWith(' '))
            {
                _paragraph[^1] = last with { Text = last.Text.TrimEnd(' ') };
            }

            _targets.Peek().Add(new Paragraph { Inlines = _paragraph });
            _paragraph = null;
        }

        private static string CollapseWhitespace(string text)
        {
            var builder = new StringBuilder(text.Length);
            var previousSpace = false;
            foreach (var c in text)
            {
                var isSpace = c is ' ' or '\t' or '\r' or '\n' or '\f';
                if (isSpace)
                {
                    if (!previousSpace)
                    {
                        builder.Append(' ');
                    }
                }
                else
                {
                    builder.Append(c);
                }

                previousSpace = isSpace;
            }

            return builder.ToString();
        }
    }
}
