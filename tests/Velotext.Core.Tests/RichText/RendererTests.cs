// Velotext: tests for the plain text, HTML, clipboard HTML and RTF renderers.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Text;
using Velotext.Core.RichText;

namespace Velotext.Core.Tests.RichText;

/// <summary>Output of the rich text renderers.</summary>
public sealed class RendererTests
{
    private static readonly RichDocument s_sample = new()
    {
        Blocks =
        [
            new Paragraph { Inlines = [new TextRun { Text = "We offer " }, new TextRun { Text = "two", Style = TextStyle.Bold | TextStyle.Italic }, new TextRun { Text = " options:" }] },
            new ListBlock
            {
                Kind = ListKind.Bullet,
                Items =
                [
                    ListItem.FromText("A caf\u00e9 voucher"),
                    new ListItem
                    {
                        Blocks =
                        [
                            Paragraph.FromText("A new parcel"),
                            new ListBlock { Kind = ListKind.Numbered, Items = [ListItem.FromText("one"), ListItem.FromText("two")] },
                        ],
                    },
                ],
            },
            new Paragraph(),
            new Paragraph { Inlines = [new TextRun { Text = "Voir " }, new Link { Url = "https://example.com/a?b=1&c=2", Runs = [new TextRun { Text = "le site" }] }, LineBreak.Instance, new TextRun { Text = "Merci", Style = TextStyle.Underline | TextStyle.Strikethrough }] },
        ],
    };

