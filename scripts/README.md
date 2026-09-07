# scripts/

Running and deploying the game now go through the .NET Aspire AppHost (`apphost/`) via the
`aspire` CLI — see the root [README](../README.md) and [QUICKSTART](../QUICKSTART.md) (`aspire
run` / `aspire start`, `aspire resource client launch`, `aspire do deploy-lobby`/`deploy-server`).

What's left here are the helpers Aspire doesn't replace: running a server/client against the
**hosted** public lobby (the AppHost always wires the local one), exporting release client builds,
and resolving a Godot install for the tools below. Both are **PowerShell 7+ (`pwsh`)** — cross-platform
(Windows natively; macOS/Linux after `brew install powershell` or your package manager) — and
wired up as VS Code tasks in `.vscode/tasks.json`. Run them **from the repo root**, e.g.
`scripts/export-clients.ps1`.

## Run against the hosted public lobby

| Script | What it does |
|--------|--------------|
| `run-server.ps1` | Builds (Release) and runs the sim server, **publishing it to the hosted public lobby** (`PUBLIC_LOBBY`, default `https://stellarlobby.wivuu.com`) under your hostname (`SIM_PUBLIC_NAME` to rename). First publish prints a device code to approve at the hosted lobby; the credential is saved beside `sim-cache/`. `-Local` stays private (direct `ws://` only); `--autostart`, `--secret`, `$env:SIM_PORT` pass through. |
| `run-client.ps1` | Rebuilds the client C# and launches Godot on the **hosted lobby's server browser**. `-Local` connects straight to `localhost:8090`; `-Release` runs optimized C#; `-WriteMovie <path>` records; extra args pass to Godot (`--host host:port`, harness flags). From zsh/bash hand the array to PowerShell itself: `pwsh -Command "& ./scripts/run-client.ps1 -GodotArgs @('--autofly','--','--ui-shot=…')"`. |

For the full local stack (local lobby + Postgres + server + dashboard) use `aspire run` instead.

## Godot resolution

| Script | What it does |
|--------|--------------|
| `godot-bin.ps1` | **Dot-sourced**, not run. `. scripts/godot-bin.ps1` defines `Resolve-Godot`, which returns the path to a runnable Godot 4 .NET ("mono") binary as a string (or `$null`) — callers do `$Godot = Resolve-Godot`. Resolution order: preset `$env:GODOT` → the per-workstation `dotnet user-secrets` store (key `godot.executablePath`, id `stellarallegiance`; set via the "Godot: set executable path" VS Code task) → `godot-mono`/`godot4`/`godot` on PATH → standard install locations. Dot-sourced by `export-clients.ps1`, `tools/godot-import.ps1`, and `tools/glb-gallery/gallery.ps1`; the Aspire AppHost uses the same resolution order independently for the `client` resource. |

## Export

| Script | What it does |
|--------|--------------|
| `export-clients.ps1` | Exports the Godot client for macOS + Windows + Linux and packages the builds for tester distribution. (Handles the macOS hardened-runtime/ad-hoc-signing gotcha that otherwise SIGKILLs the app at launch.) |

See the root [README](../README.md) and [QUICKSTART](../QUICKSTART.md) for prerequisites and the
`aspire run` local loop.
