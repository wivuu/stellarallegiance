"""Modular parametric part primitives for the spaceship generator.

Every generator returns a local-space mesh dict::

    {"pos": (N,3) f32, "nrm": (N,3) f32, "uv": (N,2) f32, "faces": (M,3) u32}

built in the game's convention: **local +Z forward, +Y up, right-handed**, ~1 unit ≈ 1 m.
A part is centered on its own origin; ``place()`` then translates/rotates/scales (and
optionally mirrors across X) it into ship-local space, baking the transform into the verts
so the final GLB needs no nested node transforms for geometry.

UVs are emitted in world-unit space (``DENSITY`` repeats per unit) so the tileable per-kind
PBR textures (see ``bake.py``) keep a uniform texel density across differently-sized parts.

Part types: ``box``, ``taper`` (frustum / nose), ``cylinder`` (capped tube or cone),
``ellipsoid`` (cockpit / pod), ``wedge`` (fin / wing).
"""

from __future__ import annotations

import math

import numpy as np

# Texture repeats per world unit. Lower = larger panels. Shared by every part so a hull
# plate and an engine nacelle show the same texel scale.
DENSITY = 0.5


# ---------------------------------------------------------------------------
# Mesh accumulator
# ---------------------------------------------------------------------------

class _Mesh:
    def __init__(self) -> None:
        self.pos: list[list[float]] = []
        self.nrm: list[list[float]] = []
        self.uv: list[list[float]] = []
        self.faces: list[tuple[int, int, int]] = []

    def quad(self, p0, p1, p2, p3, normal=None) -> None:
        """Add a planar quad p0->p1->p2->p3 (CCW). If ``normal`` is None it is derived
        from the corners and auto-oriented to point away from the part origin (works for
        convex hull pieces centered on their origin)."""
        p0, p1, p2, p3 = (np.asarray(p, np.float64) for p in (p0, p1, p2, p3))
        if normal is None:
            n = np.cross(p1 - p0, p3 - p0)
            ln = np.linalg.norm(n)
            n = n / ln if ln > 1e-12 else np.array([0.0, 0.0, 1.0])
            centroid = (p0 + p1 + p2 + p3) * 0.25
            if np.dot(n, centroid) < 0.0:  # face the normal outward from origin
                p1, p3 = p3, p1
                n = -n
        else:
            n = np.asarray(normal, np.float64)
            n = n / (np.linalg.norm(n) or 1.0)

        # UV from edge lengths in world units (u along p0->p1, v along p0->p3).
        du = float(np.linalg.norm(p1 - p0)) * DENSITY
        dv = float(np.linalg.norm(p3 - p0)) * DENSITY
        uvs = [(0.0, 0.0), (du, 0.0), (du, dv), (0.0, dv)]

        base = len(self.pos)
        for p, uv in zip((p0, p1, p2, p3), uvs):
            self.pos.append([float(p[0]), float(p[1]), float(p[2])])
            self.nrm.append([float(n[0]), float(n[1]), float(n[2])])
            self.uv.append(list(uv))
        self.faces.append((base, base + 1, base + 2))
        self.faces.append((base, base + 2, base + 3))

    def tri(self, p0, p1, p2, normal, uv0, uv1, uv2) -> None:
        n = np.asarray(normal, np.float64)
        n = n / (np.linalg.norm(n) or 1.0)
        base = len(self.pos)
        for p, uv in zip((p0, p1, p2), (uv0, uv1, uv2)):
            self.pos.append([float(p[0]), float(p[1]), float(p[2])])
            self.nrm.append([float(n[0]), float(n[1]), float(n[2])])
            self.uv.append([float(uv[0]), float(uv[1])])
        self.faces.append((base, base + 1, base + 2))

    def result(self) -> dict:
        return {
            "pos": np.asarray(self.pos, np.float32).reshape(-1, 3),
            "nrm": np.asarray(self.nrm, np.float32).reshape(-1, 3),
            "uv": np.asarray(self.uv, np.float32).reshape(-1, 2),
            "faces": np.asarray(self.faces, np.uint32).reshape(-1, 3),
        }


# ---------------------------------------------------------------------------
# Primitive generators
# ---------------------------------------------------------------------------

def _box_corners(hx, hy, hz, fx, fy):
    """8 corners of a (possibly front-tapered) box: back half-extents (hx,hy) at z=-hz,
    front half-extents (fx,fy) at z=+hz."""
    return {
        "B00": (-hx, -hy, -hz), "B10": (hx, -hy, -hz), "B11": (hx, hy, -hz), "B01": (-hx, hy, -hz),
        "F00": (-fx, -fy, hz), "F10": (fx, -fy, hz), "F11": (fx, fy, hz), "F01": (-fx, fy, hz),
    }


