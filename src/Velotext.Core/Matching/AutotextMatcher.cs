// Velotext: recognises autotext triggers in the stream of typed characters.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.Core.Matching;

/// <summary>A trigger recognised in the typed text.</summary>
/// <param name="Entry">The matched trigger and its phrase.</param>
/// <param name="TypedText">The trigger text exactly as the user typed it (case included).</param>
/// <param name="CharactersToDelete">
/// How many already-delivered characters must be erased before inserting the phrase. For immediate
/// triggers the last character is held back, so this is one less than the trigger length.
/// </param>
/// <param name="Delimiter">For delimiter triggers, the delimiter that completed the match; it was held back and must be re-sent after the phrase.</param>
public sealed record AutotextMatch(AutotextEntry Entry, string TypedText, int CharactersToDelete, char? Delimiter);

/// <summary>
/// Recognises autotext triggers in typed characters. It keeps only a short rolling buffer of the most
/// recent characters, in memory, so typed text is never retained beyond what matching needs.
/// Not thread-safe: use it from the keyboard thread only. The <see cref="Index"/> can be swapped from
/// any thread.
/// </summary>
public sealed class AutotextMatcher
{
    /// <summary>Default number of characters kept. Twice the longest allowed trigger.</summary>
    public const int DefaultCapacity = 128;

    private readonly char[] _buffer;
    private int _length;
    private bool _startIsBoundary = true;
    private int _seedLength = -1;
    private volatile TriggerIndex _index = TriggerIndex.Empty;

    /// <summary>Creates a matcher.</summary>
    /// <param name="capacity">Characters to keep; at least twice the longest trigger.</param>
    public AutotextMatcher(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2 * Model.AutotextTrigger.MaxLength);
        _buffer = new char[capacity];
    }

    /// <summary>The triggers to look for. Safe to replace from any thread.</summary>
    public TriggerIndex Index
    {
        get => _index;
        set => _index = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Current buffer content. For diagnostics in tests only; never log it.</summary>
    internal string BufferText => new(_buffer, 0, _length);

    /// <summary>Feeds one typed character and returns a match when a trigger completes.</summary>
    /// <param name="c">The character the key produced.</param>
    public AutotextMatch? OnCharacter(char c)
    {
        var index = _index;

        // A delimiter completes delimiter triggers that end right before it. Right after an undo the
        // restored trigger must not fire again, so the seeded position is skipped once.
        if (CharClass.IsDelimiter(c) && _seedLength != _length)
        {
            var delimited = FindMatch(index, immediate: false);
            if (delimited is not null)
            {
                return delimited with { Delimiter = c };
            }
        }

        // Enter and Tab usually end a field or a line (a user name, a password). Forget everything typed
        // before them, so the buffer never holds more than the current field.
        if (c is '\r' or '\n' or '\t')
        {
            Reset();
            return null;
        }

        Append(c);
        _seedLength = -1;
        return FindMatch(index, immediate: true);
    }

    /// <summary>
    /// Adds a typed character without looking for a match, so the buffer keeps up with the text in the
    /// application. Used for keys that produce several characters at once.
    /// </summary>
    /// <param name="c">The character.</param>
    public void Feed(char c)
    {
        if (c is '\r' or '\n' or '\t')
        {
            Reset();
            return;
        }

        Append(c);
        _seedLength = -1;
    }

    /// <summary>Removes the last character, as Backspace does in the target application.</summary>
    public void OnBackspace()
    {
        if (_length > 0)
        {
            _buffer[--_length] = '\0';
        }
        else
        {
            // We no longer know what precedes the caret.
            _startIsBoundary = true;
        }

        if (_seedLength > _length)
        {
            _seedLength = -1;
        }
    }

    /// <summary>Forgets all typed text, for example after a click, focus change or navigation key.</summary>
    public void Reset()
    {
        Array.Clear(_buffer, 0, _length);
        _length = 0;
        _startIsBoundary = true;
        _seedLength = -1;
    }

    /// <summary>
    /// Restores the buffer to <paramref name="text"/> after an undo, so typing continues naturally
    /// but the restored trigger does not fire again on the next delimiter.
    /// </summary>
    /// <param name="text">The trigger text that was typed back.</param>
    public void Seed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Reset();
        foreach (var c in text)
        {
            Append(c);
        }

        _seedLength = _length;
    }

    private void Append(char c)
    {
        if (_length == _buffer.Length)
        {
            var keep = _buffer.Length / 2;
            var newStart = _length - keep;
            _startIsBoundary = CharClass.IsDelimiter(_buffer[newStart - 1]);
            Array.Copy(_buffer, newStart, _buffer, 0, keep);
            Array.Clear(_buffer, keep, _buffer.Length - keep);
            _length = keep;
        }

        _buffer[_length++] = c;
    }

    private AutotextMatch? FindMatch(TriggerIndex index, bool immediate)
    {
        if (_length == 0)
        {
            return null;
        }

        foreach (var entry in index.Candidates(_buffer[_length - 1], immediate))
        {
            var trigger = entry.Trigger;
            var text = trigger.Text;
            if (text.Length > _length)
            {
                continue;
            }

            var start = _length - text.Length;
            if (!Matches(text, start, trigger.CaseSensitive) || !WordStartSatisfied(text, start, trigger.WordStart))
            {
                continue;
            }

            var typed = new string(_buffer, start, text.Length);
            return new AutotextMatch(entry, typed, immediate ? text.Length - 1 : text.Length, Delimiter: null);
        }

        return null;
    }

    private bool Matches(string text, int start, bool caseSensitive)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var typed = _buffer[start + i];
            var expected = text[i];
            if (typed != expected && (caseSensitive || char.ToUpperInvariant(typed) != char.ToUpperInvariant(expected)))
            {
                return false;
            }
        }

        return true;
    }

    private bool WordStartSatisfied(string text, int start, bool wordStart)
    {
        // A trigger that itself starts with a delimiter (for example ";sig") is already its own boundary.
        if (!wordStart || CharClass.IsDelimiter(text[0]))
        {
            return true;
        }

        return start == 0 ? _startIsBoundary : CharClass.IsDelimiter(_buffer[start - 1]);
    }
}
