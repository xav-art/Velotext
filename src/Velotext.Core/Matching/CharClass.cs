// Velotext: classifies characters as word characters or delimiters for trigger matching.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;

namespace Velotext.Core.Matching;

/// <summary>
/// Classifies characters for trigger matching. Letters, digits, underscores and combining marks are
/// word characters; everything else (space, Tab, Enter, punctuation, symbols) is a delimiter.
/// </summary>
public static class CharClass
{
    /// <summary>Returns true when <paramref name="c"/> belongs inside a word.</summary>
    /// <param name="c">The character.</param>
    public static bool IsWordChar(char c)
    {
        if (char.IsLetterOrDigit(c) || c == '_')
        {
            return true;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    /// <summary>Returns true when <paramref name="c"/> separates words.</summary>
    /// <param name="c">The character.</param>
    public static bool IsDelimiter(char c) => !IsWordChar(c);
}
