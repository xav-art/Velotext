// Velotext: renders a rich document as RTF for Word, classic Outlook and other RTF consumers.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace Velotext.Core.RichText;

/// <summary>Font settings used when rendering RTF, because RTF consumers need an explicit font.</summary>
/// <param name="FontName">Font family name, for example <c>Aptos</c>.</param>
/// <param name="FontSizePoints">Font size in points.</param>
public sealed record RtfOptions(string FontName, double FontSizePoints)
{
    /// <summary>Default: Aptos 11 pt, the Microsoft 365 default body font.</summary>
    public static RtfOptions Default { get; } = new("Aptos", 11);
}

/// <summary>
/// Renders a <see cref="RichDocument"/> as RTF. Lists use the RTF list table (the format Word writes),
/// so nesting and numbering survive; every list paragraph also carries its marker as plain text for
/// readers without list support. Non-ASCII text is written as <c>\u</c> escapes.
/// </summary>
public static class RtfRenderer
{
    private const int MaxLevels = 9;
    private const int IndentStepTwips = 720;

    /// <summary>Renders the document.</summary>
    /// <param name="document">The document to render.</param>
    /// <param name="options">Font settings; <see cref="RtfOptions.Default"/> when null.</param>
    public static string Render(RichDocument document, RtfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= RtfOptions.Default;
        var halfPoints = (int)Math.Round(Math.Clamp(options.FontSizePoints, 6, 72) * 2);
        var fontName = string.IsNullOrWhiteSpace(options.FontName) ? RtfOptions.Default.FontName : options.FontName.Trim();

        var plans = new List<ListPlan>();
        var placements = new Dictionary<ListBlock, (ListPlan Plan, int Level)>(ReferenceEqualityComparer.Instance);
        PlanLists(document.Blocks, level: -1, plans, placements);

        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1");
        rtf.Append(@"{\fonttbl{\f0\fnil\fcharset0 ").Append(EscapeText(fontName)).Append(";}}");
        rtf.Append(@"{\colortbl ;\red5\green99\blue193;}");
        AppendListTables(rtf, plans);
        rtf.Append("\r\n");

        var context = new RenderContext(rtf, halfPoints, placements);
        RenderBlocks(document.Blocks, indentLevel: -1, context);
        rtf.Append('}');
        return rtf.ToString();
    }

    /// <summary>Escapes text for RTF: control characters, braces, backslashes and non-ASCII characters.</summary>
    internal static string EscapeText(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                    escaped.Append(@"\\");
                    break;
                case '{':
                    escaped.Append(@"\{");
                    break;
                case '}':
                    escaped.Append(@"\}");
                    break;
                case '\t':
                    escaped.Append(@"\tab ");
                    break;
                case '\r':
                    break;
                case '\n':
                    escaped.Append(@"\line ");
                    break;
                case < ' ':
                    break;
                case < (char)0x80:
                    escaped.Append(c);
                    break;
                default:
                    // \uN takes a signed 16-bit value; "?" is the fallback for readers without Unicode support.
                    escaped.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }

