// asteroid.scad — reusable, deterministic asteroid geometry.
//
// This library contains NO randomness. The shape is fully described by explicit
// parameters (generated from a seed in Python, see shapefield.py) so the mesh here
// and the baked normal map cannot drift apart. The radial field mirrors
// shapefield.radius() exactly:
//
//   r(u) = R0 * ( 1 + sum_i amp_i*exp(sharp_i*(u·L_i - 1))      // low-freq lobes
//                   + sum_j amp_j*sin(F_j·u + phase_j) )         // high-freq detail
//
//   P(u) = radius * r(u) * u          (star-shaped: radius as a function of direction)
//
// Use it from a generated wrapper:
//   use <asteroid.scad>
//   asteroid(radius=20, lobes=[[lx,ly,lz,amp,sharp], ...],
//            details=[[fx,fy,fz,phase,amp], ...], nlat=48, nlon=96);

PI_ = 3.14159265358979;

// --- radial field -----------------------------------------------------------

function _sum(v, i = 0) = i >= len(v) ? 0 : v[i] + _sum(v, i + 1);

// lobe term: amp * exp(sharp * (u·L - 1))
function _lobe(u, l) = l[3] * exp(l[4] * ([l[0], l[1], l[2]] * u - 1));

// detail term: amp * sin(F·u + phase)   (OpenSCAD sin() is in degrees -> convert)
function _detail(u, d) = d[4] * sin(([d[0], d[1], d[2]] * u + d[3]) * 180 / PI_);

function asteroid_radius(u, R0, lobes, details) =
    R0 * (1
          + _sum([for (l = lobes) _lobe(u, l)])
          + _sum([for (d = details) _detail(u, d)]));

// unit direction from latitude/longitude in degrees (y-up, matches shapefield.py)
function _dir(lat, lon) = [cos(lat) * cos(lon), sin(lat), cos(lat) * sin(lon)];

// --- mesh --------------------------------------------------------------------

// Builds a watertight UV-sphere polyhedron with pole fans. No UVs (STL carries
// none); the GLB exporter handles UVs/normals separately from the same field.
module asteroid(radius = 1, R0 = 1, lobes = [], details = [], nlat = 48, nlon = 96) {
    function P(lat, lon) =
        let (u = _dir(lat, lon))
        radius * asteroid_radius(u, R0, lobes, details) * u;

    // interior ring latitudes (poles handled separately)
    ring_lats = [for (i = [0:nlat - 1]) -90 + (i + 1) * 180 / (nlat + 1)];
    lons = [for (j = [0:nlon - 1]) j * 360 / nlon];

    north = P(90, 0);
    south = P(-90, 0);

    ring_pts = [for (lat = ring_lats) for (lon = lons) P(lat, lon)];
    points = concat([north, south], ring_pts);

    // index helpers: north=0, south=1, ring vertex = 2 + i*nlon + j
    function vi(i, j) = 2 + i * nlon + (j % nlon);

    // north pole fan (top ring is i = nlat-1)
    top = nlat - 1;
    north_faces = [for (j = [0:nlon - 1]) [0, vi(top, j), vi(top, j + 1)]];

    // south pole fan (bottom ring is i = 0)
    south_faces = [for (j = [0:nlon - 1]) [1, vi(0, j + 1), vi(0, j)]];

    // quad strips between adjacent rings, each split into two triangles
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

// --- demo (only runs when this file is opened directly, not when `use`d) ------
asteroid(radius = 20,
         lobes = [[0, 1, 0, 0.30, 3.0], [1, 0, 0, 0.22, 4.0], [0, 0, 1, 0.18, 5.0]],
         details = [[5, 2, 1, 0.0, 0.05], [2, 6, 3, 1.5, 0.04], [3, 1, 7, 3.0, 0.03]],
         nlat = 48, nlon = 96);
