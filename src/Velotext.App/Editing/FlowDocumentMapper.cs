// Velotext: converts between WPF FlowDocuments (the editor) and Velotext's rich text model (storage and output).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Velotext.Core.RichText;
using Block = Velotext.Core.RichText.Block;
using Inline = Velotext.Core.RichText.Inline;
using LineBreak = Velotext.Core.RichText.LineBreak;
using ListItem = Velotext.Core.RichText.ListItem;
using Paragraph = Velotext.Core.RichText.Paragraph;
using WpfBlock = System.Windows.Documents.Block;
using WpfLineBreak = System.Windows.Documents.LineBreak;
using WpfList = System.Windows.Documents.List;
using WpfListItem = System.Windows.Documents.ListItem;
using WpfParagraph = System.Windows.Documents.Paragraph;

namespace Velotext.App.Editing;

/// <summary>
/// Maps a <see cref="RichDocument"/> to a WPF <see cref="FlowDocument"/> for editing and back. Only the
/// features Velotext stores survive the round trip (bold, italic, underline, strikethrough, lists, links,
/// line breaks); fonts, sizes and colours from pasted text are dropped on purpose.
/// </summary>
public static class FlowDocumentMapper
{
    private static readonly TextMarkerStyle[] s_numberedMarkers =
    [
        TextMarkerStyle.Decimal, TextMarkerStyle.LowerLatin, TextMarkerStyle.UpperLatin, TextMarkerStyle.LowerRoman, TextMarkerStyle.UpperRoman,
    ];

    /// <summary>Builds an editable document.</summary>
    /// <param name="document">The rich text.</param>
    /// <param name="font">Editor font (the output font, so the preview matches what recipients see).</param>
    /// <param name="fontSizePoints">Editor font size in points.</param>
    public static FlowDocument ToFlowDocument(RichDocument document, FontFamily font, double fontSizePoints)
    {
        ArgumentNullException.ThrowIfNull(document);
        var flow = new FlowDocument
        {
            FontFamily = font,
            FontSize = fontSizePoints * 96.0 / 72.0,
            PagePadding = new Thickness(0),
        };
        AddBlocks(flow.Blocks, document.Blocks);
        if (flow.Blocks.Count == 0)
        {
            flow.Blocks.Add(new WpfParagraph { Margin = new Thickness(0) });
        }

        return flow;
    }

    /// <summary>Reads the rich text back from an edited document.</summary>
    /// <param name="flow">The edited document.</param>
    public static RichDocument FromFlowDocument(FlowDocument flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var blocks = ConvertBlocks(flow.Blocks);

        // An editor always holds one paragraph; a single empty one means "no content".
        if (blocks is [Paragraph { Inlines.Count: 0 }])
        {
            return RichDocument.Empty;
        }

        return RichDocumentNormalizer.Normalize(new RichDocument { Blocks = blocks });
    }

    private static void AddBlocks(BlockCollection target, IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    var wpfParagraph = new WpfParagraph { Margin = new Thickness(0) };
                    AddInlines(wpfParagraph.Inlines, paragraph.Inlines);
                    target.Add(wpfParagraph);
                    break;
                case ListBlock list:
                    var wpfList = new WpfList
                    {
                        MarkerStyle = list.Kind == ListKind.Numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0),
                        Padding = new Thickness(24, 0, 0, 0),
                    };
                    foreach (var item in list.Items)
                    {
                        var wpfItem = new WpfListItem();
                        AddBlocks(wpfItem.Blocks, item.Blocks);
                        if (wpfItem.Blocks.Count == 0)
                        {
                            wpfItem.Blocks.Add(new WpfParagraph { Margin = new Thickness(0) });
                        }

                        wpfList.ListItems.Add(wpfItem);
                    }

