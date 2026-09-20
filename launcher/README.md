# Game Launcher

The small themed app players actually start. It installs and updates the game through
[Velopack](https://github.com/velopack/velopack), then runs the Godot client as a child process.
("Launcher" alone means a *weapon* launcher in this codebase — see `GLOSSARY.md` → *Game Launcher*.)

- **Why it exists:** Velopack runs an app's main executable with `--veloapp-*` hook arguments during
  install/update/uninstall and needs a silent exit within seconds. A Godot binary boots the whole game
  instead (its C# loads *after* the window exists). The launcher is a normal .NET `Main`, so it can.
  Decision record: [`docs/adr/0004`](../docs/adr/0004-game-launcher-fronts-velopack.md).
- **The game has no Velopack dependency.** The two talk through an environment variable and an exit code
  only — [`shared/LauncherContract.cs`](../shared/LauncherContract.cs), compiled by both sides.

```
launcher/Core   UI-free: LauncherFlow (the state machine), update/game/lobby services, parsers, settings.
                No Avalonia reference — tests/LauncherTest references ONLY this.
launcher/App    Avalonia 12 shell: the window (a pure view of LauncherFlow.View), ported design-system
                controls, platform interop. NativeAOT on Windows + macOS, self-contained JIT on Linux.
launcher/macos  Info.plist + entitlements for the macOS bundle.
```

## The one rule

**No update is downloaded or applied while a game is alive** — our child *or* an orphan left by a
launcher that died mid-match. A Velopack apply on Windows kills every process under the install root; on
macOS it renames the bundle out from under the game. `LauncherFlow` guards it, a fuzz test pins it, and it
is why `Program.Main` runs in this order — do not reorder it:

1. `--veloapp-*` hook → `VelopackApp.Build().Run()` and exit. Nothing else happens on that path.
2. Single-instance lock. A second start rings the doorbell (focus the game / show the window) and leaves
   **without touching Velopack**.
3. `VelopackApp` with **auto-apply off** (the default applies a pending package on *every* process start).
4. Headless self-test, or the UI.

## Package layout (what `scripts/package-clients.ps1` assembles, what `GameLocator` resolves)

| OS | Launcher | Game (the Godot export, untouched) |
|---|---|---|
| Windows | `current\StellarLauncher.exe` | `current\game\stellarallegiance.exe` |
| Linux | `usr/bin/StellarLauncher` (in the AppImage) | `usr/bin/game/stellarallegiance.x86_64` |
| macOS | `Stellar Allegiance.app/Contents/MacOS/StellarLauncher` | `…/Contents/Helpers/Stellar Allegiance.app` (nested, separately signed; its inner binary is exec'd directly so the exit code comes back) |

The launcher stays **resident but hidden** while the game runs (on Linux its process keeps the AppImage
mount alive; on macOS it switches to the Accessory activation policy so only the game is in the Dock).
Game exit `0` → the launcher exits · `85` (UPDATE NOW pressed in the server browser) → update, then
straight back into the game · anything else → SIGNAL LOST notice with a log-folder link.

## Run it

```sh
dotnet run --project launcher/App                                   # DEV BUILD · not installed (no update calls)
dotnet run --project launcher/App -- --launcher-game=godot-mono --path client   # …and PLAY starts the client from source
dotnet run --project launcher/App -- --launcher-showcase             # every ported component on one page
dotnet run --project launcher/App -- --launcher-fake=available        # any state, no install/feed/game needed
```

To review the UI against the local lobby instead of running from source directly: `aspire resource
launcher start` (dashboard **Start** on the `launcher` resource) opens the real flow, and `aspire
resource launcher show --view showcase` (dashboard **Show launcher view…**) opens the showcase or any
fake state — see `apphost/Hosting/GameLauncher.cs`.

Everything that is not `--launcher-*` is passed to the game verbatim (including a bare `--` and its tail).

| Flag | Purpose |
|---|---|
| `--launcher-feed=<dir\|url>` | Update feed override (a folder made by `package-clients.ps1` works). Also bypasses the 5-minute check cache. |
| `--launcher-game=<path\|command>` | Game binary override (dev). |
| `--launcher-data=<dir>` | Settings/log/lock directory (hermetic tests). Default: `%APPDATA%`, `~/Library/Application Support`, `~/.config` + `/StellarAllegiance`. |
| `--launcher-render=auto\|gpu\|software` | Renderer. Intel Macs skip Metal; a boot sentinel falls back to software if the last start never drew a frame. |
| `--launcher-no-autolaunch` | Ignore the auto-launch preference for this run. |
| `--launcher-selftest=update\|play` | **Headless** scripted run of the real flow (no Avalonia init — works on display-less CI). Emits `LAUNCHER_E2E_STATE:` markers. |
| `--launcher-showcase` / `--launcher-fake=<state>` / `--launcher-shot=<png>` | Component gallery / any state without a flow / render off-screen to a PNG and exit (the counterpart of the game's `--ui-showcase` / `--ui-shot`). States: see `Views/FakeViews.cs`. |

**No display, no window.** Avalonia cannot start on a Mac whose display is asleep (native error `-6661`) —
the relaunch after an update nobody stayed to watch, a remote session, a script. The launcher then writes
`the launcher window could not run` plus the exception to `logs/launcher.log` and exits `1`; an exception
that escapes `Main` would instead be an `abort()`, i.e. a "quit unexpectedly" dialog and an empty log.
Anything scripted should use `--launcher-selftest` or `--launcher-shot`: neither needs a display.

## Look

All colours and sizes come from the **game's own** `client/scripts/ui/DesignTokens.cs`, compiled into
`launcher/Core` through a ~100-line `Godot.Color` shim — there is nothing to keep in sync, and a Godot-only
API added to that file breaks the launcher build loudly. Controls are ports of the game's
(`ChamferButton`, `BracketPanel`, `HairlinePanel`, `ProgressSweepBar`, `StatusPill`, `AlertBox`), same
numbers, same rules. Two forced differences: fonts are **static instances** (Avalonia ignores
variable-font axes; `tools/font-instancer`), and symbols are **vector icons** (Saira has no ◆ ● ✓ ⚠ …
glyphs). See `DESIGN.md` → *Game Launcher port*.

## Test

```sh
scripts/run-tests.ps1 -Filter Launcher   # tests/LauncherTest — flow, invariant fuzz, args, parsers, guards
scripts/launcher-e2e.ps1                 # REAL install → update → restart → play on this machine (~1 min)
```

`launcher-e2e.ps1` is also what `.github/workflows/package-dryrun.yml` runs on all three OSes — on
Windows through the real `Setup.exe --silent`, which is how the install hooks are verified there.
Releasing: [`docs/RELEASING.md`](../docs/RELEASING.md).
