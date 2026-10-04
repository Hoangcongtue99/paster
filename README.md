# Paster

A `Win+Alt+V` popup of **predefined** text. Press a digit, and it pastes into
whatever you were typing in.

Unlike Windows clipboard history, nothing you copy ever appears here. The list
is exactly what you put in it, in the order you put it in, and it doesn't move
around as you work.

Everything lives in **one text file** next to the exe. Copy that file — or the
whole folder — to another machine and you're done.

## Using it

- `Win+Alt+V` — open the popup
- `1`–`9`, `0` — paste the 1st–10th entry (top row or numpad)
- `↑`/`↓` + `Enter` — reach entries past the tenth
- `Esc`, or switching to another app — dismiss
- `Win+Alt+V` again — toggle it shut

There's no main window; it lives in the tray. Right-click the tray icon for
**Manage snippets…**, or double-click it.

## The snippets file

```
#!hotkey Win+Alt+V

=== Email signature
Best regards,
Tue

=== Standup update
Yesterday: {{cursor}}
Today:
Blockers: none

=== Review request
Hi {{select:NAME}}, could you take a look at this?
```

Each entry starts with a `=== Name` line; the body runs to the next `===` line,
blank lines included. Order in the file is order in the popup.

### Placeholders

| Marker | Effect |
|---|---|
| `{{cursor}}` | The caret is left at this spot after pasting. |
| `{{select:TEXT}}` | `TEXT` is pasted and left selected, ready to type over. |

The **first** marker in a body decides where the caret lands. Later ones are
still cleaned up — a second `{{cursor}}` disappears, a second `{{select:X}}`
leaves `X` behind — so no markup ever leaks into the pasted text.

### Changing the hotkey

Edit the `#!hotkey` line: any combination of `Ctrl`, `Alt`, `Shift`, `Win` plus
a key, e.g. `#!hotkey Ctrl+Alt+V`. The file is re-read the moment you save it,
and the app rebinds without a restart.

The hotkey lives in the snippets file on purpose — otherwise "one file you can
copy anywhere" would quietly have become two.

> **Why `Win+Alt+V` and not `Win+Shift+V`?** PowerToys' *Advanced Paste*
> registers `Win+Shift+V` by default, and whoever claims a combination first
> wins it. If you don't use Advanced Paste, clear its shortcut in PowerToys and
> put `#!hotkey Win+Shift+V` back here.

## Installing

Download `Paster.exe` from the [releases page](../../releases), put it in a
folder you can write to, and run it. It creates `snippets.txt` beside itself on
first run. No installer, no .NET to install, nothing in the registry unless you
tick **Start with Windows**.

Two things to expect:

- **SmartScreen will warn you.** The exe isn't code-signed, so the first run
  gives *"Windows protected your PC"* → **More info** → **Run anyway**. If you
  downloaded a zip, right-click it → **Properties** → **Unblock** before
  extracting.
- **Nothing visible happens on launch.** It's a tray app. Look for the icon.

Put it somewhere writable — `Desktop`, `Documents`, a USB stick. Dropped into
`Program Files` it silently falls back to `%APPDATA%\Paster\`, which breaks the
"one folder, copy it anywhere" idea.

**Start with Windows** writes `HKCU\…\CurrentVersion\Run` — not a Startup-folder
shortcut — and records the exe's absolute path. It doesn't travel with the
folder, so tick it again on each machine, and re-tick it if you move the folder.

Requires 64-bit Windows 10 1607 or newer. Runs on ARM64 Windows under emulation.

## Why the popup never takes focus

The popup is a non-activating window (`WS_EX_NOACTIVATE`), and the keys it
responds to arrive as temporarily registered global hotkeys rather than through
normal keyboard focus. The app you were typing in keeps focus the whole time and
never notices the popup opened.

This isn't fussiness. An Electron app — VS Code, Slack, Discord — is a single
window as far as Windows is concerned, and which widget inside it holds focus is
Chromium's business. Deactivate that window and reactivate it, and Chromium
blurs whatever was focused without putting it back. Restoring focus at the Win32
level is provably correct and still leaves you pasting into nothing. The only
fix is never to take focus away.

Two consequences:

- **There's no type-to-filter.** Reading raw virtual keys would mean translating
  them back to characters across keyboard layouts, which is its own bug farm.
  Digits, arrows, `Enter` and `Esc` only.
- **`RegisterHotKey`, not a keyboard hook.** A low-level hook would be tidier
  internally, but it's also the API keyloggers use, and some antivirus
  heuristics treat it that way. `RegisterHotKey` is the same ordinary call used
  for the trigger itself. The cost is that it matches modifiers exactly, so each
  picker key is claimed once per modifier combination you might still be
  holding — every subset of the trigger's own modifiers.

## How pasting works, and its limits

Text goes onto the clipboard and `Ctrl+V` is sent to the window that had focus.
Typing it out key by key would be slower, would mangle non-ASCII, and would set
off editor autocomplete.

Consequences worth knowing:

- **Your clipboard is borrowed.** The previous contents are captured and put
  back ~0.4 s later. Restore is best-effort: text and images come back
  reliably, exotic formats may not.
- **Caret moves are keystrokes.** `{{cursor}}` works by sending that many `Left`
  presses after the paste. In an editor that auto-indents or pops a completion
  list on paste, the count can drift. Plain text fields, chat boxes and browser
  forms are reliable.
- **It waits for you to let go.** Sending `Ctrl+V` while `Win` or `Alt` is still
  held produces a different combination entirely, so it waits briefly for the
  modifiers to clear.
- **Slow apps may need more settle time.** The delays are constants at the top
  of [Interop/PasteEngine.cs](Interop/PasteEngine.cs).

## Building

Needs the .NET 8 SDK.

```powershell
dotnet build          # debug build
dotnet run            # run from source
./publish.cmd         # self-contained dist\Paster.exe
```

`publish.cmd` produces one self-contained exe (~63 MB compressed, no runtime
needed on the target machine). For a ~2 MB exe that requires the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0):

```powershell
dotnet publish -c Release --self-contained false `
  -p:PublishSingleFile=true -o dist
```

WPF can't be trimmed, so there's no middle ground on size.

## Layout

| Path | Role |
|---|---|
| [Core/SnippetFile.cs](Core/SnippetFile.cs) | Parses and writes `snippets.txt`; decides where it lives |
| [Core/Placeholders.cs](Core/Placeholders.cs) | Resolves `{{cursor}}` / `{{select:…}}` into text + caret moves |
| [Core/SnippetStore.cs](Core/SnippetStore.cs) | Shared in-memory list; watches the file for hand-edits |
| [Interop/HotkeyService.cs](Interop/HotkeyService.cs) | The trigger, and the picker keys claimed while the popup is open |
| [Interop/PasteEngine.cs](Interop/PasteEngine.cs) | Clipboard borrow, `Ctrl+V`, caret moves and selection |
| [UI/OverlayWindow.xaml](UI/OverlayWindow.xaml) | The popup, non-activating by design |
| [UI/ManagerWindow.xaml](UI/ManagerWindow.xaml) | Snippet editor |
| [App.xaml.cs](App.xaml.cs) | Tray icon, single instance, autostart, wiring |
| [tools/icongen](tools/icongen) | Regenerates `paster.ico` |

## Licence

MIT — see [LICENSE](LICENSE).
