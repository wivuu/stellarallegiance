#!/usr/bin/env bash
# Build the generator image and produce every catalog asteroid into ./build.
# Pass extra args to override the default `all` command, e.g.:
#   ./build.sh one --seed 4242
set -euo pipefail
cd "$(dirname "$0")"

IMAGE="${IMAGE:-asteroid-gen}"
OUT="${OUT:-$PWD/build}"

# Clean out the old build artifacts, if any.
rm -rf "$OUT"

docker build -t "$IMAGE" .
mkdir -p "$OUT"

if [ "$#" -gt 0 ]; then
  docker run --rm -v "$OUT:/out" "$IMAGE" "$@" --out /out
else
  docker run --rm -v "$OUT:/out" "$IMAGE"
fi

echo "Done. Artifacts in $OUT"
