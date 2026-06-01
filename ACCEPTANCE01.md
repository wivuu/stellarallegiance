# T0 Acceptance Test — Environment & Skeleton

Manual checklist. All four gates must pass before moving to T1.

---

## Prerequisites

SpacetimeDB server must be running before any steps below:

```bash
docker run -d --rm -p 3001:3000 --name stdb clockworklabs/spacetime start
docker logs stdb --tail 5   # expect "Starting SpacetimeDB listening on 0.0.0.0:3000"
```

---

## Gate 1 — CLI versions

```bash
docker run --rm clockworklabs/spacetime --version
dotnet --version
```

**Pass:** spacetime reports `2.x.x`, dotnet reports `10.x.x`.

---

## Gate 2 — Module publishes without error

```bash
docker run --rm \
  -v "$(pwd)/module":/workspace \
  -w /workspace \
  --network host \
  clockworklabs/spacetime publish stellar-allegiance \
    --server http://localhost:3001 \
    --yes
```

**Pass:** final line reads `Created new database` or `Publishing module...` with no error exit.

Verify the module is live:

```bash
docker run --rm --network host \
  clockworklabs/spacetime logs stellar-allegiance \
    --server http://localhost:3001
```

**Pass:** command returns without error (empty log is fine for the stock template).

---

## Gate 3 — Client project compiles

```bash
cd client
dotnet build
```

**Pass:** `Build succeeded.  0 Warning(s)  0 Error(s)`.

---

## Gate 4 — Client logs a successful connection

Open Godot and run the Main scene:

```bash
godot --path client/
```

1. In the Godot editor, open the **Main** scene (`scenes/Main.tscn`).
2. Press **F5** (or the Play button) to run.
3. Watch the **Output** panel.

**Pass:** the Output panel prints a line matching:

```
[ConnectionManager] Connected — identity: <hex-string>
```

The identity will be a long hex string. Any non-empty identity value is a pass.

**Fail signals:**
- `Connection error:` — server not running or wrong port; confirm Docker container is up.
- No output at all — check that `Main.tscn` has a `ConnectionManager` child node with the script attached.

---

## Notes

- The SpacetimeDB server binds inside Docker on port 3000, mapped to host port **3001**. The `ConnectionManager.cs` hardcodes `ws://localhost:3001`.
- The module deployed here is the stock template (Person table, Add/SayHello reducers). The game schema is added in T1.
- If the Docker container stops, restart it with the command in Prerequisites above.