        return escaped.ToString();
    }

    private static void PlanLists(IEnumerable<Block> blocks, int level, List<ListPlan> plans, Dictionary<ListBlock, (ListPlan, int)> placements)
    {
        foreach (var list in blocks.OfType<ListBlock>())
        {
            // Every list gets its own definition, so each keeps its own bullet or number style and its
            // numbering starts at 1, also when several lists share a parent item.
            var listLevel = Math.Min(level + 1, MaxLevels - 1);
            var plan = new ListPlan(plans.Count + 1);
            plans.Add(plan);
            plan.Kinds[listLevel] = list.Kind;
            placements[list] = (plan, listLevel);
            foreach (var item in list.Items)
            {
                PlanLists(item.Blocks, listLevel, plans, placements);
            }
        }
    }

    private static void AppendListTables(StringBuilder rtf, List<ListPlan> plans)
    {
        if (plans.Count == 0)
        {
            return;
        }

        rtf.Append(@"{\*\listtable");
        foreach (var plan in plans)
        {
            rtf.Append(@"{\list\listtemplateid").Append(plan.Id.ToString(CultureInfo.InvariantCulture)).Append(@"\listhybrid");
            var kind = ListKind.Bullet;
            for (var level = 0; level < MaxLevels; level++)
            {
                kind = plan.Kinds[level] ?? kind;
                var indent = Indent(level).ToString(CultureInfo.InvariantCulture);
                if (kind == ListKind.Numbered)
                {
                    rtf.Append(@"{\listlevel\levelnfc0\levelnfcn0\leveljc0\leveljcn0\levelfollow0\levelstartat1\levelspace0\levelindent0")
                        .Append(@"{\leveltext\'02\'").Append(level.ToString("x2", CultureInfo.InvariantCulture)).Append(".;}{\\levelnumbers\\'01;}");
                }
                else
                {
                    rtf.Append(@"{\listlevel\levelnfc23\levelnfcn23\leveljc0\leveljcn0\levelfollow0\levelstartat1\levelspace0\levelindent0")
                        .Append(@"{\leveltext\'01\u8226 ?;}{\levelnumbers;}");
                }

                rtf.Append(@"\fi-360\li").Append(indent).Append(@"\lin").Append(indent).Append('}');
            }

            rtf.Append(@"{\listname ;}\listid").Append(plan.Id.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        rtf.Append('}');
        rtf.Append(@"{\*\listoverridetable");
        foreach (var plan in plans)
        {
            var id = plan.Id.ToString(CultureInfo.InvariantCulture);
            rtf.Append(@"{\listoverride\listid").Append(id).Append(@"\listoverridecount0\ls").Append(id).Append('}');
        }

        rtf.Append('}');
    }

    private static int Indent(int level) => IndentStepTwips * (level + 1);

    private static void RenderBlocks(IEnumerable<Block> blocks, int indentLevel, RenderContext context)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    context.Rtf.Append(@"\pard");
                    if (indentLevel >= 0)
                    {
                        context.Rtf.Append(@"\li").Append(Indent(indentLevel).ToString(CultureInfo.InvariantCulture));
                    }

                    AppendParagraphFormat(context);
                    RenderInlines(paragraph.Inlines, context.Rtf);
                    context.Rtf.Append("\\par\r\n");
                    break;
                case ListBlock list:
                    RenderList(list, context);
                    break;
            }
        }
    }

    private static void RenderList(ListBlock list, RenderContext context)
    {
        var rtf = context.Rtf;
        var (plan, level) = context.Placements[list];
        var indent = Indent(level).ToString(CultureInfo.InvariantCulture);
        for (var index = 0; index < list.Items.Count; index++)
        {
            rtf.Append(@"\pard\ls").Append(plan.Id.ToString(CultureInfo.InvariantCulture))
                .Append(@"\ilvl").Append(level.ToString(CultureInfo.InvariantCulture))
                .Append(@"\fi-360\li").Append(indent);
            AppendParagraphFormat(context);

            // Readers without list support show this marker instead.
            rtf.Append(@"{\listtext ")
                .Append(list.Kind == ListKind.Numbered ? string.Create(CultureInfo.InvariantCulture, $"{index + 1}.") : @"\u8226 ?")
                .Append(@"\tab}");

            var item = list.Items[index];
            var rest = item.Blocks.AsEnumerable();
            if (item.Blocks.Count > 0 && item.Blocks[0] is Paragraph first)
            {
                RenderInlines(first.Inlines, rtf);
                rest = item.Blocks.Skip(1);
            }

            rtf.Append("\\par\r\n");
            RenderBlocks(rest, level, context);
        }
    }

    private static void AppendParagraphFormat(RenderContext context) =>
        context.Rtf.Append(@"\sa0\sb0\sl240\slmult1\f0\fs").Append(context.HalfPoints.ToString(CultureInfo.InvariantCulture)).Append(' ');

    private static void RenderInlines(IEnumerable<Inline> inlines, StringBuilder rtf)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    RenderRun(run, rtf, isLink: false);
                    break;
                case LineBreak:
                    rtf.Append(@"\line ");
                    break;
                case Link link:
                    var url = LinkPolicy.Normalize(link.Url);
                    if (url is null)
                    {
                        foreach (var run in link.Runs)
                        {
                            RenderRun(run, rtf, isLink: false);
                        }

                        break;
                    }

                    rtf.Append(@"{\field{\*\fldinst{HYPERLINK """).Append(EscapeText(url).Replace("\"", "%22", StringComparison.Ordinal)).Append(@"""}}{\fldrslt{");
                    foreach (var run in link.Runs)
                    {
                        RenderRun(run, rtf, isLink: true);
                    }

                    rtf.Append("}}}");
                    break;
            }
        }
    }

    private static void RenderRun(TextRun run, StringBuilder rtf, bool isLink)
    {
        if (run.Text.Length == 0)
        {
            return;
        }

        rtf.Append('{');
        var start = rtf.Length;
        var style = run.Style | (isLink ? TextStyle.Underline : TextStyle.None);
        if (style.HasFlag(TextStyle.Bold))
        {
            rtf.Append(@"\b");
        }

        if (style.HasFlag(TextStyle.Italic))
        {
            rtf.Append(@"\i");
        }

        if (style.HasFlag(TextStyle.Underline))
        {
            rtf.Append(@"\ul");
        }

        if (style.HasFlag(TextStyle.Strikethrough))
        {
            rtf.Append(@"\strike");
        }

        if (isLink)
        {
            rtf.Append(@"\cf1");
        }

        // A space ends a control word; without a control word it would be literal text.
        if (rtf.Length > start)
        {
            rtf.Append(' ');
        }

        rtf.Append(EscapeText(run.Text)).Append('}');
    }

    /// <summary>One RTF list definition, shared by a top-level list and the lists nested in it.</summary>
    private sealed class ListPlan(int id)
    {
        public int Id { get; } = id;

        public ListKind?[] Kinds { get; } = new ListKind?[MaxLevels];
    }

    private sealed record RenderContext(StringBuilder Rtf, int HalfPoints, Dictionary<ListBlock, (ListPlan Plan, int Level)> Placements);
}
