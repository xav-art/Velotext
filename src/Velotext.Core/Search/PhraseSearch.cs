// Velotext: matches phrases against a search query (description, triggers and content).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using Velotext.Core.Model;
using Velotext.Core.RichText;

namespace Velotext.Core.Search;

/// <summary>
/// Matches phrases against a query. Every word of the query must appear in the description, a
/// trigger or the content. Matching ignores case and accents, so "reponse" finds "Réponse".
/// </summary>
public static class PhraseSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private static readonly CompareInfo s_compare = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Splits a query into words.</summary>
    /// <param name="query">The raw query.</param>
    public static string[] Tokenize(string? query) =>
        string.IsNullOrWhiteSpace(query) ? [] : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Returns true when every token appears in the phrase.</summary>
    /// <param name="phrase">The phrase.</param>
    /// <param name="tokens">Tokens from <see cref="Tokenize"/>. An empty list matches everything.</param>
    public static bool Matches(Phrase phrase, IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0)
        {
            return true;
        }

        var haystack = string.Join(
            '\n',
            new[] { phrase.Description, PlainTextRenderer.Render(phrase.Content) }
                .Concat(phrase.Triggers.Select(t => t switch
                {
                    AutotextTrigger a => a.Text,
                    HotkeyTrigger h => h.Gesture.ToString(),
                    _ => string.Empty,
                })));

        return tokens.All(token => s_compare.IndexOf(haystack, token, Options) >= 0);
    }
}
