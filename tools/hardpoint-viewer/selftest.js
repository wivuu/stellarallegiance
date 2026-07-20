#!/usr/bin/env node
// selftest.js — verify js/gltf.js against the authoritative hardpoint contract.
//
// Parses a GLB with the same parser the browser uses and prints each
// HP_<Kind>_<Index> node's world position + forward and the mesh AABB, in the
// exact format of .claude/skills/hardpoints/glb_hardpoints.py. Run it beside
// that tool to confirm they agree, e.g.:
//
//   node tools/hardpoint-viewer/selftest.js pick-assets/wc_icadv.glb
//   python3 .claude/skills/hardpoints/glb_hardpoints.py pick-assets/wc_icadv.glb
//
// Exits non-zero if the GLB can't be parsed.
const fs = require("fs");
const path = require("path");
const GLTF = require("./js/gltf.js");

const file = process.argv[2];
if (!file) {
  console.error("usage: node selftest.js <file.glb>");
  process.exit(2);
}

const buf = fs.readFileSync(file);
const ab = buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength);
const doc = GLTF.parseGLTFDocument(ab);

const b = doc.meshAabb;
const f3 = (v) => (v >= 0 ? "+" : "") + v.toFixed(3);
console.log(`== ${file} ==`);
console.log(
  `   mesh AABB: min=(${b.min.map((x) => x.toFixed(3)).join(",")}) ` +
    `max=(${b.max.map((x) => x.toFixed(3)).join(",")})  longest-axis=${doc.stats.longestAxis.toFixed(4)}`
);
for (const hp of doc.hardpoints) {
  console.log(
    `   ${hp.name.padEnd(24)} pos=(${hp.pos.map((x) => x.toFixed(3).padStart(8)).join(",")}) ` +
      `fwd=(${hp.fwd.map((x) => f3(x).slice(0, 5)).map((s) => s.padStart(5)).join(",")})`
  );
}
console.log(`   [${doc.hardpoints.length} hardpoints, ${doc.stats.vertexCount} verts, ${doc.stats.triangleCount} tris]`);
