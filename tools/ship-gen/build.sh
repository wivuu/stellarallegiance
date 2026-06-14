#!/usr/bin/env bash
# Build the generator image and produce every catalog ship into ./build.
# Pass extra args to override the default `build ships.yaml` command, e.g.:
#   ./build.sh generate --seed 7 --count 20
set -euo pipefail
cd "$(dirname "$0")"

IMAGE="${IMAGE:-ship-gen}"
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
