---
status: accepted
date: 2026-09-19
---
# A separate Game Launcher fronts Velopack; the game takes no update dependency

Releases were hand-rolled zips (~300 MiB per OS) and the client could only *tell* players a newer build
existed. That matters more than convenience here: the lobby filters listings by protocol version, so a stale
client sees an **empty server browser** with no explanation. We wanted an installer and delta auto-updates,
and Velopack delivers both — its deltas for a real release were 9 MiB (macOS) and 0.8 MiB (Windows).

Velopack's contract is the obstacle. During install, update and uninstall it runs the app's **main
executable** with `--veloapp-*` arguments and expects a silent exit within seconds (Windows kills it after
15–30 s and shows a warning). A Godot export ignores unknown arguments and boots the whole game; its C#
assembly loads at `ScriptServer::init_languages`, *after* `DisplayServer::create` and the boot splash — so
even the earliest possible C# handler flashes a window on every install, update and uninstall.

We decided that the Velopack main executable on all three OSes is a **separate, themed Avalonia app**
(`launcher/`), which installs/updates and then runs the Godot client as a **direct child process**. The
game has no Velopack reference; the two sides share only `shared/LauncherContract.cs`: an env var
(`SA_LAUNCHER`, plus what the launcher already knows in `SA_LAUNCHER_UPDATE`) and one exit code (`85` =
the player pressed UPDATE NOW in the server browser).

## Consequences we chose deliberately

- **One package, launcher + game, per OS.** Windows/Linux: the untouched export in a `game/` subfolder. macOS:
  the pristine Godot `.app` nested in the launcher bundle's `Contents/Helpers`, signed inside-out, and started
  by exec'ing its inner binary (own Dock identity *and* an exit code; `open -W` would discard the code).
  Verified: Velopack's locator, apply and pkg building all work with the nested bundle, and the seal stays
  valid through real updates.
- **The launcher stays resident, hidden, while the game runs.** Required on Linux (the AppImage mount dies with
  its main process) and it is what catches the exit code everywhere. macOS flips to the Accessory activation
  policy so only the game is in the Dock.
- **No update work while a game is alive — enforced, not hoped for.** Velopack applies a pending package on
  *every* process start by default, and a Windows apply kills every process under the install root. So
  auto-apply is off, `Main` takes the single-instance lock *before* its first non-hook Velopack call, the flow
  adopts orphaned games, and a fuzz test asserts the invariant.
- **Applies are not silent.** Silent mode refuses the elevation prompt, and an all-users `.pkg` install is
  owned by root: Velopack swaps the whole bundle, macOS will not let a normal user move a root-owned directory,
  and the updater then has to ask for admin rights. (So the recommended macOS download is the portable zip —
  dragged in by the player, owned by the player. The `.pkg` also offers *Install for me only*.)
- **The design system is linked, not copied.** `launcher/Core` compiles the game's real `DesignTokens.cs`
  through a small `Godot.Color` shim, so the two UIs cannot drift. Fonts had to become static instances
  (Avalonia ignores variable-font axes; Saira's default weight is Thin) and symbols became vector icons
  (Saira has no ◆ ● ✓ ⚠ glyphs).
- **NativeAOT on Windows and macOS, JIT on Linux.** AOT gives an instant hook exit and needs no JIT entitlement;
  on Linux there are no hooks and an AOT binary built on a current runner would raise the glibc floor above
  the game's own.

## Alternatives considered

- **In-game updater** (Velopack's SDK inside the Godot process; verified working on macOS). Zero new UI stack,
  but still needs a hook-swallowing stub on Windows, and a release that crashes at boot could never repair
  itself. Rejected by the project owner in favour of a real launcher.
- **Handle hooks in C# inside Godot** (module initializer + `TerminateProcess`): a window/splash flash per
  hook, and Godot's known shutdown segfaults risk a non-zero hook exit (= warning dialog at install).
- **A Windows-only GDExtension** exiting at core-init (GDExtensions do load before the display server):
  native code per platform for something a 20 MB launcher does anyway.
- **One macOS bundle with two executables** instead of nesting: needs Info.plist/Resources surgery on Godot's
  export and couples the layout to Godot's `mainBundle` lookup.
