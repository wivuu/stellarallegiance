# hardpoint-viewer/

Interactive, in-browser inspector for a GLB's **hardpoints** — the `HP_<Kind>_<Index>`
nodes that are the authoritative hardpoint inventory + geometry (see
`shared/Collision/GlbReader.cs` and the `hardpoints` skill). Orbit the hull, toggle
hardpoint kinds, and read each node's world position + forward vector.

No LLM, no build step, no dependencies beyond **Python 3** (stdlib) and a browser with WebGL.

## Run it

```powershell
tools/hardpoint-viewer/view.ps1
```

This starts a tiny local server and opens the viewer in your browser. By default the
**Library** panel lists every `.glb` under `pick-assets/` — type in the **Filter models…**
box to narrow the list (matches name or subfolder path; Esc clears), then click one to load
it. You can also press **Open .glb…** or drag any `.glb` onto the view (drag/drop and
file-open work for files anywhere on disk, not just the library folder).

Point it at a different folder, change the port, or don't auto-open a browser:

```powershell
tools/hardpoint-viewer/view.ps1 path/to/models
tools/hardpoint-viewer/view.ps1 --port 8123 --no-open
```

(`view.ps1` just runs `serve.py`; run `python3 tools/hardpoint-viewer/serve.py --help` for
options.)

## Controls

- **Drag** — orbit (three.js OrbitControls signs: drag right turns the hull to follow the
  cursor, drag up orbits the camera down toward the underside). The sign of each axis is a
  documented one-liner in `js/render.js` if you prefer the opposite.
- **Scroll** — zoom.
- **Up / Down arrows** — with the Library active (filter box or a row focused), step to the
  previous / next model in the (filtered) list and load it. Holding the key skims quickly;
  the load debounces so it settles on the one you stop at.
- **Filter box** — narrow the Library by name or subfolder path; Esc clears.
- **Legend checkboxes** — show/hide hardpoints by kind.
- **Collision hull toggle** — overlay the baked `COL_` convex collision proxies as a
  translucent, per-part-coloured wireframe (drawn depth-off so they read through the hull
  they sit inside). The panel appears only for models that carry `COL_` parts — i.e. bases
  and other meshes baked by `tools/collision-hull`; ships that collide via a single
  whole-mesh convex hull have none.
- **Hardpoint table row** — click to spotlight that hardpoint (pulses in the view).
- **reset view** — recenter the camera.

Each hardpoint is drawn as a colored marker with a short tick along its **forward** (local
+Z) and an on-canvas label. The sidebar shows the mesh AABB, longest axis (the normalize
scale ships/bases use), vertex/triangle counts (of the **visual** mesh — the `COL_` proxies
are counted separately in the Collision hull panel), and the full `HP_` table.

## How it works

- `serve.py` — stdlib `http.server` that serves this folder plus two endpoints:
  `GET /list` (JSON catalog of `.glb` under the library root) and `GET /asset/<relpath>`
  (the model bytes, with a path-traversal guard).
- `js/gltf.js` — dependency-free glTF/GLB parser: visual drawables in world space, embedded
  baseColor texture, bounds, `HP_` hardpoints (world translation = position, world +Z =
  forward), and the `COL_` compound-collision parts split out as a separate layer (same
  `COL_`-node subtree routing as `shared/Collision/GlbReader.cs`). Runs in the browser
  **and** under Node.
- `js/render.js` — from-scratch WebGL1 renderer (hull + collision-proxy overlay + markers +
  forward ticks + orbit camera + label projection).
- `js/app.js` — file loading, sidebar, legend, table, and the label overlay.

## Verifying the parser

`js/gltf.js` reproduces the same hardpoint contract as the authoritative Python dump. To
confirm on any model, run both and compare:

```sh
node   tools/hardpoint-viewer/selftest.js pick-assets/wc_icadv.glb
python3 .claude/skills/hardpoints/glb_hardpoints.py pick-assets/wc_icadv.glb
```

Positions, forwards, mesh AABB, and longest axis should match.
