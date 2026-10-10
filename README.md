# Velotext

Velotext is a text expander for Windows. Type a short trigger, such as `DEUX`, and Velotext replaces it with a
formatted phrase: bold text, bullets, numbered lists and links stay intact in Outlook, Word, Teams and browser
editors. It runs with normal user rights, starts with Windows and stays out of the way in the notification area.

## Features

- **Typed-text triggers** with three options for each trigger:
  - **Match case**: fires only when you type the same capital and small letters.
  - **Expand immediately**: inserts the phrase as soon as you type the last character. When off, Velotext waits
    for a space, Enter or punctuation and keeps that character after the phrase.
  - **Start of word**: fires only when the trigger starts a word, so `DEUX` does not fire inside `ADEUX`.
- **Hotkeys**: a global key combination per phrase. Velotext tells you when another app already uses it.
- **Formatted phrases**: a small editor for bold, italic, underline, strikethrough, lists and links. Text pasted
  from Word, Outlook or a web page keeps that formatting and drops fonts and colours, so inserted text takes
  the style of the target app.
- **Insert into the active application** (default) or **copy to the clipboard only**.
- **Your clipboard is safe**: Velotext puts your clipboard back after each insertion, and the temporary phrase never
  shows up in the Windows clipboard history.
- **Undo with Backspace**: press Backspace right after an insertion and your typed trigger comes back.
- **Folders, search and "Recently used"**, with drag and drop to reorder.
- **Problem checks**: duplicate triggers, triggers that can never fire because a shorter one fires first, and
  hotkeys in use elsewhere are flagged next to the trigger.
- **Pause and resume** from the tray icon, the main window or an optional hotkey.
- **Import and export** folders as `.velotext` files to share phrases with colleagues.
- Dark, light or Windows theme. Your Windows accent colour is used for highlights.

## Install

1. Download `Velotext-win-Setup.exe` from the [latest release](https://github.com/xav-art/Velotext/releases/latest).
2. Run it. Velotext installs for your user only, in `%LocalAppData%\Velotext`. No admin rights are needed.
3. To update, run the setup of a newer version. Your phrases and settings stay.

The installer is not code-signed yet, so Windows SmartScreen can show a warning the first time. Select
**More info**, then **Run anyway**, or ask your IT team to approve the file.

Requirements: Windows 10 (version 2004 or later) or Windows 11, 64-bit.

## Use

1. Open Velotext from the Start menu or the notification area icon.
2. Select **New phrase**. Give it a name and type the text.
3. Under **Triggers**, type the trigger text, for example `DEUX`.
4. Type `DEUX` in any app. Velotext replaces it with the phrase.

Close the window to keep Velotext running in the notification area. To stop it, right-click the icon and select
**Exit Velotext**.

## Privacy and security

- Velotext watches the keyboard with a standard Windows low-level hook, which needs no admin rights.
- It keeps only the last few typed characters in memory, to recognise triggers. Typed text is never written to
  disk, never logged and never sent anywhere. Velotext makes no network connections.
- Windows does not let a normal app read keys typed into apps that run as administrator, so triggers do not fire
  there.
- Links in phrases are limited to `http`, `https`, `mailto` and `tel` addresses. Imported files are checked and
  size-limited before anything is added.

## Your data

| What | Where |
|---|---|
| Phrases, settings, recently used | `%APPDATA%\Velotext` (`library.json`, `settings.json`, `usage.json`) |
| Automatic backups of your phrases | `%APPDATA%\Velotext\backups` (the 20 most recent) |
| Logs (no typed text, no phrase content) | `%LOCALAPPDATA%\Velotext\logs` (7 days) |

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

## Licence

Velotext is free software under the [MIT License](LICENSE).