def _box_like(size, taper=(1.0, 1.0)) -> dict:
    sx, sy, sz = size
    hx, hy, hz = sx / 2, sy / 2, sz / 2
    fx, fy = hx * taper[0], hy * taper[1]
    c = _box_corners(hx, hy, hz, fx, fy)
    m = _Mesh()
    eps = 1e-5
    if fx > eps and fy > eps:
        m.quad(c["F00"], c["F10"], c["F11"], c["F01"])      # +Z front
    m.quad(c["B10"], c["B00"], c["B01"], c["B11"])          # -Z back
    m.quad(c["B01"], c["B11"], c["F11"], c["F01"])          # +Y top
    m.quad(c["B00"], c["B10"], c["F10"], c["F00"])          # -Y bottom
    m.quad(c["B10"], c["B11"], c["F11"], c["F10"])          # +X right
    m.quad(c["B00"], c["F00"], c["F01"], c["B01"])          # -X left
    return m.result()


def box(size, **_) -> dict:
    """A rectangular box, size [x, y, z]."""
    return _box_like(size)


def taper(size, taper=(0.4, 0.4), **_) -> dict:
    """A frustum: a box whose +Z (front) end is scaled by ``taper`` [tx, ty]. ``[0,0]``
    pinches to a point (a nose cone); ``[1,1]`` is a plain box."""
    return _box_like(size, taper=(float(taper[0]), float(taper[1])))


def cylinder(radius=1.0, length=2.0, taper=1.0, segments=16, **_) -> dict:
    """A capped tube along Z (radius ``radius`` at the back), front radius = radius*taper.
    ``taper=0`` makes a cone with the tip pointing +Z (nose / Scout silhouette)."""
    seg = max(3, int(segments))
    L = float(length)
    r_back = float(radius)
    r_front = r_back * float(taper)
    hz = L / 2
    rprime = (r_front - r_back) / L if L > 1e-9 else 0.0
    eps = 1e-4
    m = _Mesh()

    ang = np.linspace(0.0, 2 * math.pi, seg + 1)
    circ = 2 * math.pi * max(r_back, r_front)
    for i in range(seg):
        a0, a1 = ang[i], ang[i + 1]
        c0, s0 = math.cos(a0), math.sin(a0)
        c1, s1 = math.cos(a1), math.sin(a1)
        # back ring radius slightly nudged off zero so cone-tip normals stay defined.
        rb = max(r_back, eps)
        rf = max(r_front, eps)
        b0 = (rb * c0, rb * s0, -hz)
        b1 = (rb * c1, rb * s1, -hz)
        f1 = (rf * c1, rf * s1, hz)
        f0 = (rf * c0, rf * s0, hz)
        nb0 = _norm((c0, s0, -rprime)); nb1 = _norm((c1, s1, -rprime))
        u0 = (i / seg) * circ * DENSITY
        u1 = ((i + 1) / seg) * circ * DENSITY
        v0, v1 = 0.0, L * DENSITY
        m.tri(b0, b1, f1, nb0, (u0, v0), (u1, v0), (u1, v1))
        m.tri(b0, f1, f0, nb0, (u0, v0), (u1, v1), (u0, v1))

    if r_back > eps:                                        # back cap (-Z)
        for i in range(seg):
            a0, a1 = ang[i], ang[i + 1]
            p0 = (r_back * math.cos(a0), r_back * math.sin(a0), -hz)
            p1 = (r_back * math.cos(a1), r_back * math.sin(a1), -hz)
            ctr = (0.0, 0.0, -hz)
            m.tri(ctr, p1, p0, (0, 0, -1),
                  (0.5, 0.5),
                  (0.5 + 0.5 * math.cos(a1), 0.5 + 0.5 * math.sin(a1)),
                  (0.5 + 0.5 * math.cos(a0), 0.5 + 0.5 * math.sin(a0)))
    if r_front > eps:                                       # front cap (+Z)
        for i in range(seg):
            a0, a1 = ang[i], ang[i + 1]
            p0 = (r_front * math.cos(a0), r_front * math.sin(a0), hz)
            p1 = (r_front * math.cos(a1), r_front * math.sin(a1), hz)
            ctr = (0.0, 0.0, hz)
            m.tri(ctr, p0, p1, (0, 0, 1),
                  (0.5, 0.5),
                  (0.5 + 0.5 * math.cos(a0), 0.5 + 0.5 * math.sin(a0)),
                  (0.5 + 0.5 * math.cos(a1), 0.5 + 0.5 * math.sin(a1)))
    return m.result()


def ellipsoid(size=(1.0, 1.0, 1.0), segments=20, rings=12, **_) -> dict:
    """A sphere scaled to half-extents ``size`` [x, y, z] (cockpit canopy / escape pod)."""
    sx, sy, sz = (float(s) for s in size)
    nlon = max(3, int(segments))
    nlat = max(2, int(rings))
    lat = np.linspace(-math.pi / 2, math.pi / 2, nlat + 1)
    lon = np.linspace(0.0, 2 * math.pi, nlon + 1)

    def vert(i, j):
        ct, st = math.cos(lat[i]), math.sin(lat[i])
        cp, sp = math.cos(lon[j]), math.sin(lon[j])
        d = (ct * cp, st, ct * sp)
        p = (sx * d[0], sy * d[1], sz * d[2])
        n = _norm((d[0] / sx, d[1] / sy, d[2] / sz))
        uv = (j / nlon, (lat[i] + math.pi / 2) / math.pi)
        return p, n, uv

    m = _Mesh()
    for i in range(nlat):
        for j in range(nlon):
            p00, n00, uv00 = vert(i, j)
            p10, n10, uv10 = vert(i + 1, j)
            p11, n11, uv11 = vert(i + 1, j + 1)
            p01, n01, uv01 = vert(i, j + 1)
            m.tri(p00, p01, p11, n00, uv00, uv01, uv11)
            m.tri(p00, p11, p10, n00, uv00, uv11, uv10)
    return m.result()


