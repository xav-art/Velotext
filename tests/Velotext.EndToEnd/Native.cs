// Velotext: Win32 helpers for the end-to-end runner (typing, focus, clipboard, input guard hooks).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;

namespace Velotext.EndToEnd;

/// <summary>Win32 functions used by the end-to-end runner.</summary>
internal static unsafe partial class Native
{
    public const uint InputKeyboard = 1;
    public const uint KeyUp = 0x0002;
    public const uint ExtendedKey = 0x0001;
    public const int WhKeyboardLl = 13;
    public const int WhMouseLl = 14;
    public const uint LlkhfInjected = 0x10;
    public const uint LlmhfInjected = 0x01;
    public const uint CfUnicodeText = 13;

    public delegate nint HookProc(int code, nint wParam, nint lParam);

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, InputRecord* inputs, int size);

    [LibraryImport("user32.dll")]
    public static partial short VkKeyScanExW(ushort ch, nint layout);

    [LibraryImport("user32.dll")]
    public static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyW(uint code, uint mapType);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextW(nint hwnd, [Out] char[] text, int max);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowsHookExW(int idHook, nint proc, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    public static partial nint SetClipboardData(uint format, nint data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint mem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint mem);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(nint mem);

    /// <summary>Returns the title of a window.</summary>
    public static string WindowTitle(nint hwnd)
    {
        var buffer = new char[512];
        var length = GetWindowTextW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    /// <summary>Finds a visible top-level window whose title contains <paramref name="titlePart"/>.</summary>
    public static nint FindWindow(string titlePart, uint? processId = null)
    {
        nint found = 0;
        EnumWindows(
            (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                if (processId is not null)
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid != processId)
                    {
                        return true;
                    }
                }

                if (WindowTitle(hwnd).Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            },
            0);
        return found;
    }

    /// <summary>Brings a window to the foreground, attaching to the current foreground thread if needed.</summary>
    public static bool ForceForeground(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        var current = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != current && AttachThreadInput(current, foregroundThread, true);
        try
        {
            ShowWindow(hwnd, 9);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(current, foregroundThread, false);
            }
        }

        Thread.Sleep(300);
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>Reads clipboard text in a format (UTF-16 for text, UTF-8 for HTML).</summary>
    public static string? ReadClipboard(uint format, bool utf16)
    {
        for (var attempt = 0; attempt < 40 && !OpenClipboard(0); attempt++)
        {
            Thread.Sleep(25);
        }

        try
        {
            var handle = GetClipboardData(format);
            if (handle == 0)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            try
            {
                var size = (int)GlobalSize(handle);
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return (utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes)).TrimEnd('\0');
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Puts plain text on the clipboard.</summary>
    public static void WriteClipboardText(string text)
    {
        for (var attempt = 0; attempt < 40 && !OpenClipboard(0); attempt++)
        {
            Thread.Sleep(25);
        }

        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var handle = GlobalAlloc(0x0002, (nuint)bytes.Length);
            var pointer = GlobalLock(handle);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            GlobalUnlock(handle);
            SetClipboardData(CfUnicodeText, handle);
        }
        finally
        {
            CloseClipboard();
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort Vk;
    public ushort Scan;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int X;
    public int Y;
    public uint Data;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)]
    public MouseInput Mouse;

    [FieldOffset(0)]
    public KeyboardInput Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct InputRecord
{
    public uint Type;
    public InputUnion Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public nint Hwnd;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int X;
    public int Y;
    public uint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardHookData
{
    public uint Vk;
    public uint Scan;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseHookData
{
    public int X;
    public int Y;
    public uint Data;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}
