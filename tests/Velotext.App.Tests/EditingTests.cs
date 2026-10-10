// Velotext: tests for the editor mapping (FlowDocument round trips, WPF reading our RTF) and HTML import.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Velotext.App.Editing;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using LineBreak = Velotext.Core.RichText.LineBreak;
using ListItem = Velotext.Core.RichText.ListItem;
using Paragraph = Velotext.Core.RichText.Paragraph;

namespace Velotext.App.Tests;

/// <summary>Rich text conversions between Velotext, WPF and HTML.</summary>
public sealed class EditingTests
{
    private static readonly RichDocument s_sample = new()
    {
        Blocks =
        [
            new Paragraph { Inlines = [new TextRun { Text = "Nous pouvons " }, new TextRun { Text = "deux", Style = TextStyle.Bold | TextStyle.Underline }, new TextRun { Text = " solutions", Style = TextStyle.Italic | TextStyle.Strikethrough }] },
            new ListBlock { Kind = ListKind.Bullet, Items = [ListItem.FromText("Remboursement immédiat"), new ListItem { Blocks = [Paragraph.FromText("Renvoi"), new ListBlock { Kind = ListKind.Numbered, Items = [ListItem.FromText("un")] }] }] },
            new Paragraph(),
            new Paragraph { Inlines = [new Link { Url = "https://example.com/", Runs = [new TextRun { Text = "site" }] }, LineBreak.Instance, new TextRun { Text = "fin" }] },
        ],
    };

    private static string Html(RichDocument document) => HtmlRenderer.RenderFragment(document);

    [Fact]
    public void Flow_document_round_trip_keeps_every_supported_feature() => WpfThread.Run(() =>
    {
        var flow = FlowDocumentMapper.ToFlowDocument(s_sample, new FontFamily("Aptos"), 11);

        var back = FlowDocumentMapper.FromFlowDocument(flow);

        Assert.Equal(Html(s_sample), Html(back));
        Assert.Equal(11 * 96.0 / 72.0, flow.FontSize, 3);
    });

    [Fact]
    public void Empty_document_maps_to_one_empty_paragraph_and_back_to_empty() => WpfThread.Run(() =>
    {
        var flow = FlowDocumentMapper.ToFlowDocument(RichDocument.Empty, new FontFamily("Aptos"), 11);

        Assert.Single(flow.Blocks);
        Assert.Same(RichDocument.Empty, FlowDocumentMapper.FromFlowDocument(flow));
    });

