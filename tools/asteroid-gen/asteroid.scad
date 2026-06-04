// asteroid.scad — reusable, deterministic asteroid geometry (multiple kinds).
//
// This library contains NO randomness. The shape is fully described by explicit
// parameters (generated from a seed in Python, see shapefield.py). Only the BASE
// silhouette is mirrored here; the high-frequency detail (rocks/roughness) lives only
// in the normal map, so the mesh and map cannot drift apart.
//
// Base radial field by kind (P(u) = radius * r(u) * u, star-shaped):
//   "bulbous"     r = R0*(1 + sum_i amp_i*exp(sharp_i*(u·L_i - 1)))     // smooth lobes
//   "crystalline" r = min_i d_i / max(u·N_i, eps)                        // convex facets
//   "angular"     r = crystalline - sum_k amp_k*exp(sharp_k*(u·C_k - 1)) // facets w/ gouges
//
// Use from a generated wrapper:
//   use <asteroid.scad>
//   asteroid(kind="crystalline", radius=20, planes=[[nx,ny,nz,d], ...], nlat=64, nlon=128);

EPS = 1e-3;

// --- helpers -----------------------------------------------------------------

function _sum(v, i = 0) = i >= len(v) ? 0 : v[i] + _sum(v, i + 1);

// Gaussian bump: amp * exp(sharp * (u·C - 1))   (b = [Cx,Cy,Cz,amp,sharp])
function _bump(u, b) = b[3] * exp(b[4] * ([b[0], b[1], b[2]] * u - 1));

// convex faceted support: min_i d_i / max(u·N_i, eps)   (p = [Nx,Ny,Nz,d])
function _crystal(u, planes) =
    min([for (p = planes) p[3] / max([p[0], p[1], p[2]] * u, EPS)]);

function _rbase(u, kind, R0, lobes, planes, gouges) =
    kind == "crystalline" ? _crystal(u, planes)
  : kind == "angular"     ? _crystal(u, planes) - _sum([for (g = gouges) _bump(u, g)])
  : /* bulbous */           R0 * (1 + _sum([for (l = lobes) _bump(u, l)]));

// unit direction from latitude/longitude in degrees (y-up, matches shapefield.py)
function _dir(lat, lon) = [cos(lat) * cos(lon), sin(lat), cos(lat) * sin(lon)];

// --- mesh --------------------------------------------------------------------

// Watertight UV-sphere polyhedron with pole fans. No UVs (STL carries none); the GLB
// exporter handles UVs/normals separately from the same base field.
module asteroid(kind = "bulbous", radius = 1, R0 = 1,
                lobes = [], planes = [], gouges = [], nlat = 64, nlon = 128) {
    function P(lat, lon) =
        let (u = _dir(lat, lon))
        radius * _rbase(u, kind, R0, lobes, planes, gouges) * u;

    ring_lats = [for (i = [0:nlat - 1]) -90 + (i + 1) * 180 / (nlat + 1)];
    lons = [for (j = [0:nlon - 1]) j * 360 / nlon];

    ring_pts = [for (lat = ring_lats) for (lon = lons) P(lat, lon)];
    points = concat([P(90, 0), P(-90, 0)], ring_pts);   // north=0, south=1

    function vi(i, j) = 2 + i * nlon + (j % nlon);
    top = nlat - 1;

    north_faces = [for (j = [0:nlon - 1]) [0, vi(top, j), vi(top, j + 1)]];
    south_faces = [for (j = [0:nlon - 1]) [1, vi(0, j + 1), vi(0, j)]];
    strip_faces = [
        for (i = [0:nlat - 2])
            for (j = [0:nlon - 1])
                each [
                    [vi(i, j), vi(i, j + 1), vi(i + 1, j)],
                    [vi(i, j + 1), vi(i + 1, j + 1), vi(i + 1, j)],
                ]
    ];

    polyhedron(points = points,
               faces = concat(north_faces, south_faces, strip_faces),
               convexity = 6);
}

// --- demo (only runs when opened directly, not when `use`d) -------------------
asteroid(kind = "crystalline", radius = 20,
         planes = [[1,0,0,1.0],[-1,0,0,0.95],[0,1,0,1.05],[0,-1,0,0.9],
                   [0,0,1,1.0],[0,0,-1,0.92],[0.7,0.7,0,0.85],[-0.6,0.5,0.6,0.8],
                   [0.4,-0.7,0.6,0.88],[-0.5,-0.5,-0.7,0.83]],
         nlat = 64, nlon = 128);
