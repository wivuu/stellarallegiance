# Re-imagine SectorEnvironment: backlit dust + raymarched sun beams

## Context

The reference image (dense asteroid belt backlit by a blazing warm sun) defines the target look:
frame-filling volumetric crepuscular beams streaming through sunlit dust, asteroids as near-black
silhouettes carving long high-contrast shadow streaks through the lit haze, deep blacks + hot warm
highlights with heavy bloom.

`client/scripts/SectorEnvironment.cs` already has all four ingredients — sun override, billboard
dust puffs, a screen-space god-ray pass, multiply-blend occluder shadow volumes — but tuned for
subtlety: shafts darken 2% fading within 750 m, dust defaults to a dim dusky tone with no
view-dependent scattering, god rays only amplify already-bright pixels near the sun disc.

**Decided direction (user):**
1. **Hybrid** — keep billboard-puff dust as the medium's body; replace BOTH the screen-space god
   rays and the shadow-volume shafts with ONE raymarched "sun beams" pass (per-pixel view-ray
   march, analytic sphere occluder shadows, forward-scattering phase).
2. **New default look** — any sector streaming sun + dust gets it automatically; existing YAML is
   re-interpreted (`sun.god-rays` becomes beam strength). No new YAML knobs. Zero server/wire changes.
3. **Modest GPU budget** — the deleted 48-tap god-ray pass + shadow-volume overdraw pay for the
   14-sample raymarch. No half-res infrastructure.

Environment facts: Godot 4.6, Forward+ (project.godot sets no rendering_method) → spatial shaders
have `hint_depth_texture`, `INV_PROJECTION_MATRIX`, `INV_VIEW_MATRIX`, `CAMERA_POSITION_WORLD`,
reversed-Z. Camera far 6000, sun quad at 4500 m. Glow calibrated against `glow_hdr_threshold 0.95`
in Main.tscn — **do not touch Main.tscn**. Rocks carry `SetMeta("shadowRadius", row.Radius)`
(WorldRenderer.cs:1430) so rock occluder spheres need no mesh walking.

## 1. Beams pass: fullscreen near-plane spatial quad (SectorEnvironment.cs)

A `MeshInstance3D` with 2×2 `QuadMesh`, spatial shader with vertex-stage
`POSITION = vec4(VERTEX.xy, 1.0, 1.0)` (near plane, reversed-Z), rendered in the transparent pass:
`blend_add, unshaded, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled`.
`RenderPriority = 5` (after dust puffs at 0 — repo already uses RenderPriority this way, shafts
were 2). Loose `CustomAabb` so it never frustum-culls. `Visible` gated on
`HasSun && GodRays > 0.01`.