                    target.Add(wpfList);
                    break;
            }
        }
    }

    private static void AddInlines(InlineCollection target, IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    target.Add(CreateRun(run));
                    break;
                case LineBreak:
                    target.Add(new WpfLineBreak());
                    break;
                case Link link:
                    var hyperlink = new Hyperlink();
                    if (LinkPolicy.Normalize(link.Url) is { } url)
                    {
                        // The tooltip shows the real address, so a link cannot hide where it goes.
                        hyperlink.NavigateUri = new Uri(url);
                        hyperlink.ToolTip = url;
                    }

                    foreach (var run in link.Runs)
                    {
                        hyperlink.Inlines.Add(CreateRun(run));
                    }

                    target.Add(hyperlink);
                    break;
            }
        }
    }

    private static Run CreateRun(TextRun run)
    {
        var wpfRun = new Run(run.Text);
        if (run.Style.HasFlag(TextStyle.Bold))
        {
            wpfRun.FontWeight = FontWeights.Bold;
        }

        if (run.Style.HasFlag(TextStyle.Italic))
        {
            wpfRun.FontStyle = FontStyles.Italic;
        }

        if (run.Style.HasFlag(TextStyle.Underline) || run.Style.HasFlag(TextStyle.Strikethrough))
        {
            var decorations = new TextDecorationCollection();
            if (run.Style.HasFlag(TextStyle.Underline))
            {
                decorations.Add(TextDecorations.Underline);
            }

            if (run.Style.HasFlag(TextStyle.Strikethrough))
            {
                decorations.Add(TextDecorations.Strikethrough);
            }

            wpfRun.TextDecorations = decorations;
        }

        return wpfRun;
    }

    private static List<Block> ConvertBlocks(BlockCollection blocks)
    {
        var result = new List<Block>();
        foreach (var block in blocks)
        {
            ConvertBlock(block, result);
        }

        return result;
    }

    private static void ConvertBlock(WpfBlock block, List<Block> result)
    {
        switch (block)
        {
            case WpfParagraph paragraph:
                result.Add(new Paragraph { Inlines = ConvertInlines(paragraph.Inlines, paragraph.TextDecorations) });
                break;
            case WpfList list:
                result.Add(new ListBlock
                {
                    Kind = s_numberedMarkers.Contains(list.MarkerStyle) ? ListKind.Numbered : ListKind.Bullet,
                    Items = [.. list.ListItems.Select(item => new ListItem { Blocks = ConvertBlocks(item.Blocks) })],
                });
                break;
            case Section section:
                result.AddRange(ConvertBlocks(section.Blocks));
                break;
            case Table table:
                foreach (var cell in table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells))
                {
                    result.AddRange(ConvertBlocks(cell.Blocks));
                }

                break;
        }
    }

    private static List<Inline> ConvertInlines(InlineCollection inlines, TextDecorationCollection? inherited)
    {
        var result = new List<Inline>();
        foreach (var inline in inlines)
        {
            var decorations = Merge(inherited, inline.TextDecorations);
            switch (inline)
            {
                case Run run:
                    result.Add(new TextRun { Text = run.Text, Style = StyleOf(run, decorations) });
                    break;
                case WpfLineBreak:
                    result.Add(LineBreak.Instance);
                    break;
                case Hyperlink hyperlink:
                    // A hyperlink's own underline comes from its default look, not from the user's formatting.
                    var runs = ConvertInlines(hyperlink.Inlines, inherited).OfType<TextRun>().ToList();
                    var url = hyperlink.NavigateUri?.ToString();
                    if (string.IsNullOrEmpty(url))
                    {
                        result.AddRange(runs);
                    }
                    else
                    {
                        // Links are always shown underlined, so an underline on link text adds nothing.
                        result.Add(new Link { Url = url, Runs = [.. runs.Select(r => r with { Style = r.Style & ~TextStyle.Underline })] });
                    }

                    break;
                case Span span:
                    result.AddRange(ConvertInlines(span.Inlines, decorations));
                    break;
            }
        }

        return result;
    }

    private static TextStyle StyleOf(Run run, TextDecorationCollection? decorations)
    {
        var style = TextStyle.None;
        if (run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight())
        {
            style |= TextStyle.Bold;
        }

        if (run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique)
        {
            style |= TextStyle.Italic;
        }

        if (decorations is not null)
        {
            if (decorations.Any(d => d.Location == TextDecorationLocation.Underline))
            {
                style |= TextStyle.Underline;
            }

            if (decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough))
            {
                style |= TextStyle.Strikethrough;
            }
        }

        return style;
    }

    private static TextDecorationCollection? Merge(TextDecorationCollection? inherited, TextDecorationCollection? own)
    {
        if (inherited is null || inherited.Count == 0)
        {
            return own;
        }

        if (own is null || own.Count == 0)
        {
            return inherited;
        }

        var merged = new TextDecorationCollection(inherited);
        foreach (var decoration in own)
        {
            merged.Add(decoration);
        }

        return merged;
    }
}
