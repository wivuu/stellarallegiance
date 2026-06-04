# Headless asteroid generator (pure Python / uv: bake textures + assemble GLB).
# Build:  docker build -t asteroid-gen .
# Run:    docker run --rm -v "$PWD/build:/out" asteroid-gen   # generates the catalog
FROM python:3.12-slim

# uv (pinned image) provides reproducible Python dependency resolution.
COPY --from=ghcr.io/astral-sh/uv:0.5 /uv /uvx /bin/

WORKDIR /app
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev

# Source (rarely-changing layers last for cache friendliness).
COPY shapefield.py bake.py glb.py generate.py asteroids.json ./

ENV PATH="/app/.venv/bin:$PATH"

# Default: generate the whole catalog into the mounted /out volume.
ENTRYPOINT ["uv", "run", "generate.py"]
CMD ["all", "--out", "/out"]
