// Velotext: the formatted-text document model shared by the editor, storage and clipboard renderers.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Velotext.Core.RichText;

/// <summary>Character formatting flags that can be combined on a text run.</summary>
[Flags]
public enum TextStyle
{
    /// <summary>Plain text.</summary>
    None = 0,

    /// <summary>Bold.</summary>
    Bold = 1,

    /// <summary>Italic.</summary>
    Italic = 2,

    /// <summary>Underlined.</summary>
    Underline = 4,

    /// <summary>Struck through.</summary>
    Strikethrough = 8,
}

/// <summary>The kind of marker a list uses.</summary>
public enum ListKind
{
    /// <summary>Bulleted list.</summary>
    Bullet = 0,

    /// <summary>Numbered list (1, 2, 3).</summary>
    Numbered = 1,
}

/// <summary>
/// A formatted document: paragraphs and lists made of styled text, links and line breaks.
/// Fonts and colours are deliberately not stored, so pasted text takes the target application's style.
/// </summary>
public sealed record RichDocument
{
    /// <summary>Top-level blocks in reading order.</summary>
    public IReadOnlyList<Block> Blocks { get; init; } = [];

    /// <summary>A document with no content.</summary>
    public static RichDocument Empty { get; } = new();

    /// <summary>True when the document holds no visible text.</summary>
    [JsonIgnore]
    public bool IsEmpty => PlainTextRenderer.Render(this).Length == 0;

    /// <summary>True when the document uses any formatting that plain text cannot express.</summary>
    [JsonIgnore]
    public bool HasFormatting => Blocks.Any(BlockHasFormatting);

    /// <summary>Builds a document from plain text; each line becomes a paragraph.</summary>
    /// <param name="text">Text with any mix of CR, LF or CRLF line endings.</param>
    public static RichDocument FromPlainText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return new RichDocument
        {
            Blocks = [.. lines.Select(line => (Block)Paragraph.FromText(line))],
        };
    }

    private static bool BlockHasFormatting(Block block) => block switch
    {
        ListBlock => true,
        Paragraph paragraph => paragraph.Inlines.Any(inline => inline switch
        {
            Link => true,
            TextRun run => run.Style != TextStyle.None,
            _ => false,
        }),
        _ => false,
    };
}

/// <summary>A block-level element: a paragraph or a list.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Paragraph), "paragraph")]
[JsonDerivedType(typeof(ListBlock), "list")]
public abstract record Block;

/// <summary>A paragraph of inline content.</summary>
public sealed record Paragraph : Block
{
    /// <summary>Inline content in reading order. Empty means a blank line.</summary>
    public IReadOnlyList<Inline> Inlines { get; init; } = [];

    /// <summary>Creates a paragraph holding one plain run, or a blank paragraph for empty text.</summary>
    /// <param name="text">The paragraph text.</param>
    public static Paragraph FromText(string text) =>
        string.IsNullOrEmpty(text) ? new Paragraph() : new Paragraph { Inlines = [new TextRun { Text = text }] };
}

/// <summary>A bulleted or numbered list.</summary>
public sealed record ListBlock : Block
{
    /// <summary>Bullet or numbered.</summary>
    public ListKind Kind { get; init; }

    /// <summary>The list items, in order.</summary>
    public IReadOnlyList<ListItem> Items { get; init; } = [];
}

/// <summary>One list item; it can hold paragraphs and nested lists.</summary>
public sealed record ListItem
{
    /// <summary>Blocks inside the item. The first one is usually a paragraph.</summary>
    public IReadOnlyList<Block> Blocks { get; init; } = [];

    /// <summary>Creates an item holding one plain paragraph.</summary>
    /// <param name="text">The item text.</param>
    public static ListItem FromText(string text) => new() { Blocks = [Paragraph.FromText(text)] };
}

/// <summary>Inline content: a styled text run, a line break or a link.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextRun), "text")]
[JsonDerivedType(typeof(LineBreak), "break")]
[JsonDerivedType(typeof(Link), "link")]
public abstract record Inline;

/// <summary>A run of text that shares one style.</summary>
public sealed record TextRun : Inline
{
    /// <summary>The text. Never contains line breaks; those are separate <see cref="LineBreak"/> inlines.</summary>
    public required string Text { get; init; }

    /// <summary>Formatting applied to the whole run.</summary>
    public TextStyle Style { get; init; }
}

/// <summary>A line break inside a paragraph (Shift+Enter in most editors).</summary>
public sealed record LineBreak : Inline
{
    /// <summary>The shared instance; line breaks carry no data.</summary>
    public static LineBreak Instance { get; } = new();
}

/// <summary>A hyperlink around one or more text runs.</summary>
public sealed record Link : Inline
{
    /// <summary>Target address. Only addresses accepted by <see cref="LinkPolicy"/> are rendered as links.</summary>
    public required string Url { get; init; }

    /// <summary>The visible link text.</summary>
    public IReadOnlyList<TextRun> Runs { get; init; } = [];
}
