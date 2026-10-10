// Velotext: describes what a phrase file adds before it is imported, so the user can check it first.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Model;
using Velotext.Core.RichText;

namespace Velotext.Core.Storage;

/// <summary>A link whose visible text names a different site than the address it opens.</summary>
/// <param name="Text">The visible text.</param>
/// <param name="Host">The site the link really opens.</param>
public sealed record MisleadingLink(string Text, string Host);

/// <summary>
/// What a phrase file adds: phrases, folders, every trigger and hotkey, the sites its links open and
/// links that look misleading. Imported triggers work at once, so the user checks this before importing.
/// </summary>
/// <param name="PhraseCount">Number of phrases.</param>
/// <param name="FolderCount">Number of folders.</param>
/// <param name="TypedTriggers">Typed-text triggers, in file order, without duplicates.</param>
/// <param name="Hotkeys">Hotkeys, in file order, without duplicates.</param>
/// <param name="LinkHosts">Sites that links open, without duplicates.</param>
/// <param name="MisleadingLinks">Links whose text names another site.</param>
public sealed record ImportPreview(
    int PhraseCount,
    int FolderCount,
    IReadOnlyList<string> TypedTriggers,
    IReadOnlyList<HotkeyGesture> Hotkeys,
    IReadOnlyList<string> LinkHosts,
    IReadOnlyList<MisleadingLink> MisleadingLinks)
{
    /// <summary>Describes the nodes that an import would add.</summary>
    /// <param name="nodes">Nodes returned by <see cref="PhraseExchange.Import"/>.</param>
    public static ImportPreview Describe(IEnumerable<LibraryNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var walker = new Walker();
        walker.Visit(nodes);
        return new ImportPreview(
            walker.Phrases,
            walker.Folders,
            [.. walker.Triggers],
            [.. walker.Hotkeys],
            [.. walker.Hosts],
            [.. walker.Misleading]);
    }

    private sealed class Walker
    {
        public int Phrases { get; private set; }

        public int Folders { get; private set; }

        public List<string> Triggers { get; } = [];

        public List<HotkeyGesture> Hotkeys { get; } = [];

        public List<string> Hosts { get; } = [];

        public List<MisleadingLink> Misleading { get; } = [];

        private HashSet<string> SeenTriggers { get; } = new(StringComparer.Ordinal);

        private HashSet<HotkeyGesture> SeenHotkeys { get; } = [];

        private HashSet<string> SeenHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Visit(IEnumerable<LibraryNode> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case Folder folder:
                        Folders++;
                        Visit(folder.Children);
                        break;
                    case Phrase phrase:
                        Phrases++;
                        VisitTriggers(phrase.Triggers);
                        VisitBlocks(phrase.Content.Blocks);
                        break;
                }
            }
        }

        private void VisitTriggers(IEnumerable<Trigger> triggers)
        {
            foreach (var trigger in triggers)
            {
                switch (trigger)
                {
                    case AutotextTrigger { Text.Length: > 0 } typed when SeenTriggers.Add(typed.Text):
                        Triggers.Add(typed.Text);
                        break;
                    case HotkeyTrigger hotkey when SeenHotkeys.Add(hotkey.Gesture):
                        Hotkeys.Add(hotkey.Gesture);
                        break;
                }
            }
        }

        private void VisitBlocks(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        foreach (var link in paragraph.Inlines.OfType<Link>())
                        {
                            VisitLink(link);
                        }

                        break;
                    case ListBlock list:
                        foreach (var item in list.Items)
                        {
                            VisitBlocks(item.Blocks);
                        }

                        break;
                }
            }
        }

        private void VisitLink(Link link)
        {
            if (LinkPolicy.HostOf(link.Url) is not { } host)
            {
                return;
            }

            if (SeenHosts.Add(host))
            {
                Hosts.Add(host);
            }

            var text = string.Concat(link.Runs.Select(r => r.Text));
            if (LinkPolicy.IsMisleading(text, link.Url))
            {
                Misleading.Add(new MisleadingLink(text, host));
            }
        }
    }
}
