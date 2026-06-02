#!/usr/bin/env bash
# Copies the canonical shared/FlightModel.cs VERBATIM into the module and client.
# The three files must stay byte-for-byte identical (T3 acceptance gate).
# Edit shared/FlightModel.cs, then run this. Never edit a copy directly.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/shared/FlightModel.cs"
DEST_MODULE="$ROOT/module/spacetimedb/FlightModel.cs"
DEST_CLIENT="$ROOT/client/scripts/FlightModel.cs"

cp "$SRC" "$DEST_MODULE"
cp "$SRC" "$DEST_CLIENT"

# Verify identical (defensive — cp should guarantee it).
if diff -q "$SRC" "$DEST_MODULE" >/dev/null && diff -q "$SRC" "$DEST_CLIENT" >/dev/null; then
    echo "synced: FlightModel.cs is identical in shared/, module/, client/"
else
    echo "ERROR: copies differ after sync" >&2
    exit 1
fi
