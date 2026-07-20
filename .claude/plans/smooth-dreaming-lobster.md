# Migrate all repo shell scripts from bash (.sh) to cross-platform PowerShell (.ps1)

## Context

All 9 tooling/dev scripts are bash, which works on macOS/Linux but not natively on Windows (the VS Code tasks currently require Git Bash on PATH). pwsh 7+ runs on all three platforms, so every `.sh` is rewritten as an idiomatic `.ps1` and the `.sh` files deleted. GitHub workflows never invoke these scripts (they re-implement the logic inline and only mention scripts in comments), so workflows are untouched except for comment path updates.

**User decision:** scripts expose idiomatic PowerShell switches (`-Local`, `-Force`, `-WriteMovie <path>`), NOT the old `--flags`. All docs/skills/tasks examples update accordingly. Unknown/pass-through args (`--host`, `--secret`, `--autostart`, Godot flags) still flow through via `ValueFromRemainingArguments`.

## Shared conventions (every .ps1)

- Line 1: `#!/usr/bin/env pwsh`; line 2: `#Requires -Version 7.3`. Preserve each script's explanatory header comments (they encode hard-won gotchas — port them verbatim, adjusting flag examples).
- `set -euo pipefail` analog: `$ErrorActionPreference = 'Stop'` + `$PSNativeCommandUseErrorActionPreference = $true`. Wrap native commands *expected* to fail (`lsof` no-match, `codesign --remove-signature`, `pkill`, `railway list` when logged out, `dotnet build-server shutdown`) by temporarily disabling `$PSNativeCommandUseErrorActionPreference` or `2>$null` + ignore, mirroring bash `|| true`.
- Repo root: `$RepoRoot = Split-Path $PSScriptRoot -Parent` (scripts/), `Split-Path (Split-Path $PSScriptRoot -Parent) -Parent` for tools/*/. Use `/` separators (pwsh handles them on Windows).
- Args: `param(...)` blocks with `[switch]`/typed params + `[Parameter(ValueFromRemainingArguments)][string[]]$RestArgs` for pass-through.
- bash `exec` → `& $exe @args; exit $LASTEXITCODE` as the final statement.
- Same base names (`run-server.ps1`, etc.). `git rm` each `.sh` in the same commit; after `git add`, run `git update-index --chmod=+x <file.ps1>` so direct `./scripts/run-server.ps1` execution works on mac/linux.
- Platform branches via `$IsWindows` / `$IsMacOS` / `$IsLinux`.

## Phase 1 — shared helper: `scripts/godot-bin.ps1` (replaces godot-bin.sh)

Dot-sourced library defining `Resolve-Godot` that **returns the executable path as a string, or `$null`** (instead of mutating `$GODOT`). Callers: `$Godot = Resolve-Godot; if (-not $Godot) { exit 1 }`. Resolution order mirrors bash (`scripts/godot-bin.sh:15-45`):
1. `$env:GODOT` if non-empty — accept if `Get-Command` resolves it or `Test-Path -PathType Leaf`; else warn and fall through. (tasks.json keeps setting `GODOT` from `${config:godot.executablePath}` — unchanged wiring.)
2. `godot-mono` / `godot4` / `godot` via `Get-Command -ErrorAction SilentlyContinue`.
3. Standard install locations guarded by platform:
   - macOS: `/Applications/Godot_mono.app/Contents/MacOS/Godot`, `/Applications/Godot.app/...`, `$HOME/Applications/Godot_mono.app/...`
   - Windows: glob `"$env:ProgramFiles\Godot\*mono*\Godot*.exe"`, `"$env:ProgramFiles\Godot_mono\Godot*.exe"`, `"$HOME\scoop\apps\godot-mono\current\Godot*.exe"`, `"$HOME\scoop\apps\godot\current\Godot*.exe"` (bash's `/c/Program Files/...` msys paths become `$env:ProgramFiles`).
4. Nothing → print the same 2 guidance lines to stderr, return `$null`.

## Phase 2 — per-script translations

**scripts/run-server.ps1** — `param([switch]$Local, [Parameter(ValueFromRemainingArguments)][string[]]$ServerArgs)`
- `$SimPort = if ($env:SIM_PORT) { [int]$env:SIM_PORT } else { 8090 }`.
- `-Local`: `$env:SIM_PUBLIC_NAME = ''` (on Windows this removes the var — verified equivalent: `LobbyRegistrar.cs` treats null/empty identically). Else default `$env:PUBLIC_LOBBY` to the public URL and `SIM_PUBLIC_NAME` to `[System.Net.Dns]::GetHostName()` truncated to 50 chars.
- Cross-platform stale-port kill (replaces `lsof`+`kill`, `run-server.sh:49-57`): helper `Get-PortListenerPids` — Windows: `(Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue).OwningProcess | Select-Object -Unique`; unix: `lsof -tnP -iTCP:$p -sTCP:LISTEN` (non-fatal). `Stop-Process -Force` each, then poll up to 10×500ms until free.
- `dotnet build server/SimServer.csproj -c Release`, then `Set-Location $RepoRoot; & dotnet run --project server -c Release --no-build -- --port $SimPort @ServerArgs; exit $LASTEXITCODE`.

**scripts/run-client.ps1** — `param([switch]$Local, [string]$WriteMovie, [Parameter(ValueFromRemainingArguments)][string[]]$GodotArgs)`
- `-WriteMovie` now takes a **required** path (`-WriteMovie recording.avi`) — the bash optional-value form doesn't map to param(); docs note the change.
- Capture `$CallerDir = (Get-Location).Path` up front (replaces `$OLDPWD` anchoring for relative movie paths; use `[System.IO.Path]::IsPathRooted`).
- `-Local`: prepend `--host "localhost:$SimPort"` to `$GodotArgs`; else default `$env:PUBLIC_LOBBY`.
- Dot-source godot-bin.ps1; `dotnet build client/stellarallegiance.csproj -c Debug`.
- Movie mode (`run-client.sh:63-87`): `Test-HasArg` helper scans `$GodotArgs` for `--fixed-fps`/`--resolution` (also `flag=*` form); inject `--fixed-fps $env:MOVIE_FPS` (default 30) and `--resolution $env:MOVIE_RESOLUTION` (only if set) when absent; prepend `--write-movie $path`.
- Final: `& $Godot --path client/ @GodotArgs; exit $LASTEXITCODE`.

**scripts/deploy-railway-lobby.ps1 / deploy-railway-server.ps1** (twins; ~6 lines differ) — `param([string]$Project = 'wivuu-public-lobby' | 'wivuu-game-server')`
- Guard: `Get-Command railway -ErrorAction SilentlyContinue` or exit with the install message.
- **Drop the inline python3 entirely**: `railway list --json 2>$null | ConvertFrom-Json` (non-fatal wrap), then a small recursive `Find-ProjectId` walking PSCustomObjects/arrays for `.name -eq $Project` with an `id` — same semantics as the python `walk()`. Removes the python3 dependency.
- `${STUN_URL:+...}` conditional args → build a `$vars` array, append `"STUN_URL=$($env:STUN_URL)"` only if set, splat into `railway variable set` / `railway add`.
- Trailing heredoc → literal here-string `@'...'@` via `Write-Host`.

**tools/godot-import.ps1** — `param([switch]$Force)`
- Needs-import check (`godot-import.sh:24-31`): `Get-ChildItem "$Client/assets" -Recurse -Filter *.glb | Where-Object { -not (Test-Path "$($_.FullName).import") } | Select-Object -First 1`.
- **Preserve exactly:** exit 0 quietly when `Resolve-Godot` fails (the folderOpen auto-run task must not error on Godot-less machines).
- `& $Godot --headless --import --path $Client`.

**scripts/export-clients.ps1** — no params
- Invoke `& "$RepoRoot/tools/godot-import.ps1"`; guard `Test-Path "$Client/assets/bases/base.glb.import"`.
- `$env:MSBUILDDISABLENODEREUSE='1'; $env:DOTNET_CLI_USE_MSBUILD_SERVER='0'`; kill stale compilers cross-platform: `Get-Process VBCSCompiler,MSBuild -ErrorAction SilentlyContinue | Stop-Process -Force`, plus `pkill -9 -f MSBuild.dll` under `if (-not $IsWindows)` (dotnet MSBuild.dll workers); `dotnet build-server shutdown` non-fatal.
- macOS branch under `if ($IsMacOS)`: keep native `codesign` / `ditto` calls verbatim (`export-clients.sh:53-69`) — `Compress-Archive` would destroy .app symlinks/signature, `ditto` is mandatory there.
- Windows zip: `Compress-Archive "$Out/win/*"` is fine. Linux zip: executable bit matters — on non-Windows keep native `chmod +x` + `zip -rq`; on Windows fall back to `Compress-Archive` and print a note that testers must `chmod +x` (matches current reality: bash version required zip/chmod anyway).

**tools/asteroid-gen/build.ps1** — `param([Parameter(ValueFromRemainingArguments)][string[]]$GenArgs)`
- `Set-Location $PSScriptRoot`; `$Image = $env:IMAGE ?? 'asteroid-gen'`; `$Out = $env:OUT ?? (Join-Path $PSScriptRoot 'build')`; `Remove-Item -Recurse -Force`; `docker build`; `New-Item -ItemType Directory -Force`; `docker run --rm -v "${Out}:/out" $Image @GenArgs --out /out` (args present) else bare run. Note: `"${Out}:/out"` — braces required before `:` in pwsh interpolation.

**tools/glb-gallery/gallery.ps1** — `param([string]$SrcDir, [string]$OutPng, [int]$Size = 320, [int]$Limit = 0)` (positional; defaults from `$RepoRoot/pick-assets` etc.)
- **Fix the existing divergence**: dot-source `scripts/godot-bin.ps1` / `Resolve-Godot` instead of the hardcoded `/Applications/...` paths (`gallery.sh:20-21`).
- Add `--rendering-driver metal` to the Godot arg array only when `$IsMacOS`.
- compose step: resolve `python3` falling back to `python` (`Get-Command ... -ErrorAction SilentlyContinue`), run `compose.py`.

## Phase 3 — reference sweep

1. **`.vscode/tasks.json`** (the only live invoker; lines 17-19, 34-37, 49-51, 64-66, 79-81, 93-95): all 6 tasks become `"command": "pwsh"`, `"args": ["-NoProfile", "-File", "${workspaceFolder}/…/<name>.ps1", …]`; `--force` arg → `-Force`. Update the header comment (Git Bash note → "requires PowerShell 7+ (pwsh) on PATH"). Keep `options.env.GODOT` wiring unchanged.
2. **Docs** — update invocations `.sh`→`.ps1` AND flag spellings (`--local`→`-Local`, `--force`→`-Force`, `--write-movie`→`-WriteMovie <path>`); add a one-time note (README dev-setup) that mac/linux need pwsh (`brew install powershell` / apt) and both `./scripts/run-server.ps1` and `pwsh scripts/run-server.ps1` work: `README.md`, `QUICKSTART.md`, `CONTRIBUTING.md`, `docs/DEPLOY.md`, `scripts/README.md`, `tools/README.md`, `tools/collision-hull/README.md`, `client/README.md`, `server/README.md`, `public-lobby/README.md`, `tools/asteroid-gen/README.md` (if it references build.sh).
3. **Skills** (these get executed, paths+flags must be exact): `.claude/skills/verify/SKILL.md`, `collision-hull-generator/SKILL.md`, `base-collision/SKILL.md`, `hardpoints/SKILL.md`.
4. **Comment-only touch-ups**: `.github/workflows/release.yml` lines 24/120/136 (path mentions → .ps1 — no workflow restructuring per user), `.gitignore:11`, `client/project.godot:35`.
5. **Leave untouched**: historical `.claude/plans/*.md`, `.PLAN/**`, docs/archive; workflows' inline bash steps (they don't reference removed files).

## Execution strategy (per user request + CLAUDE.md)

Delegate the build work to **Opus subagents** (`model: opus`): agent A writes `godot-bin.ps1` + the three scripts that dot-source it chain-wise (godot-import, run-client, export-clients) plus run-server; agent B (parallel) writes the independent scripts (deploy-railway ×2, asteroid-gen build, glb-gallery); agent C runs the reference sweep (tasks.json, docs, skills, comments) after A/B land. The lead session reviews diffs, does the `git rm`/`--chmod=+x` step, and runs verification.

## Phase 4 — sequencing

1. `scripts/godot-bin.ps1` → 2. `tools/godot-import.ps1`, `run-server.ps1`, `run-client.ps1` → 3. `export-clients.ps1`, `deploy-railway-*.ps1`, `build.ps1`, `gallery.ps1` → 4. `git rm` the 9 `.sh`, `git update-index --chmod=+x` the 9 `.ps1` → 5. tasks.json + docs/skills sweep.

## Verification (on this Windows machine)

1. Parse all 9: `[System.Management.Automation.Language.Parser]::ParseFile($p, [ref]$null, [ref]$err)`; assert no errors.
2. Resolver: `pwsh -NoProfile -c '. scripts/godot-bin.ps1; Resolve-Godot'` → real path; also test `$env:GODOT` override + bogus-value fallback-with-warning.
3. `tools/godot-import.ps1` plain (expect "already imported — nothing to do"); optionally `-Force` if Godot present.
4. `scripts/run-server.ps1 -Local`: confirm private message, build, "starting on :8090"; run a **second** instance to exercise the Get-NetTCPConnection stale-kill; then stop.
5. `scripts/run-client.ps1 -Local`: build + launch, verify it connects/opens (close after).
6. Deploy scripts: don't deploy — unit-test `Find-ProjectId` against a canned `railway list --json`-shaped object; verify missing-CLI guard message.
7. Run each tasks.json command line by hand (`pwsh -NoProfile -File tools/godot-import.ps1`, …).
8. Sweep: `rg '\.sh\b'` — remaining hits only in historical plans/.PLAN/archives.
9. macOS/Linux branches (codesign/ditto, lsof, metal flag) can't run here — keep them line-for-line mirrors of the bash originals; flag for a post-merge mac smoke test.
