// Velotext: tells whether another program owns a key combination as a global hotkey, so it never reaches apps.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Platform.Windows.Native;

namespace Velotext.Platform.Windows.Input;

/// <summary>
/// Finds out whether another program has registered a key combination as a global hotkey. Windows gives such
/// keys to that program instead of the focused app, but low-level hooks still see them. The check registers
/// the combination for a moment (it fails when someone else owns it) and releases it at once. Answers are
/// cached per combination and checked again after a minute, so a program that starts later is noticed.
/// Runs on the keyboard hook thread.
/// </summary>
internal sealed class HotkeyOwnershipCache
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const int ProbeId = 0xBFF0;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private static readonly TimeSpan s_maxAge = TimeSpan.FromMinutes(1);

    private readonly Dictionary<int, (bool Claimed, long CheckedAt)> _answers = [];
    private readonly Func<uint, uint, bool?> _probe;
    private readonly TimeProvider _time;

    /// <summary>Creates the cache with the real Windows check.</summary>
    public HotkeyOwnershipCache()
        : this(ProbeWithWindows, TimeProvider.System)
    {
    }

    /// <summary>Creates the cache with a custom check, for tests.</summary>
    /// <param name="probe">Returns true when the combination is owned elsewhere, false when free, null when unknown.</param>
    /// <param name="time">Clock for the cache age.</param>
    internal HotkeyOwnershipCache(Func<uint, uint, bool?> probe, TimeProvider time)
    {
        _probe = probe;
        _time = time;
    }

    /// <summary>True when another program owns the combination of this key with these modifiers as a hotkey.</summary>
    /// <param name="vk">Virtual key.</param>
    /// <param name="shift">Shift held.</param>
    /// <param name="ctrl">Ctrl held.</param>
    /// <param name="alt">Alt held.</param>
    public bool IsClaimed(int vk, bool shift, bool ctrl, bool alt)
    {
        var modifiers = (shift ? ModShift : 0) | (ctrl ? ModControl : 0) | (alt ? ModAlt : 0);
        var key = (int)modifiers << 16 | vk;
        var now = _time.GetTimestamp();
        if (_answers.TryGetValue(key, out var answer) && _time.GetElapsedTime(answer.CheckedAt, now) < s_maxAge)
        {
            return answer.Claimed;
        }

        // When the check cannot tell (an unexpected error), the key counts as typed text, as before.
        var claimed = _probe(modifiers, (uint)vk) ?? false;
        _answers[key] = (claimed, now);
        return claimed;
    }

    private static bool? ProbeWithWindows(uint modifiers, uint vk)
    {
        // A thread hotkey (no window): registering succeeds only when nobody owns the combination.
        if (NativeMethods.RegisterHotKey(0, ProbeId, modifiers | NativeMethods.ModNoRepeat, vk))
        {
            NativeMethods.UnregisterHotKey(0, ProbeId);
            return false;
        }

        return System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == ErrorHotkeyAlreadyRegistered ? true : null;
    }
}