def wedge(size=(0.2, 1.0, 2.0), **_) -> dict:
    """A right-triangular fin: thin along X (thickness size[0]), rising to height size[1]
    at the back, sloping to nothing at the +Z front over length size[2]. Rotate/mirror in
    placement to make tail fins or swept wings."""
    t, H, L = (float(s) for s in size)
    hx, hz = t / 2, L / 2
    # Triangle in the Z-Y plane (right angle at back-bottom): back-bottom, front-bottom, back-top.
    A = (-hz, 0.0); B = (hz, 0.0); C = (-hz, H)  # (z, y)
    m = _Mesh()
    # two triangular side faces (±X)
    pr = lambda x, zy: (x, zy[1], zy[0])
    m.tri(pr(hx, A), pr(hx, B), pr(hx, C), (1, 0, 0), (A[0] * DENSITY, A[1] * DENSITY),
          (B[0] * DENSITY, B[1] * DENSITY), (C[0] * DENSITY, C[1] * DENSITY))
    m.tri(pr(-hx, A), pr(-hx, C), pr(-hx, B), (-1, 0, 0), (A[0] * DENSITY, A[1] * DENSITY),
          (C[0] * DENSITY, C[1] * DENSITY), (B[0] * DENSITY, B[1] * DENSITY))
    # bottom rectangle (A-B edge, -Y)
    m.quad(pr(-hx, A), pr(hx, A), pr(hx, B), pr(-hx, B), normal=(0, -1, 0))
    # back rectangle (A-C edge, -Z)
    m.quad(pr(-hx, A), pr(-hx, C), pr(hx, C), pr(hx, A), normal=(0, 0, -1))
    # hypotenuse rectangle (B-C edge), outward normal in +Z/+Y quadrant
    hyp_n = _norm((0.0, (B[0] - C[0]), (C[1] - B[1])))  # perpendicular to B->C, pointing out
    m.quad(pr(-hx, C), pr(-hx, B), pr(hx, B), pr(hx, C), normal=hyp_n)
    return m.result()


GENERATORS = {
    "box": box,
    "taper": taper,
    "cylinder": cylinder,
    "ellipsoid": ellipsoid,
    "wedge": wedge,
}


def build_part(spec: dict) -> dict:
    """Build the local-space mesh for one part spec (the keys other than placement are
    forwarded to the generator)."""
    ptype = spec["type"]
    if ptype not in GENERATORS:
        raise ValueError(f"unknown part type {ptype!r} (have {sorted(GENERATORS)})")
    kwargs = {k: v for k, v in spec.items()
              if k not in ("type", "material", "pos", "rot", "scale", "mirror")}
    return GENERATORS[ptype](**kwargs)


# ---------------------------------------------------------------------------
# Placement (transform baked into the verts)
# ---------------------------------------------------------------------------

def _euler_matrix(rot_deg) -> np.ndarray:
    rx, ry, rz = (math.radians(float(a)) for a in rot_deg)
    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]], np.float64)
    Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]], np.float64)
    Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]], np.float64)
    return Rz @ Ry @ Rx


def place(mesh: dict, pos=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1), mirror_x=False) -> dict:
    """Return a copy of ``mesh`` transformed into ship-local space. ``mirror_x`` reflects
    across X (for the +X half of a mirrored pair) and flips winding so normals stay outward."""
    R = _euler_matrix(rot)
    s = np.asarray(scale if hasattr(scale, "__len__") else (scale, scale, scale), np.float64)
    pos = np.asarray(pos, np.float64)

    pos_v = mesh["pos"].astype(np.float64) * s
    nrm_v = mesh["nrm"].astype(np.float64) / np.where(s == 0, 1, s)  # inverse-scale normals
    if mirror_x:
        pos_v[:, 0] *= -1
        nrm_v[:, 0] *= -1

    pos_v = pos_v @ R.T + pos
    nrm_v = nrm_v @ R.T
    nrm_v /= np.linalg.norm(nrm_v, axis=1, keepdims=True).clip(1e-9)

    faces = mesh["faces"]
    if mirror_x:  # reflection flips handedness -> reverse winding to keep faces front-facing
        faces = faces[:, ::-1].copy()

    return {
        "pos": pos_v.astype(np.float32),
        "nrm": nrm_v.astype(np.float32),
        "uv": mesh["uv"].copy(),
        "faces": faces.astype(np.uint32),
    }


def _norm(v):
    v = np.asarray(v, np.float64)
    n = np.linalg.norm(v)
    return v / n if n > 1e-12 else np.array([0.0, 0.0, 1.0])