    [Fact]
    public void Plain_text_uses_markers_indentation_and_crlf()
    {
        var text = PlainTextRenderer.Render(s_sample);

        var expected = string.Join(
            "\r\n",
            "We offer two options:",
            "\u2022 A caf\u00e9 voucher",
            "\u2022 A new parcel",
            "  1. one",
            "  2. two",
            string.Empty,
            "Voir le site (https://example.com/a?b=1&c=2)",
            "Merci");
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Plain_text_omits_address_when_link_text_is_the_address()
    {
        var doc = new RichDocument
        {
            Blocks = [new Paragraph { Inlines = [new Link { Url = "mailto:a@b.com", Runs = [new TextRun { Text = "a@b.com" }] }, new TextRun { Text = " " }, new Link { Url = "javascript:x", Runs = [new TextRun { Text = "bad" }] }] }],
        };

        Assert.Equal("a@b.com bad", PlainTextRenderer.Render(doc));
    }

    [Fact]
    public void Plain_text_handles_list_item_without_paragraph_and_multiline_items()
    {
        var doc = new RichDocument
        {
            Blocks =
            [
                new ListBlock
                {
                    Kind = ListKind.Bullet,
                    Items =
                    [
                        new ListItem { Blocks = [new ListBlock { Kind = ListKind.Bullet, Items = [ListItem.FromText("inner")] }] },
                        new ListItem { Blocks = [new Paragraph { Inlines = [new TextRun { Text = "a" }, LineBreak.Instance, new TextRun { Text = "b" }] }] },
                    ],
                },
            ],
        };

        Assert.Equal("\u2022\r\n  \u2022 inner\r\n\u2022 a\r\n  b", PlainTextRenderer.Render(doc));
    }

    [Fact]
    public void Html_fragment_has_zero_margin_paragraphs_lists_styles_and_safe_links()
    {
        var html = HtmlRenderer.RenderFragment(s_sample);

        Assert.StartsWith("<p style=\"margin:0\">We offer <strong><em>two</em></strong> options:</p>", html, StringComparison.Ordinal);
        Assert.Contains("<ul style=\"margin-top:0;margin-bottom:0\"><li>A caf\u00e9 voucher</li><li>A new parcel<ol style=\"margin-top:0;margin-bottom:0\"><li>one</li><li>two</li></ol></li></ul>", html, StringComparison.Ordinal);
        Assert.Contains("<p style=\"margin:0\"><br></p>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/a?b=1&amp;c=2\">le site</a><br><u><s>Merci</s></u>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_escapes_markup_and_keeps_repeated_spaces()
    {
        var doc = RichDocument.FromPlainText(" a  <b> & \"c\"\t'd'");

        var html = HtmlRenderer.RenderFragment(doc);

        Assert.Equal("<p style=\"margin:0\">&nbsp;a &nbsp;&lt;b&gt; &amp; &quot;c&quot;&nbsp;&nbsp;&nbsp;&nbsp;&#39;d&#39;</p>", html);
    }

    [Fact]
    public void Html_drops_disallowed_links_but_keeps_text()
    {
        var doc = new RichDocument { Blocks = [new Paragraph { Inlines = [new Link { Url = "javascript:alert(1)", Runs = [new TextRun { Text = "click" }] }] }] };

        Assert.Equal("<p style=\"margin:0\">click</p>", HtmlRenderer.RenderFragment(doc));
    }

    [Fact]
    public void Html_renders_newlines_inside_runs_as_breaks()
    {
        var doc = new RichDocument { Blocks = [new Paragraph { Inlines = [new TextRun { Text = "a\r\nb" }, new TextRun { Text = string.Empty }] }] };

        Assert.Equal("<p style=\"margin:0\">a<br>b</p>", HtmlRenderer.RenderFragment(doc));
    }

    [Fact]
    public void Clipboard_html_offsets_point_at_the_right_bytes()
    {
        const string Fragment = "<p>caf\u00e9 \u2022 \ud83d\ude00</p>";

        var payload = HtmlRenderer.ToClipboardFormat(Fragment);
        var bytes = Encoding.UTF8.GetBytes(payload);

        int Offset(string name)
        {
            var start = payload.IndexOf(name + ":", StringComparison.Ordinal) + name.Length + 1;
            return int.Parse(payload.AsSpan(start, 10), CultureInfo.InvariantCulture);
        }

        var fragment = Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment"));
        Assert.Equal(Fragment, fragment);
        Assert.Equal(bytes.Length, Offset("EndHTML"));
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes, Offset("StartHTML"), 6), StringComparison.Ordinal);
    }

    [Fact]
    public void Rtf_has_font_table_size_and_escapes()
    {
        var rtf = RtfRenderer.Render(s_sample, new RtfOptions("Aptos", 11));

        Assert.StartsWith(@"{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0\fnil\fcharset0 Aptos;}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\fs22 ", rtf, StringComparison.Ordinal);
        Assert.Contains(@"caf\u233? voucher", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\b\i two}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\*\listtable{\list\listtemplateid1\listhybrid{\listlevel\levelnfc23", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\listlevel\levelnfc0\levelnfcn0\leveljc0\leveljcn0\levelfollow0\levelstartat1\levelspace0\levelindent0{\leveltext\'02\'01.;}{\levelnumbers\'01;}\fi-360\li1440\lin1440}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\*\listoverridetable{\listoverride\listid1\listoverridecount0\ls1}}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\pard\ls1\ilvl0\fi-360\li720\sa0", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\listtext \u8226 ?\tab}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\pard\ls1\ilvl1\fi-360\li1440", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\listtext 1.\tab}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\field{\*\fldinst{HYPERLINK ""https://example.com/a?b=1&c=2""}}{\fldrslt{{\ul\cf1 le site}}}}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\line ", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\ul\strike Merci}", rtf, StringComparison.Ordinal);
        Assert.EndsWith("}", rtf, StringComparison.Ordinal);
        Assert.Equal(rtf.Count(c => c == '{'), rtf.Count(c => c == '}'));
    }

    [Fact]
    public void Rtf_escaping_handles_special_and_supplementary_characters()
    {
        Assert.Equal(@"a\\b\{c\}\tab \line x\u-10179?\u-8704?", RtfRenderer.EscapeText("a\\b{c}\t\r\n\u0001x\ud83d\ude00"));
    }

    [Fact]
    public void Rtf_uses_default_font_and_clamps_size()
    {
        var rtf = RtfRenderer.Render(RichDocument.FromPlainText("x"), new RtfOptions(" ", 500));

        Assert.Contains("Aptos;", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\fs144 ", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\fs22 ", RtfRenderer.Render(RichDocument.FromPlainText("x")), StringComparison.Ordinal);
    }

    [Fact]
    public void Rtf_indents_extra_paragraphs_of_a_list_item_under_the_item_text()
    {
        var doc = new RichDocument
        {
            Blocks = [new ListBlock { Items = [new ListItem { Blocks = [Paragraph.FromText("first"), Paragraph.FromText("second")] }] }],
        };

        var rtf = RtfRenderer.Render(doc);

        Assert.Contains(@"\fi-360\li720", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\pard\li720\sa0", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\ls2", rtf, StringComparison.Ordinal);
        Assert.Contains("{second}", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void Rtf_drops_disallowed_link_but_keeps_text()
    {
        var doc = new RichDocument { Blocks = [new Paragraph { Inlines = [new Link { Url = "file:///c:/x", Runs = [new TextRun { Text = "f" }] }] }] };

        var rtf = RtfRenderer.Render(doc);

        Assert.DoesNotContain("HYPERLINK", rtf, StringComparison.Ordinal);
        Assert.Contains("{f}", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderers_reject_null()
    {
        Assert.Throws<ArgumentNullException>(() => PlainTextRenderer.Render(null!));
        Assert.Throws<ArgumentNullException>(() => PlainTextRenderer.InlineText(null!));
        Assert.Throws<ArgumentNullException>(() => HtmlRenderer.RenderFragment(null!));
        Assert.Throws<ArgumentNullException>(() => HtmlRenderer.ToClipboardFormat(null!));
        Assert.Throws<ArgumentNullException>(() => RtfRenderer.Render(null!));
        Assert.Throws<ArgumentNullException>(() => RichDocument.FromPlainText(null!));
    }
}