    [Fact]
    public void Wpf_reads_velotext_rtf_with_lists_styles_and_links() => WpfThread.Run(() =>
    {
        // Our RTF goes to Word and classic Outlook; WPF's RTF reader is a strict, independent check of it.
        var rtf = RtfRenderer.Render(s_sample, RtfOptions.Default);
        var data = new DataObject();
        data.SetData(DataFormats.Rtf, rtf);

        var back = RichTextEditor.ReadRich(data)!;

        Assert.Equal(PlainTextRenderer.Render(s_sample).Replace("• ", string.Empty, StringComparison.Ordinal), PlainTextRenderer.Render(back).Replace("• ", string.Empty, StringComparison.Ordinal));
        Assert.Contains(back.Blocks, b => b is ListBlock);
        var first = (Paragraph)back.Blocks[0];
        Assert.Contains(first.Inlines.OfType<TextRun>(), r => r.Text == "deux" && r.Style == (TextStyle.Bold | TextStyle.Underline));
        Assert.Contains(back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines), i => i is Link { Url: "https://example.com/" });
    });

    [Fact]
    public void Clipboard_html_without_rtf_is_read_through_the_html_importer() => WpfThread.Run(() =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, HtmlRenderer.ToClipboardFormat("<p>Hi <b>there</b></p>"));

        var back = RichTextEditor.ReadRich(data)!;

        Assert.Equal("<p style=\"margin:0\">Hi <strong>there</strong></p>", Html(back));
        Assert.Null(RichTextEditor.ReadRich(new DataObject(DataFormats.UnicodeText, "plain")));
    });

    [Fact]
    public void Wpf_spans_and_hyperlinks_map_to_styles_and_links() => WpfThread.Run(() =>
    {
        var paragraph = new System.Windows.Documents.Paragraph();
        paragraph.Inlines.Add(new Bold(new Underline(new Run("a"))));
        paragraph.Inlines.Add(new Hyperlink(new Run("b")) { NavigateUri = new Uri("https://x.y/") });
        paragraph.Inlines.Add(new Hyperlink(new Run("c")));
        var section = new Section(paragraph);
        var table = new Table();
        var group = new TableRowGroup();
        var row = new TableRow();
        row.Cells.Add(new TableCell(new System.Windows.Documents.Paragraph(new Run("cell"))));
        group.Rows.Add(row);
        table.RowGroups.Add(group);
        var flow = new FlowDocument();
        flow.Blocks.Add(section);
        flow.Blocks.Add(table);
        flow.Blocks.Add(new System.Windows.Documents.List(new System.Windows.Documents.ListItem(new System.Windows.Documents.Paragraph(new Run("n")))) { MarkerStyle = TextMarkerStyle.LowerRoman });

        var back = FlowDocumentMapper.FromFlowDocument(flow);

        Assert.Equal("<p style=\"margin:0\"><strong><u>a</u></strong><a href=\"https://x.y/\">b</a>c</p><p style=\"margin:0\">cell</p><ol style=\"margin-top:0;margin-bottom:0\"><li>n</li></ol>", Html(back));
    });

    [Theory]
    [InlineData("<p>One</p><p>Two</p>", "<p style=\"margin:0\">One</p><p style=\"margin:0\">Two</p>")]
    [InlineData("<div>a<br>b</div>", "<p style=\"margin:0\">a<br>b</p>")]
    [InlineData("<b>bold</b> <i>it</i> <u>u</u> <s>s</s> <strong>x</strong><em>y</em><del>z</del><ins>w</ins>", "<p style=\"margin:0\"><strong>bold</strong> <em>it</em> <u>u</u> <s>s</s> <strong>x</strong><em>y</em><s>z</s><u>w</u></p>")]
    [InlineData("<span style=\"font-weight:700; font-style:italic; text-decoration: underline line-through\">all</span>", "<p style=\"margin:0\"><strong><em><u><s>all</s></u></em></strong></p>")]
    [InlineData("<ul><li>a</li><li>b<ol><li>c</li></ol></li></ul>", "<ul style=\"margin-top:0;margin-bottom:0\"><li>a</li><li>b<ol style=\"margin-top:0;margin-bottom:0\"><li>c</li></ol></li></ul>")]
    [InlineData("<ul><li>a</li><ul><li>nested</li></ul></ul>", "<ul style=\"margin-top:0;margin-bottom:0\"><li>a<ul style=\"margin-top:0;margin-bottom:0\"><li>nested</li></ul></li></ul>")]
    [InlineData("<a href=\"https://example.com\">link <b>bold</b></a> after", "<p style=\"margin:0\"><a href=\"https://example.com/\">link <strong>bold</strong></a> after</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<p style=\"margin:0\">x</p>")]
    [InlineData("<script>alert(1)</script><style>p{}</style><p>safe</p><img src=x>", "<p style=\"margin:0\">safe</p>")]
    [InlineData("<h2>Title</h2>text", "<p style=\"margin:0\"><strong>Title</strong></p><p style=\"margin:0\">text</p>")]
    [InlineData("<pre>a  b\nc</pre>", "<p style=\"margin:0\">a &nbsp;b<br>c</p>")]
    [InlineData("  lots   of\n   space  ", "<p style=\"margin:0\">lots of space</p>")]
    [InlineData("<li>orphan</li>", "<p style=\"margin:0\">orphan</p>")]
    [InlineData("<p class=MsoListParagraph><span style='mso-list:Ignore'>·&nbsp;</span>item</p>", "<p style=\"margin:0\">item</p>")]
    [InlineData("<table><tr><td>a</td><td>b</td></tr></table>", "<p style=\"margin:0\">a</p><p style=\"margin:0\">b</p>")]
    public void Html_import_keeps_supported_formatting_only(string html, string expected) =>
        Assert.Equal(expected, Html(HtmlImporter.Parse(html)));

    [Fact]
    public void Html_fragment_is_extracted_from_clipboard_payload()
    {
        var payload = HtmlRenderer.ToClipboardFormat("<b>x</b>");

        Assert.Equal("<b>x</b>", HtmlImporter.ExtractFragment(payload));
        Assert.Equal("<html><body>y</body></html>", HtmlImporter.ExtractFragment("Version:1\r\n<html><body>y</body></html>"));
        Assert.Equal("plain", HtmlImporter.ExtractFragment("plain"));
    }

    [Fact]
    public void Hotkey_box_maps_modifiers()
    {
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, HotkeyBox.ToHotkeyModifiers(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, windowsKeyDown: false));
        Assert.Equal(HotkeyModifiers.Windows, HotkeyBox.ToHotkeyModifiers(ModifierKeys.None, windowsKeyDown: true));
        Assert.Equal(HotkeyModifiers.Windows, HotkeyBox.ToHotkeyModifiers(ModifierKeys.Windows, windowsKeyDown: false));
    }

    [Fact]
    public void Encoding_round_trip_of_non_ascii_rtf_text() => WpfThread.Run(() =>
    {
        var doc = RichDocument.FromPlainText("Grüße • € æøå");
        var data = new DataObject();
        data.SetData(DataFormats.Rtf, RtfRenderer.Render(new RichDocument { Blocks = [.. doc.Blocks, new ListBlock { Items = [ListItem.FromText("x")] }] }));

        var back = RichTextEditor.ReadRich(data)!;

        Assert.StartsWith("Grüße • € æøå", PlainTextRenderer.Render(back), StringComparison.Ordinal);
    });
}
