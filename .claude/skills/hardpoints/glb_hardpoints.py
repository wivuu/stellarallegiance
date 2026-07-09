#!/usr/bin/env python3
"""Dump HP_<Kind>_<Index> hardpoint nodes from a GLB, replicating shared/Collision/GlbReader.cs.

Usage:
  glb_hardpoints.py <file.glb>                 # authored-unit positions + forwards
  glb_hardpoints.py <file.glb> --length 5.5    # also print world-unit positions
                                               #   (ws = length / longest AABB axis, the same
                                               #    scale World.LoadShipHull / GlbLoader.
                                               #    NormalizeLongestAxis apply; ships use the
                                               #    hull's model-length, bases radius*2)
  glb_hardpoints.py <dir> [--length ...]       # every .glb in a directory

Output per HP_ node: name, position (node world translation), forward (node world +Z) —
the exact contract GlbReader/SimModel consume. Also prints the mesh AABB + longest axis
(from glTF POSITION accessor min/max, matching ConvexHull.LongestAxis / MeshAabb).
No dependencies (stdlib only).
"""

import json, struct, sys, os


def load_gltf(path):
    with open(path, "rb") as f:
        magic, _ver, _length = struct.unpack("<III", f.read(12))
        assert magic == 0x46546C67, f"{path}: not a GLB"
        clen, _ctype = struct.unpack("<II", f.read(8))
        return json.loads(f.read(clen))


def node_matrix(n):
    if "matrix" in n:
        m = n["matrix"]  # column-major
        return [[m[0], m[4], m[8], m[12]],
                [m[1], m[5], m[9], m[13]],
                [m[2], m[6], m[10], m[14]],
                [m[3], m[7], m[11], m[15]]]
    t = n.get("translation", [0, 0, 0])
    r = n.get("rotation", [0, 0, 0, 1])  # x,y,z,w
    s = n.get("scale", [1, 1, 1])
    x, y, z, w = r
    R = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
         [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    M = [[R[i][j] * s[j] for j in range(3)] + [t[i]] for i in range(3)]
    M.append([0, 0, 0, 1])
    return M


def matmul(A, B):
    return [[sum(A[i][k] * B[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def walk(g, idx, parent, out):
    n = g["nodes"][idx]
    M = matmul(parent, node_matrix(n))
    out.append((n, M))
    for c in n.get("children", []):
        walk(g, c, M, out)


def transform_point(M, p):
    return tuple(M[i][0] * p[0] + M[i][1] * p[1] + M[i][2] * p[2] + M[i][3] for i in range(3))


def aabb(g, flat):
    """Union of every mesh POSITION accessor's min/max corners, node-transformed
    (8 corners per accessor — same result as MeshAabb / ConvexHull over the point cloud
    for the axis-extent purpose)."""
    lo = [float("inf")] * 3
    hi = [float("-inf")] * 3
    for n, M in flat:
        if "mesh" not in n:
            continue
        for prim in g["meshes"][n["mesh"]].get("primitives", []):
            acc = g["accessors"][prim["attributes"]["POSITION"]]
            mn, mx = acc.get("min"), acc.get("max")
            if not mn or not mx:
                continue
            for cx in (mn[0], mx[0]):
                for cy in (mn[1], mx[1]):
                    for cz in (mn[2], mx[2]):
                        p = transform_point(M, (cx, cy, cz))
                        for i in range(3):
                            lo[i] = min(lo[i], p[i])
                            hi[i] = max(hi[i], p[i])
    if lo[0] == float("inf"):
        return None, None, 0.0
    return lo, hi, max(hi[i] - lo[i] for i in range(3))


def dump(path, length=None):
    g = load_gltf(path)
    ident = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]
    flat = []
    for root in g["scenes"][g.get("scene", 0)]["nodes"]:
        walk(g, root, ident, flat)

    lo, hi, longest = aabb(g, flat)
    ws = (length / longest) if (length and longest > 1e-6) else None

    print(f"== {path} ==")
    if lo:
        print(f"   mesh AABB: min=({lo[0]:+.3f},{lo[1]:+.3f},{lo[2]:+.3f}) "
              f"max=({hi[0]:+.3f},{hi[1]:+.3f},{hi[2]:+.3f})  longest-axis={longest:.4f}"
              + (f"  ws={ws:.4f} (target length {length})" if ws else ""))
    hp = 0
    for n, M in flat:
        name = n.get("name", "")
        if not name.startswith("HP_"):
            continue
        hp += 1
        pos = (M[0][3], M[1][3], M[2][3])
        fwd = (M[0][2], M[1][2], M[2][2])
        ln = (fwd[0] ** 2 + fwd[1] ** 2 + fwd[2] ** 2) ** 0.5 or 1.0
        fwd = tuple(c / ln for c in fwd)
        line = (f"   {name:<24} pos=({pos[0]:+8.3f},{pos[1]:+8.3f},{pos[2]:+8.3f}) "
                f"fwd=({fwd[0]:+.2f},{fwd[1]:+.2f},{fwd[2]:+.2f})")
        if ws:
            wp = tuple(c * ws for c in pos)
            line += f"  world=({wp[0]:+8.3f},{wp[1]:+8.3f},{wp[2]:+8.3f})"
        print(line)
    if hp == 0:
        print("   (no HP_ nodes)")


def main():
    args = sys.argv[1:]
    length = None
    if "--length" in args:
        i = args.index("--length")
        length = float(args[i + 1])
        del args[i:i + 2]
    if not args:
        print(__doc__)
        sys.exit(1)
    target = args[0]
    if os.path.isdir(target):
        for f in sorted(os.listdir(target)):
            if f.endswith(".glb"):
                dump(os.path.join(target, f), length)
    else:
        dump(target, length)


if __name__ == "__main__":
    main()