Why this mechanism: canvas_item shaders have no depth texture (beams must stop at asteroid
surfaces — that's the silhouette effect); CompositorEffect is far more plumbing for zero gain and
runs post-glow, whereas the transparent pass runs **before tonemap/glow so hot beam cores feed
bloom for free**.

No per-frame C# updates: camera built-ins come free in the shader; the whole god-ray `_Process`
override is deleted. Per-sector uniforms set in `Apply`; occluder/cloud arrays re-uploaded on the
existing 150 m camera-move regather.

## 2. Beam shader (new `BeamShaderCode` const)

Load-bearing structure (final code may adjust constants):

```glsl
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, depth_test_disabled,
            cull_disabled, fog_disabled, shadows_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform vec3  sun_dir = vec3(0.0, 0.0, 1.0);      // world unit vector TOWARD the sun
uniform vec3  sun_color : source_color = vec3(1.0, 0.85, 0.6);
uniform float beam_strength = 0.5;                 // sun.god-rays, re-purposed
uniform float sun_energy = 1.3;
// Haze medium: flat "air glow" floor + flattened-belt profile matching the seeded dust disc.
uniform vec3  haze_center = vec3(0.0);             // derived from streamed cloud positions
uniform float base_density = 0.0;                  // dustless god-ray sectors still get air glow
uniform float haze_density = 0.0;                  // from mean cloud density × DustOpacity
uniform float haze_half_y = 200.0;                 // vertical gaussian half-extent of the belt
uniform float haze_radius = 700.0;                 // radial gaussian extent
uniform vec4 occluders[24];                        // xyz = center, w = shadow radius
uniform int  occluder_count = 0;
uniform vec4 clouds[10];                           // xyz = center, w = radius (10 nearest)
uniform vec4 cloud_params[10];                     // x = density, rest reserved
uniform int  cloud_count = 0;

const int SAMPLES = 14;
const float MAX_RANGE = 2400.0;
const float CLOUD_SHADOW = 0.55;

float ign(vec2 px) {  // interleaved gradient noise: dither march start, hides banding
    return fract(52.9829189 * fract(dot(px, vec2(0.06711056, 0.00583715))));
}
float haze_at(vec3 p) {
    vec3 q = p - haze_center;
    float ry = q.y / haze_half_y;
    float rr = length(q.xz) / haze_radius;
    return base_density + haze_density * exp(-ry * ry) * exp(-rr * rr * 0.7);
}
void vertex() { POSITION = vec4(VERTEX.xy, 1.0, 1.0); }
void fragment() {
    float depth = texture(depth_tex, SCREEN_UV).r;             // reversed-Z: 1 near, 0 far
    vec4 vpos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
    vpos.xyz /= vpos.w;
    float scene_dist = length(vpos.xyz);
    vec3 ro = CAMERA_POSITION_WORLD;
    vec3 world_end = (INV_VIEW_MATRIX * vec4(vpos.xyz, 1.0)).xyz;
    vec3 rd = normalize(world_end - ro);

    float t_max = min(scene_dist, MAX_RANGE);
    float dt = t_max / float(SAMPLES);
    float t = dt * ign(FRAGCOORD.xy);
    // Forward-scatter lobe: blaze looking sunward, faint isotropic floor elsewhere.
    float mu = dot(rd, sun_dir);
    float phase = 0.06 + 1.6 * pow(clamp(mu * 0.5 + 0.5, 0.0, 1.0), 8.0);

    vec3 inscatter = vec3(0.0);
    float transmit = 1.0;
    for (int i = 0; i < SAMPLES; i++) {
        vec3 p = ro + rd * t;
        float dens = haze_at(p);
        float sun_vis = 1.0;
        for (int c = 0; c < cloud_count; c++) {   // clouds: density boost + soft sun shadow
            vec3 toC = clouds[c].xyz - p;
            float r = clouds[c].w;
            float d2 = dot(toC, toC);
            dens += cloud_params[c].x * exp(-d2 / (r * r) * 2.0);
            float along = dot(toC, sun_dir);
            if (along > 0.0) {
                float perp2 = d2 - along * along;
                float occ = 1.0 - smoothstep(r * r * 0.4, r * r, perp2);
                sun_vis *= 1.0 - CLOUD_SHADOW * occ * cloud_params[c].x;
            }
        }
        for (int o = 0; o < occluder_count; o++) { // rocks/bases: soft-penumbra sphere shadow
            vec3 toC = occluders[o].xyz - p;
            float along = dot(toC, sun_dir);
            if (along > 0.0) {
                float r = occluders[o].w;
                float perp2 = dot(toC, toC) - along * along;
                float soft = r * (0.35 + 0.0004 * along); // penumbra widens downsun
                float inner = r - soft, outer = r + soft;
                sun_vis *= smoothstep(inner * inner, outer * outer, perp2);
            }
        }
        float sigma = dens * dt;
        inscatter += transmit * sun_vis * sigma * phase * sun_color;
        transmit *= exp(-sigma * 0.6);  // mild self-extinction; puffs carry the real body
        t += dt;
    }
    ALBEDO = inscatter * beam_strength * sun_energy;
    ALPHA = 1.0;  // blend_add: black contributes nothing
}
```

Budget: 14 samples × (24 sphere tests + 10 cloud tests) ≈ same order as the deleted 48-tap
full-res screen-texture march + shadow-volume overdraw. Tuning levers: `SAMPLES`, array caps.

## 3. Haze model + YAML reinterpretation (no wire changes)

Computed once per sector in a new `ApplyBeams(SectorEnv?)` called from `Apply` (after `ApplySun`):
- `haze_center` = mean of streamed `DustClouds` positions; `haze_half_y` = max |ΔY| + mean radius;
  `haze_radius` = max horizontal Δ + mean radius. (Server seeds a flattened disc — Y extent 15% of
  coverage — so this analytic slab faithfully tracks where the billboards actually are.)
- `haze_density` = `mean(cloud.Density) × env.DustOpacity × HazeGain` (start `HazeGain = 0.012`).
- **Dustless god-ray sectors** (home sector authors `god-rays: 1`, no dust): quad still visible with
  `base_density = 0.004 × GodRays`, `haze_density = 0` — faint sunward air-glow with rock shadows.
- `beam_strength` = `env.GodRays`; ≤ 0.01 hides the quad.

## 4. Occluder plumbing simplification (WorldRenderer.cs)

- `GatherShadowOccluders` → `GatherBeamOccluders`, returns `(Node3D Node, float Radius)`:
  - rocks: keep the existing distance logic (2500 m + per-rock `shadowRadius` reach, nearest-first);
    sphere radius = `0.8f × shadowRadius` meta (inset sphere + soft penumbra reads better on lumpy rocks);
  - bases: one-time cached bounding sphere from child `MeshInstance3D` AABBs (replaces all hull-vert
    machinery);
  - cap drops 64 → **24** (hulls only shadowed 750 m of nearby dust; the raymarch covers the whole
    2400 m frustum, and 24 is the per-pixel budget).
- Same gather also selects the **10 nearest dust clouds** to the camera from `row.Env.DustClouds`.
- `UpdateShadowOccluders` keeps the 150 m move throttle; gate property renamed
  `CastsSectorShadows` → `WantsBeamOccluders` = `HasSun && GodRays > 0.01` (no longer requires dust).
- `SectorEnvironment.UpdateOccluders(spheres, clouds)` re-packs into preallocated `Vector4[]`s and
  `SetShaderParameter` — no mesh builds, no node churn. Read `node.GlobalPosition` at upload time
  with `IsInstanceValid` guard.

## 5. Puff shader upgrade: sunlit dust body

In `BuildDust` / `PuffShaderCode`:
- **Tones from the resolved sun colour** (via `_light.LightColor`, already set — `ApplySun` runs
  before `BuildDust`) instead of the hardcoded warm/cool constants:
  `warm = dustColor × (0.6+0.8·sunR, 0.55+0.7·sunG, 0.5+0.6·sunB)`, `cool = dustColor × (0.75, 0.9, 1.15)`.
  Keep the baked per-puff sun-side exposure lerp (0.4 → 1.2).
- **View-dependent forward scattering** (per-instance in the vertex stage — puffs are small vs
  distances). New uniforms `sun_dir`, `sun_color`, `backlight ≈ 0.9·GodRays + 0.35`:
  ```glsl
  // vertex
  vec3 vd = normalize(MODEL_MATRIX[3].xyz - INV_VIEW_MATRIX[3].xyz);
  v_phase = pow(clamp(dot(vd, sun_dir) * 0.5 + 0.5, 0.0, 1.0), 6.0);
  // fragment: additive glow so authored-dark dust stays dark off-sun, ignites when backlit
  ALBEDO = v_col.rgb * (0.85 + 0.28 * n) + sun_color * (v_phase * backlight * fall * v_col.a);
  ```
- **No per-puff CPU sphere occlusion** — the beam pass paints the shadow lanes (absence of additive
  light IS the shadow); baking camera-dependent occlusion into sector-static instance colours is a
  correctness mismatch.
- Optional perf giveback (only after first visual check): puff count divisor 6.5 → 7.5.

## 6. Sun + silhouettes (Sun.cs only; Main.tscn untouched)

- Hotter gradient: offsets `{0, 0.1, 0.32, 1}`, colors `{white, (1,0.9,0.7), (1,0.45,0.15,0.55), transparent}`.
- New `SetIntensity(float sunEnergy)` called from `ApplySun` next to `SetDiscSize`:
  `EmissionEnergyMultiplier = 5f × Mathf.Clamp((sunEnergy - 1.3f) × 0.5f + 1f, 1f, 1.6f)`
  — pins ×1.0 at the 1.3 default (stock sectors byte-identical), scales up for hotter authored suns.
- Asteroid silhouettes are free: low ambient + near-black dust + blazing beams behind PBR rocks.
  Optional polish: `Rim = 0.4` on the grey fallback `_asteroidMat` only. GLB materials untouched.

## 7. Deletions

| Delete | Where |
|---|---|
| `ShadowVolume.cs` (whole file + `.uid`) | client/scripts |
| `ShaftShaderCode`, `_shaftMat`, `_shadowByNode`, `_shadowWantScratch`, `_shadowDropScratch`, `ShaftLength/Darkness/FadeDistance`, `SyncShafts()`, `ClearShafts()` | SectorEnvironment.cs |
| `GodRayShaderCode`, `_godRayLayer`, `_godRayMat`, `_godRaysStrength`, `_godRaysOn`, `ApplyGodRays()`, entire `_Process` override | SectorEnvironment.cs |
| `CollectHullVerts` / `CollectMeshVerts` / `HullVertsFor` / `_hullVertCache` / `_hullVertScratch` | WorldRenderer.cs |
| `Invalidate()` stays but drops `_shadowByNode.Clear()` (nothing parents to rocks anymore) | SectorEnvironment.cs |

Server (`server/`, `shared/`), YAML schema, dust seeding, `DustVisionMult` vision coupling: **zero changes**.

## 8. Ordering / interactions

- Puffs (priority 0) → beams (priority 5), both transparent, depth-read-only. Beams march through
  puffs (they don't write depth) — desired: beam light over puffs = lit haze; sun-blocked regions
  get no added light = dark streaks through visible dust.
- Depth stop at opaque geometry: foreground rocks punch black silhouettes into the glow; sky pixels
  (far depth) get the full march → hottest convergence around the sun disc.
- Sun quad (additive, sorts 4500 m away → draws before the near-plane beams quad), LensFlare, and
  the sky shader's glare stay as-is; additive-over-additive is harmless.

## 9. Implementation order

1. SectorEnvironment.cs: deletions (§7) + beams quad, `BeamShaderCode`, `ApplyBeams` (§1–3);
   rework `Apply`/`UpdateOccluders` signatures to sphere lists.
2. WorldRenderer.cs: simplify gather, rename gate, nearest-cloud selection, base sphere cache (§4).
3. Delete ShadowVolume.cs.
4. Puff upgrade (§5).
5. Sun.cs (§6).
6. Tune constants against Verge.

## 10. Verification

1. Build: `dotnet build` the client csproj + full solution (server must build untouched;
   `git diff server/ shared/` empty).
2. Run headless server on brimstone-gambit + `scripts/run-client.sh` with `--autofly`
   (game flags before `--`). Fly/warp to **Verge (sector 1)**; F3 overview to switch viewed sectors
   (exercises regather + `Invalidate`).
3. Checklist: beams blaze looking sunward, fade looking away; shadow lanes visible in haze, no
   popping across the 150 m regather (soft penumbra should mask churn); beams stop at rock surfaces
   (black silhouette + glow rim); no banding at 14 samples (IGN dither); home sector (god-rays 1,
   no dust) shows faint air-glow, not blackness; **Defaultio (sector 2, no env) renders exactly
   stock** (quad hidden, defaults restored); reconnect rebuilds cleanly.
4. Perf: frame time vs master in Verge looking sunward (worst case) — target parity. Levers:
   `SAMPLES`, sphere/cloud caps, puff divisor.
