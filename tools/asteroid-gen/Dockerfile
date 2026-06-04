# Headless asteroid generator: OpenSCAD (geometry) + Python/uv (bake + GLB).
# Build:  docker build -t asteroid-gen .
# Run:    docker run --rm -v "$PWD/build:/out" asteroid-gen   # generates the catalog
FROM python:3.12-slim

# OpenSCAD for STL export. STL export uses CGAL (no GL/display needed), so this
# runs headless with no Xvfb.
RUN apt-get update \
    && apt-get install -y --no-install-recommends openscad \
    && rm -rf /var/lib/apt/lists/*

# uv (pinned image) provides reproducible Python dependency resolution.
COPY --from=ghcr.io/astral-sh/uv:0.5 /uv /uvx /bin/

WORKDIR /app
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev

# Source (rarely-changing layers last for cache friendliness).
COPY shapefield.py bake_normals.py glb.py generate.py asteroid.scad asteroids.json ./

ENV PATH="/app/.venv/bin:$PATH"

# Default: generate the whole catalog into the mounted /out volume.
ENTRYPOINT ["uv", "run", "generate.py"]
CMD ["all", "--out", "/out"]
