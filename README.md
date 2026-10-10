# Velotext

Velotext is a text expander for Windows. Type a short trigger, such as `;thx`, and Velotext replaces it with a
formatted phrase: bold text, bullets, numbered lists and links stay intact in Outlook, Word, Teams and browser
editors. It runs with normal user rights, starts with Windows and stays out of the way in the notification area.

## Features

- **Typed-text triggers** with three options for each trigger:
  - **Match case**: fires only when you type the same capital and small letters.
  - **Expand immediately**: inserts the phrase as soon as you type the last character. When off, Velotext waits
    for a space, Enter or punctuation and keeps that character after the phrase.
  - **Start of word**: fires only when the trigger starts a word, so `sig` does not fire inside `design`.
- **Hotkeys**: a global key combination per phrase. Velotext tells you when another app already uses it.
- **Formatted phrases**: a small editor for bold, italic, underline, strikethrough, lists and links. Text pasted
  from Word, Outlook or a web page keeps that formatting and drops fonts and colors, so inserted text takes
  the style of the target app.
- **Insert into the active application** (default) or **copy to the clipboard only**.
- **Your clipboard comes back**: Velotext puts your clipboard back after each insertion, and the temporary phrase
  never shows up in the Windows clipboard history. See the limits below.
- **Undo with Backspace**: press Backspace right after an expansion and your typed trigger comes back. This works for
  triggers that expand immediately and for hotkeys, and not in terminal windows.
- **Safe while you work**: if you click or switch windows while Velotext inserts a phrase, it stops and tells you,
  so a phrase never lands in the wrong place.
- **Folders, search and "Recently used"**, with drag and drop to reorder.
- **Problem checks**: duplicate triggers, triggers that can never fire because a shorter one fires first, and
  hotkeys in use elsewhere are flagged next to the trigger.
- **Pause and resume** from the tray icon, the main window or an optional hotkey.
- **Import and export** folders as `.velotext` files to share phrases with colleagues. Before an import, Velotext
  lists every trigger, hotkey and link site in the file and warns about links whose text names another site.
- Dark, light or Windows theme. Highlights use your Windows accent color, unless it is too hard to read on the background.

## Install

1. Download `Velotext-win-Setup.exe` from the [latest release](https://github.com/xav-art/Velotext/releases/latest).
2. Run it. Velotext installs for your user only, in `%LocalAppData%\Velotext`. No admin rights are needed.
3. To update, run the setup of a newer version. Your phrases and settings stay.

The installer is not code-signed yet, so Windows SmartScreen can show a warning the first time. Each release lists
the SHA-256 checksum of every file and a GitHub build attestation, so you or your IT team can check that the file
came from this repository's release workflow. Run the installer only after that check, or after your IT team
approves the file.

Requirements: Windows 10 or Windows 11, 64-bit. Tested on Windows 11 24H2.

## Use

1. Open Velotext from the Start menu or the notification area icon.
2. Select **New phrase**. Give it a name and type the text.
3. Under **Triggers**, type the trigger text, for example `;thx`.
4. Type `;thx` in any app. Velotext replaces it with the phrase.

Close the window to keep Velotext running in the notification area. To stop it, right-click the icon and select
**Exit Velotext**.

## Privacy and security

- Velotext watches the keyboard with a standard Windows low-level hook, which needs no admin rights.
- To recognize triggers, it keeps the characters typed since the last Enter, Tab, click or window change in memory,
  128 at most, and clears them when they are no longer needed. Typed text is never written to disk, never logged
  and never sent anywhere. Velotext makes no network connections.
- Text that other software types for you (for example a password manager) passes through the same check, but the
  memory is cleared at the next Enter or Tab.
- Windows does not let a normal app read keys typed into apps that run as administrator, so triggers do not fire
  there.
- Links in phrases are limited to `http`, `https`, `mailto` and `tel` addresses. The editor shows a link's address
  when you point at it. Imported files are checked and size-limited, and you see what they add before anything is added.
- Hotkeys cannot take over standard shortcuts such as Ctrl+C, Ctrl+V, Ctrl+Z or Alt+Tab, and triggers need at least
  2 characters with a letter or a digit, so a shared file cannot hijack normal typing.

### Clipboard limits

Velotext saves the clipboard formats that are plain memory: text, formatted text, images as bitmaps (DIB) and most
app formats. It does not save:

- formats that Windows keeps as handles (for example a metafile or a bitmap without a DIB copy),
- data that stays inside the source app (for example files copied from an Outlook attachment), and
- any single format larger than 32 MB, or anything beyond 64 MB in total.

If an app does not confirm that it read the phrase, the phrase stays on the clipboard until the app reads it, you
switch to another window, or 10 seconds pass. Then your clipboard comes back.

## Your data

| What | Where |
|---|---|
| Phrases and settings | `%APPDATA%\Velotext` (`library.json`, `settings.json`) |
| Automatic backups of your phrases | `%APPDATA%\Velotext\backups` (the 20 most recent) |
| How often and when you last used each phrase (for "Recently used") | `%APPDATA%\Velotext\usage.json` |
| Logs (no typed text, no phrase content) | `%LOCALAPPDATA%\Velotext\logs` (the last 7 daily files) |
| Installer log (no typed text, no phrase content) | `%LOCALAPPDATA%\Velotext\velopack.log` |

Every save is atomic, so a crash or power loss never leaves a half-written file. If the phrase file is ever
damaged, Velotext loads the most recent backup and tells you. **Settings** has buttons to open these folders and
to restore a backup.

## Build from source

Requirements: Windows 10 or 11. The script installs the .NET SDK into the repository folder; no admin rights are
needed.

```powershell
git clone https://github.com/xav-art/Velotext.git
cd Velotext
.\build.ps1 -InstallSdk        # restore, build, test, check coverage
.\build.ps1 -Publish -Version 1.0.0   # also build the installer into artifacts\releases
```

Tests that use the real clipboard, hotkeys and keyboard hook run only with `.\build.ps1 -DesktopTests`.

### Project layout

| Project | Purpose |
|---|---|
| `src/Velotext.Core` | Phrase model, rich text renderers (HTML, RTF, plain text), trigger matching, validation, storage |
| `src/Velotext.Platform.Windows` | Keyboard hook, input injection, clipboard, hotkeys, autostart, the expansion engine |
| `src/Velotext.App` | WPF user interface, tray icon, settings |
| `tests/*` | Unit, integration, UI rendering and repository convention tests |

## License

Velotext is free software under the [MIT License](LICENSE).
