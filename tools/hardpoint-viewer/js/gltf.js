// gltf.js — minimal glTF 2.0 / GLB parser, no dependencies.
//
// Extracts drawable primitives (world-space positions/normals/uvs/indices),
// the embedded baseColor texture, world + mesh bounds, and every
// HP_<Kind>_<Index> hardpoint node. Mirrors shared/Collision/GlbReader.cs and
// tools/../.claude/skills/hardpoints/glb_hardpoints.py: a hardpoint's world
// translation is its position and its world +Z axis is its forward.
//
// Runs in the browser (window.GLTF) and under Node (module.exports) so the
// same parser can be self-tested against the authoritative Python dump.
(function (root, factory) {
  const api = factory();
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.GLTF = api;
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const TYPE_COMPONENTS = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT2: 4, MAT3: 9, MAT4: 16 };
  const COMPONENT_SIZE = { 5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4 };
  const HP_RE = /^HP_([A-Za-z]+)_(\d+)/;

  // ---- GLB container ------------------------------------------------------

  function parseGLB(buffer) {
    const dv = new DataView(buffer);
    if (dv.getUint32(0, true) !== 0x46546c67) throw new Error("Not a GLB (bad magic)");
    const version = dv.getUint32(4, true);
    if (version !== 2) throw new Error("Unsupported glTF version " + version);
    const total = dv.getUint32(8, true);
    let offset = 12;
    let json = null;
    let bin = null;
    while (offset + 8 <= total) {
      const len = dv.getUint32(offset, true);
      const type = dv.getUint32(offset + 4, true);
      const start = offset + 8;
      if (type === 0x4e4f534a) {
        json = JSON.parse(new TextDecoder("utf-8").decode(new Uint8Array(buffer, start, len)));
      } else if (type === 0x004e4942) {
        bin = new Uint8Array(buffer, start, len);
      }
      offset = start + len + (len % 4 ? 4 - (len % 4) : 0);
    }
    if (!json) throw new Error("GLB has no JSON chunk");
    return { json, bin };
  }

  // ---- column-major 4x4 matrix helpers ------------------------------------

  function matFromTRS(t, r, s) {
    const [x, y, z, w] = r;
    const x2 = x + x, y2 = y + y, z2 = z + z;
    const xx = x * x2, xy = x * y2, xz = x * z2;
    const yy = y * y2, yz = y * z2, zz = z * z2;
    const wx = w * x2, wy = w * y2, wz = w * z2;
    const [sx, sy, sz] = s;
    return [
      (1 - (yy + zz)) * sx, (xy + wz) * sx, (xz - wy) * sx, 0,
      (xy - wz) * sy, (1 - (xx + zz)) * sy, (yz + wx) * sy, 0,
      (xz + wy) * sz, (yz - wx) * sz, (1 - (xx + yy)) * sz, 0,
      t[0], t[1], t[2], 1,
    ];
  }

  function matFromNode(n) {
    if (n.matrix) return n.matrix.slice(); // already column-major
    return matFromTRS(n.translation || [0, 0, 0], n.rotation || [0, 0, 0, 1], n.scale || [1, 1, 1]);
  }

  function matMul(a, b) {
    const o = new Array(16);
    for (let c = 0; c < 4; c++) {
      for (let r = 0; r < 4; r++) {
        o[c * 4 + r] =
          a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
      }
    }
    return o;
  }

  function xformPoint(m, p) {
    return [
      m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12],
      m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13],
      m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14],
    ];
  }

  function xformDir(m, p) {
    return [
      m[0] * p[0] + m[4] * p[1] + m[8] * p[2],
      m[1] * p[0] + m[5] * p[1] + m[9] * p[2],
      m[2] * p[0] + m[6] * p[1] + m[10] * p[2],
    ];
  }

  function normalize(v) {
    const l = Math.hypot(v[0], v[1], v[2]) || 1;
    return [v[0] / l, v[1] / l, v[2] / l];
  }

  // ---- buffer / accessor access -------------------------------------------

  function base64ToBytes(b64) {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }

  function bufferBytes(gltf, bin, bufferIndex) {
    const buf = gltf.buffers[bufferIndex];
    if (buf.uri) {
      const m = /^data:[^,]*;base64,(.*)$/.exec(buf.uri);
      if (m) return base64ToBytes(m[1]);
      throw new Error("External buffer URIs are not supported: " + buf.uri.slice(0, 32));
    }
    if (!bin) throw new Error("glTF references the binary chunk but none was found");
    return bin;
  }

  function readAccessor(gltf, bin, index) {
    const acc = gltf.accessors[index];
    const view = gltf.bufferViews[acc.bufferView];
    const nComp = TYPE_COMPONENTS[acc.type];
    const compSize = COMPONENT_SIZE[acc.componentType];
    const bytes = bufferBytes(gltf, bin, view.buffer);
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const base = (view.byteOffset || 0) + (acc.byteOffset || 0);
    const stride = view.byteStride || compSize * nComp;
    const out = new Float32Array(acc.count * nComp);
    for (let i = 0; i < acc.count; i++) {
      const rowBase = base + i * stride;
      for (let c = 0; c < nComp; c++) {
        const p = rowBase + c * compSize;
        let v;
        switch (acc.componentType) {
          case 5126: v = dv.getFloat32(p, true); break;
          case 5125: v = dv.getUint32(p, true); break;
          case 5123: v = dv.getUint16(p, true); break;
          case 5121: v = dv.getUint8(p); break;
          case 5122: v = dv.getInt16(p, true); break;
          case 5120: v = dv.getInt8(p); break;
          default: v = 0;
        }
        out[i * nComp + c] = v;
      }
    }
    return { data: out, nComp, count: acc.count, min: acc.min, max: acc.max };
  }

  function readIndices(gltf, bin, index) {
    const acc = gltf.accessors[index];
    const view = gltf.bufferViews[acc.bufferView];
    const bytes = bufferBytes(gltf, bin, view.buffer);
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const base = (view.byteOffset || 0) + (acc.byteOffset || 0);
    const compSize = COMPONENT_SIZE[acc.componentType];
    const out = new Uint32Array(acc.count);
    for (let i = 0; i < acc.count; i++) {
      const p = base + i * compSize;
      out[i] =
        acc.componentType === 5125 ? dv.getUint32(p, true) :
        acc.componentType === 5123 ? dv.getUint16(p, true) :
        dv.getUint8(p);
    }
    return out;
  }

  // ---- scene walk ----------------------------------------------------------

  function extractTextureBytes(gltf, bin, materialIndex) {
    const mat = gltf.materials && gltf.materials[materialIndex];
    const base = mat && mat.pbrMetallicRoughness;
    const texRef = base && base.baseColorTexture;
    const factor = (base && base.baseColorFactor) || [1, 1, 1, 1];
    if (!texRef) return { factor, image: null };
    const tex = gltf.textures[texRef.index];
    const img = gltf.images[tex.source];
    if (img.bufferView == null) return { factor, image: null };
    const view = gltf.bufferViews[img.bufferView];
    const bytes = bufferBytes(gltf, bin, view.buffer);
    const slice = bytes.subarray(view.byteOffset || 0, (view.byteOffset || 0) + view.byteLength);
    return { factor, image: { mimeType: img.mimeType || "image/png", bytes: slice } };
  }

  function computeNormals(positions, indices) {
    const normals = new Float32Array(positions.length);
    const n = indices ? indices.length : positions.length / 3;
    for (let t = 0; t < n; t += 3) {
      const ia = (indices ? indices[t] : t) * 3;
      const ib = (indices ? indices[t + 1] : t + 1) * 3;
      const ic = (indices ? indices[t + 2] : t + 2) * 3;
      const ux = positions[ib] - positions[ia];
      const uy = positions[ib + 1] - positions[ia + 1];
      const uz = positions[ib + 2] - positions[ia + 2];
      const vx = positions[ic] - positions[ia];
      const vy = positions[ic + 1] - positions[ia + 1];
      const vz = positions[ic + 2] - positions[ia + 2];
      const nx = uy * vz - uz * vy;
      const ny = uz * vx - ux * vz;
      const nz = ux * vy - uy * vx;
      for (const i of [ia, ib, ic]) {
        normals[i] += nx; normals[i + 1] += ny; normals[i + 2] += nz;
      }
    }
    return normals;
  }

  function parseGLTFDocument(buffer) {
    const { json: gltf, bin } = parseGLB(buffer);
    const drawables = [];
    const hardpoints = [];
    const world = { min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] };
    const mesh = { min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] };
    let vertexCount = 0;
    let triangleCount = 0;

    function growBounds(b, p) {
      for (let k = 0; k < 3; k++) {
        if (p[k] < b.min[k]) b.min[k] = p[k];
        if (p[k] > b.max[k]) b.max[k] = p[k];
      }
    }

    function walk(nodeIndex, parent) {
      const node = gltf.nodes[nodeIndex];
      const worldMat = matMul(parent, matFromNode(node));

      const hp = node.name && HP_RE.exec(node.name);
      if (hp) {
        hardpoints.push({
          name: node.name,
          kind: hp[1],
          index: parseInt(hp[2], 10),
          pos: [worldMat[12], worldMat[13], worldMat[14]],
          fwd: normalize(xformDir(worldMat, [0, 0, 1])),
        });
      }

      if (node.mesh != null) {
        for (const prim of gltf.meshes[node.mesh].primitives) {
          if (prim.mode != null && prim.mode !== 4) continue; // triangles only
          if (prim.attributes.POSITION == null) continue;
          const posAcc = readAccessor(gltf, bin, prim.attributes.POSITION);
          const localPos = posAcc.data;
          const uvs =
            prim.attributes.TEXCOORD_0 != null
              ? readAccessor(gltf, bin, prim.attributes.TEXCOORD_0).data
              : new Float32Array((localPos.length / 3) * 2);
          const indices = prim.indices != null ? readIndices(gltf, bin, prim.indices) : null;

          // mesh AABB from accessor min/max, transformed to world (matches the
          // Python tool when node transforms are identity; correct otherwise).
          if (posAcc.min && posAcc.max) {
            for (const corner of aabbCorners(posAcc.min, posAcc.max)) growBounds(mesh, xformPoint(worldMat, corner));
          }

          const worldPos = new Float32Array(localPos.length);
          for (let i = 0; i < localPos.length; i += 3) {
            const p = xformPoint(worldMat, [localPos[i], localPos[i + 1], localPos[i + 2]]);
            worldPos[i] = p[0]; worldPos[i + 1] = p[1]; worldPos[i + 2] = p[2];
            growBounds(world, p);
          }

          let normals;
          if (prim.attributes.NORMAL != null) {
            const local = readAccessor(gltf, bin, prim.attributes.NORMAL).data;
            normals = new Float32Array(local.length);
            for (let i = 0; i < local.length; i += 3) {
              const d = xformDir(worldMat, [local[i], local[i + 1], local[i + 2]]);
              normals[i] = d[0]; normals[i + 1] = d[1]; normals[i + 2] = d[2];
            }
          } else {
            normals = computeNormals(worldPos, indices);
          }

          const material = extractTextureBytes(gltf, bin, prim.material);
          drawables.push({ positions: worldPos, normals, uvs, indices, material });
          vertexCount += localPos.length / 3;
          triangleCount += (indices ? indices.length : localPos.length / 3) / 3;
        }
      }

      for (const c of node.children || []) walk(c, worldMat);
    }

    const scene = gltf.scenes[gltf.scene || 0];
    const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    for (const rootNode of scene.nodes) walk(rootNode, identity);

    const size = mesh.min[0] === Infinity ? [0, 0, 0] : [mesh.max[0] - mesh.min[0], mesh.max[1] - mesh.min[1], mesh.max[2] - mesh.min[2]];
    const longestAxis = Math.max(size[0], size[1], size[2]);
    hardpoints.sort((a, b) => (a.kind === b.kind ? a.index - b.index : a.kind < b.kind ? -1 : 1));

    return {
      drawables,
      hardpoints,
      bounds: world,
      meshAabb: mesh,
      stats: { vertexCount, triangleCount: Math.round(triangleCount), longestAxis, size },
    };
  }

  function aabbCorners(mn, mx) {
    const out = [];
    for (let i = 0; i < 8; i++) out.push([i & 1 ? mx[0] : mn[0], i & 2 ? mx[1] : mn[1], i & 4 ? mx[2] : mn[2]]);
    return out;
  }

  return { parseGLB, parseGLTFDocument, matMul, matFromNode, matFromTRS, normalize };
});
