# scripts/

Helper shell scripts for building, running, exporting, and deploying the game. They're written
for **bash** (macOS/Linux natively; Windows via Git Bash) and are wired up as VS Code tasks in
`.vscode/tasks.json`. Run them **from the repo root**, e.g. `scripts/run-server.sh --local`.

## Run locally

| Script | What it does |
|--------|--------------|
| `run-server.sh` | Builds and runs the authoritative sim server (`server/`). Default publishes to the public lobby for discovery; `--local` stays private (direct `ws://` only); `--autostart` skips the ready-up gate for a perpetual match. |
| `run-client.sh` | Rebuilds the client C# fresh, then launches the Godot client. Default opens the public-lobby server browser; `--local` connects straight to `localhost:8090`. Extra args pass through to Godot. |

The rebuild in `run-client.sh` exists so Godot can't launch a stale assembly against a rebuilt
server (which causes silent protocol skew).

## Godot resolution

| Script | What it does |
|--------|--------------|
| `godot-bin.sh` | **Sourced**, not run. `source scripts/godot-bin.sh; godot_resolve` sets `$GODOT` to a runnable Godot 4 .NET ("mono") binary. Resolution order: preset `$GODOT` → `godot-mono`/`godot4`/`godot` on PATH → standard install locations. |

## Export & deploy

| Script | What it does |
|--------|--------------|
| `export-clients.sh` | Exports the Godot client for macOS + Windows + Linux and packages the builds for tester distribution. (Handles the macOS hardened-runtime/ad-hoc-signing gotcha that otherwise SIGKILLs the app at launch.) |
| `deploy-railway-server.sh [name]` | Deploys/updates a game server on Railway, published to the default public lobby. Re-running with the same name updates the existing project. The name doubles as the server's public name. Needs the Railway CLI logged in. |
| `deploy-railway-lobby.sh [name]` | Deploys/updates the public lobby (`public-lobby/`: server registry + WebRTC signaling relay) on Railway. Re-running with the same name updates it. Needs the Railway CLI logged in. |

See the root [README](../README.md) and [QUICKSTART](../QUICKSTART.md) for prerequisites.
