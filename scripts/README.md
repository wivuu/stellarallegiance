# scripts/

Helper scripts for building, running, exporting, and deploying the game. They're written for
**PowerShell 7+ (`pwsh`)** — cross-platform (Windows natively; macOS/Linux after
`brew install powershell` or your package manager) — and are wired up as VS Code tasks in
`.vscode/tasks.json`. Run them **from the repo root**, e.g. `scripts/run-server.ps1 -Local`.

## Run locally

| Script | What it does |
|--------|--------------|
| `run-server.ps1` | Builds and runs the authoritative sim server (`server/`). Default publishes to the public lobby for discovery; `-Local` stays private (direct `ws://` only); `--autostart` skips the ready-up gate for a perpetual match. |
| `run-client.ps1` | Rebuilds the client C# fresh, then launches the Godot client. Default opens the public-lobby server browser; `-Local` connects straight to `localhost:8090`. Extra args pass through to Godot. |

The rebuild in `run-client.ps1` exists so Godot can't launch a stale assembly against a rebuilt
server (which causes silent protocol skew).

## Godot resolution

| Script | What it does |
|--------|--------------|
| `godot-bin.ps1` | **Dot-sourced**, not run. `. scripts/godot-bin.ps1` defines `Resolve-Godot`, which returns the path to a runnable Godot 4 .NET ("mono") binary as a string (or `$null`) — callers do `$Godot = Resolve-Godot`. Resolution order: preset `$env:GODOT` → the per-workstation `dotnet user-secrets` store (key `godot.executablePath`, id `stellarallegiance`; set via the "Godot: set executable path" VS Code task) → `godot-mono`/`godot4`/`godot` on PATH → standard install locations. |

## Export & deploy

| Script | What it does |
|--------|--------------|
| `export-clients.ps1` | Exports the Godot client for macOS + Windows + Linux and packages the builds for tester distribution. (Handles the macOS hardened-runtime/ad-hoc-signing gotcha that otherwise SIGKILLs the app at launch.) |
| `deploy-railway-server.ps1 [name]` | Deploys/updates a game server on Railway, published to the default public lobby. Re-running with the same name updates the existing project. The name doubles as the server's public name. Needs the Railway CLI logged in. |
| `deploy-railway-lobby.ps1 [name]` | Deploys/updates the public lobby (`public-lobby/`: server registry + WebRTC signaling relay) on Railway. Re-running with the same name updates it. Needs the Railway CLI logged in. |

See the root [README](../README.md) and [QUICKSTART](../QUICKSTART.md) for prerequisites.
