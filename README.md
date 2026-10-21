# spidey

A spider-hero who visits when Claude Code needs you **and you're in a
different window**.

| Visit | When | What you see |
|---|---|---|
| **Permission** | Claude asks to run something | Drops from the top on a web line, white bubble with the actual command |
| **Spider-sense** | …and the command is risky (`rm -rf`, `git push --force`, `DROP TABLE`, `curl … \| sh`, editing `.env` …) | Same, plus a tingle around his head, a shake and a red "Careful!" bubble. Shows even if you're looking at Claude |
| **Question** | Claude asks you a question | Drops from the top |
| **Done** | A turn that took 2+ minutes finishes | Jumps up from the taskbar, lands in a crouch, green bubble with Claude's last line |

- Switch back to Claude's window → he leaves.
- Click him → jumps you to Claude's window.
- A new visit replaces the current one.

Windows only (Windows PowerShell + WinForms; nothing to install besides Node).

## Install

```
/plugin marketplace add champ18ion/spidey
/plugin install spidey@spidey
```

Private repo: the machine needs git access to `champ18ion/spidey`
(e.g. `gh auth login`). Then `/reload-plugins` or restart Claude Code.

## Try it

```bash
node scripts/launch.js --test wait    # or alert / done
```

`--test` shows him even if Claude is focused, and he leaves after 15s.
Set `CLAUDE_SPIDEY_ALWAYS=1` to make the real hooks ignore focus too.

## Settings

Defaults live in `sprites/config.json`. Override any of them per machine in
`%USERPROFILE%\.claude\spidey\config.json` (all optional):

```json
{ "suit": "Default", "zoom": 2, "fps": 12, "sound": true, "volume": 0.6,
  "spiderSense": true, "doneAfterSeconds": 120 }
```

| key | meaning | default |
|---|---|---|
| `suit` | `"Default"`, `"Black"`, `"Iron Spidey"` (any sprite subfolder), or `"random"` | `"Default"` |
| `zoom` | whole-number sprite scale (1 = native size, sharpest) | auto from screen DPI |
| `fps` | sprite animation speed | `12` |
| `sound` / `volume` | thwip / tingle / done sounds, volume 0–1 | `true` / `0.6` |
| `spiderSense` | warn about risky commands | `true` |
| `doneAfterSeconds` | minimum turn length for a "done" visit; `0` turns it off | `120` |

## Custom sprites

Bundled suits live in `sprites/` (Default, Black, Iron Spidey). To use other
frames on one machine, put them in `%USERPROFILE%\.claude\spidey\` (or point
`CLAUDE_SPIDEY_SPRITES` at another folder); if that folder has sprites it wins
over the bundled ones. Without any sprites he's a built-in pixel spider.
Several costumes can live in subfolders. File names follow the Marvel Cosmic
Invasion sprite rip:

| set | files | required |
|---|---|---|
| hanging (head down, web at the top) | `Selectscreenstart_*.png` once, then `Selectscreen_*.png` looped | yes |
| spider-sense effect | `Spidersense_*.png` (drawn around his head) | no |
| done | `Jump.png`, `Jumpapex_*.png`, `Jumpfall.png`, `Land_*.png`, `Idle_*.png` looped | no |

Frames are auto-cropped and scaled up with nearest-neighbour. Packs can also
bring their own `thwip.wav`, `tingle.wav` and `done.wav`.

The bundled sprites are third-party art: keep this repo private.

## Several VS Code windows

Every VS Code window belongs to one `Code.exe` process, so spidey tells them
apart by title: a window only counts as "Claude's" if its title contains the
project folder name (VS Code's default title does). The bubble shows
`[project]` so you know which Claude is asking, and clicking him focuses that
window. Same for Cursor / Windsurf / VSCodium.

Limit: two windows on the same folder name look identical.

## How it works

`hooks/hooks.json` runs `scripts/launch.js` on four hooks:

- `PermissionRequest` saves the pending tool call to `%TEMP%` (so the bubble can show the command)
- `Notification` (`permission_prompt`, `elicitation_dialog`) starts a visit
- `UserPromptSubmit` / `Stop` time the turn for the "done" visit

A visit starts a detached keeper that runs `scripts/spidey.ps1`, which records
the hook's process ancestry (claude → terminal / VS Code) and compiles
`scripts/Spidey.cs` once into `%TEMP%\claude-spidey-<hash>.dll`. The overlay is
a topmost window that is click-through except for him, and polls the
foreground window; if it belongs to that ancestry, you're back.

Default sounds are synthesized by `scripts/make-sounds.js` into `assets/`.
Errors are logged to `%TEMP%\claude-spidey.log`.
