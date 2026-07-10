

Generic (works on any GLB today):
- bake.py takes --glb PATH --yaml PATH --out PATH — nothing is hardcoded to this station beyond defaults.
- All validations are model-agnostic: the visual convex hull, AABB, and bounding-radius neutrality checks are computed from whatever GLB you feed it, and the dock-corridor check reads the HP_DockingEntrance*/HP_DockingExit empties out of that GLB.
- The downstream pipeline (once B2/B3 land) is fully generic: GlbReader will bucket any COL_* node in any model into a sub-hull, and GlbLoader already hides COL_* in anything it loads — ships included. So a new base model with baked COL_ parts gets a compound hull with zero code changes.

Not automatic: the 7 boxes in base-col.yaml were designed around this station's silhouette (limb-aligned thin boxes, bay left open). Load a different base model and hit build, and nothing generates parts for it — you'd author a new YAML. The intended workflow for a new model:

1. uv run bake.py --glb newbase.glb --suggest → prints k-means-fitted candidate boxes as YAML seed
2. Hand-refine the YAML (tighten limbs, open the dock corridors)
3. --check until validations pass, then bake

That's maybe 20–30 minutes of authoring per model, and the validators catch the two failure modes that matter (metric drift, capped dock corridors).

If you want true one-command generation later, there's a middle path we deliberately deferred: an --auto mode running offline V-HACD (trimesh) whose output is baked into the GLB — determinism was only a concern for runtime decomposition, so baked V-HACD is safe. The catch is quality: V-HACD happily caps docking bays, so it would lean on the corridor validator and still often want hand-touchup. Happy to add that as a follow-up after STOP 3 if authoring per model feels like too much friction.