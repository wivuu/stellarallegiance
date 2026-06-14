# Headless spaceship generator (pure Python / uv: compose parts + bake textures + assemble GLB).
# Build:  docker build -t ship-gen .
# Run:    docker run --rm -v "$PWD/build:/out" ship-gen   # builds every ship in ships.yaml
FROM python:3.12-slim

# uv (pinned image) provides reproducible Python dependency resolution.
COPY --from=ghcr.io/astral-sh/uv:0.5 /uv /uvx /bin/

WORKDIR /app
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev

# Source (rarely-changing layers last for cache friendliness).
COPY parts.py bake.py glb.py generate.py ships.yaml ./

ENV PATH="/app/.venv/bin:$PATH"

# Default: build every ship in the catalog into the mounted /out volume.
ENTRYPOINT ["uv", "run", "generate.py"]
CMD ["build", "ships.yaml", "--out", "/out"]
