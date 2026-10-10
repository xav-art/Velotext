// Velotext: tests for the rich document model, normaliser and link policy.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.RichText;

namespace Velotext.Core.Tests.RichText;

/// <summary>Model helpers, normalisation and link safety.</summary>
public sealed class RichDocumentTests
{
    [Fact]
    public void From_plain_text_makes_one_paragraph_per_line()
    {
        var doc = RichDocument.FromPlainText("a\r\nb\rc\n\nd");

        Assert.Equal(5, doc.Blocks.Count);
        Assert.Empty(((Paragraph)doc.Blocks[3]).Inlines);
        Assert.False(doc.HasFormatting);
        Assert.False(doc.IsEmpty);
    }

    [Fact]
    public void Has_formatting_detects_styles_lists_and_links()
    {
        Assert.True(new RichDocument { Blocks = [new Paragraph { Inlines = [new TextRun { Text = "x", Style = TextStyle.Bold }] }] }.HasFormatting);
        Assert.True(new RichDocument { Blocks = [new ListBlock { Items = [ListItem.FromText("x")] }] }.HasFormatting);
        Assert.True(new RichDocument { Blocks = [new Paragraph { Inlines = [new Link { Url = "https://a.b", Runs = [new TextRun { Text = "x" }] }] }] }.HasFormatting);
        Assert.False(new RichDocument { Blocks = [new Paragraph { Inlines = [LineBreak.Instance] }] }.HasFormatting);
        Assert.True(RichDocument.Empty.IsEmpty);
    }

    [Fact]
    public void Normalizer_merges_runs_splits_newlines_and_strips_control_characters()
    {
        var doc = new RichDocument
        {
            Blocks =
            [
                new Paragraph
                {
                    Inlines =
                    [
                        new TextRun { Text = "a" },
                        new TextRun { Text = "b\u0007" },
                        new TextRun { Text = string.Empty, Style = TextStyle.Bold },
                        new TextRun { Text = "c\nd", Style = TextStyle.Bold | (TextStyle)64 },
                        new TextRun { Text = "\t" },
                        LineBreak.Instance,
                        new Link { Url = " https://x.y ", Runs = [new TextRun { Text = "l" }, new TextRun { Text = "m" }] },
                        new Link { Url = "https://x.y", Runs = [new TextRun { Text = string.Empty }] },
                    ],
                },
            ],
        };

        var normalized = RichDocumentNormalizer.Normalize(doc);

        var inlines = ((Paragraph)normalized.Blocks[0]).Inlines;
        Assert.Equal("ab", ((TextRun)inlines[0]).Text);
        Assert.Equal(new TextRun { Text = "c", Style = TextStyle.Bold }, inlines[1]);
        Assert.IsType<LineBreak>(inlines[2]);
        Assert.Equal(new TextRun { Text = "d", Style = TextStyle.Bold }, inlines[3]);
        Assert.Equal("\t", ((TextRun)inlines[4]).Text);
        Assert.IsType<LineBreak>(inlines[5]);
        var link = Assert.IsType<Link>(inlines[6]);
        Assert.Equal("https://x.y", link.Url);
        Assert.Equal("lm", Assert.Single(link.Runs).Text);
        Assert.Equal(7, inlines.Count);
    }

    [Fact]
    public void Normalizer_flattens_lists_nested_too_deep_and_drops_empty_lists()
    {
        Block nested = new ListBlock { Items = [ListItem.FromText("deepest")] };
        for (var i = 0; i < 12; i++)
        {
            nested = new ListBlock { Kind = ListKind.Numbered, Items = [new ListItem { Blocks = [nested] }] };
        }

        var doc = new RichDocument { Blocks = [nested, new ListBlock(), new ListBlock { Kind = (ListKind)9, Items = [ListItem.FromText("x")] }] };

        var normalized = RichDocumentNormalizer.Normalize(doc);

        var depth = 0;
        Block? current = normalized.Blocks[0];
        while (current is ListBlock list)
        {
            depth++;
            current = list.Items[0].Blocks[0];
        }

        Assert.Equal(RichDocumentNormalizer.MaxListDepth, depth);
        Assert.Equal("deepest", PlainTextRenderer.InlineText(((Paragraph)current!).Inlines));
        Assert.Equal(2, normalized.Blocks.Count);
        Assert.Equal(ListKind.Bullet, ((ListBlock)normalized.Blocks[1]).Kind);
        Assert.Same(RichDocument.Empty, RichDocumentNormalizer.Normalize(null));
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("example.com/path", "https://example.com/path")]
    [InlineData("mailto:service@example.com", "mailto:service@example.com")]
    [InlineData("tel:+4512345678", "tel:+4512345678")]
    [InlineData("HTTP://EXAMPLE.COM", "http://example.com/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///c:/windows", null)]
    [InlineData("C:\\Windows", null)]
    [InlineData("hello world", null)]
    [InlineData("https://exa mple.com", null)]
    [InlineData("http:///path-only", null)]
    [InlineData("hello", null)]
    [InlineData("intranet.example/wiki", "https://intranet.example/wiki")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Link_policy_allows_only_web_mail_and_phone(string? input, string? expected) =>
        Assert.Equal(expected, LinkPolicy.Normalize(input));

    [Fact]
    public void Link_policy_rejects_overlong_addresses() =>
        Assert.Null(LinkPolicy.Normalize("https://example.com/" + new string('a', 3000)));
}
