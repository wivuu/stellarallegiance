#!/usr/bin/env python3
"""
Extract ship hull movement stats from an Allegiance static-core .igc file.

The .igc static core format (see LoadIGCStaticCore / LoadIGCFile / CmissionIGC::Import):

    [int32 version][int32 datasize][ datasize bytes of chunks ]

Each chunk:

    [int16 ObjectType][int32 size][ size bytes of object data ]

ObjectType 29 == OT_hullType, whose payload is a DataHullTypeIGC struct
(inherits DataBuyableIGC). The struct is NOT inside a #pragma pack(1) region,
so it uses default 4-byte-max alignment. Byte offsets derived from igc.h:

  DataBuyableIGC (base):
    price            int32     @  0
    timeToBuild      uint32    @  4
    modelName        char[14]  @  8   (c_cbFileName+1)
    iconName         char[13]  @ 22   (c_cbFileName)
    name             char[25]  @ 35   (c_cbName)
    description      char[201] @ 60   (c_cbDescription)
    groupID          int8      @261
    ttbmRequired     uint8[50] @262   (TLargeBitMask<400>)
    ttbmEffects      uint8[50] @312
    -> base ends @362, padded to 4 -> sizeof = 364

  DataHullTypeIGC (derived, starts @364):
    mass             float @364
    signature        float @368
    speed            float @372   (maxSpeed)
    maxTurnRates[3]  float @376,380,384   (yaw,pitch,roll; radians/s)
    turnTorques[3]   float @388,392,396   (yaw,pitch,roll)
    thrust           float @400
    sideMultiplier   float @404
    backMultiplier   float @408
    scannerRange     float @412
    maxFuel          float @416
    ecm              float @420
    length           float @424
    maxEnergy        float @428
    rechargeRate     float @432
    ripcordSpeed     float @436
    ripcordCost      float @440

Usage:  python3 extract_hulls.py <core.igc> [--csv out.csv]
"""

import struct
import sys
import math

OT_HULLTYPE = 29

# field name -> byte offset within the hull chunk payload
FLOAT_FIELDS = [
    ("mass", 364), ("signature", 368), ("speed", 372),
    ("rate_yaw", 376), ("rate_pitch", 380), ("rate_roll", 384),
    ("torque_yaw", 388), ("torque_pitch", 392), ("torque_roll", 396),
    ("thrust", 400), ("side_mult", 404), ("back_mult", 408),
    ("scanner_range", 412), ("max_fuel", 416), ("ecm", 420),
    ("length", 424), ("max_energy", 428), ("recharge_rate", 432),
    ("ripcord_speed", 436), ("ripcord_cost", 440),
]
NAME_OFF, NAME_LEN = 35, 25
MODEL_OFF, MODEL_LEN = 8, 14
MIN_HULL_SIZE = 444  # must hold through ripcord_cost


def cstr(buf, off, length):
    raw = buf[off:off + length]
    z = raw.find(b"\x00")
    if z >= 0:
        raw = raw[:z]
    return raw.decode("latin-1", "replace").strip()


def find_chunk_stream(data):
    """Return (offset, length) of the chunk stream, handling the version+size header."""
    # Static core: [int32 version][int32 datasize][data...]
    if len(data) >= 8:
        ds = struct.unpack_from("<i", data, 4)[0]
        if 0 < ds <= len(data) - 8 and (len(data) - 8 - ds) in (0, 4):
            return 8, ds
    # Plain map/mission: [int32 datasize][data...]
    if len(data) >= 4:
        ds = struct.unpack_from("<i", data, 0)[0]
        if 0 < ds <= len(data) - 4 and (len(data) - 4 - ds) in (0, 4):
            return 4, ds
    # Fallback: scan whole file as chunk stream
    return 0, len(data)


def parse_hulls(path):
    data = open(path, "rb").read()
    start, length = find_chunk_stream(data)
    end = min(start + length, len(data))
    pos = start
    hulls = []
    while pos + 6 <= end:
        otype = struct.unpack_from("<h", data, pos)[0]
        size = struct.unpack_from("<i", data, pos + 2)[0]
        payload = pos + 6
        if size < 0 or payload + size > len(data):
            break  # corrupt / wrong alignment; stop
        if otype == OT_HULLTYPE and size >= MIN_HULL_SIZE:
            buf = data[payload:payload + size]
            h = {
                "name": cstr(buf, NAME_OFF, NAME_LEN),
                "model": cstr(buf, MODEL_OFF, MODEL_LEN),
                "_chunk_size": size,
            }
            for fname, foff in FLOAT_FIELDS:
                h[fname] = struct.unpack_from("<f", buf, foff)[0]
            hulls.append(h)
        pos = payload + size
    return hulls


def looks_valid(h):
    """Sanity gate so we don't emit garbage if offsets ever drift."""
    return (0 < h["mass"] < 1e6 and 0 < h["speed"] < 1e5 and
            0 <= h["rate_yaw"] < 100 and 0 <= h["thrust"] < 1e9 and
            0 < h["side_mult"] <= 4 and 0 < h["back_mult"] <= 4)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    path = sys.argv[1]
    csv_out = None
    if "--csv" in sys.argv:
        csv_out = sys.argv[sys.argv.index("--csv") + 1]

    hulls = parse_hulls(path)
    valid = [h for h in hulls if looks_valid(h)]
    print(f"Parsed {len(hulls)} hull chunks from {path} ({len(valid)} passed sanity check)\n")

    # Human-readable table: degrees/s for turn rates, derived drift angle.
    hdr = f"{'name':22} {'mass':>7} {'maxSpd':>7} {'accel':>7} {'yaw°/s':>7} {'pitch°/s':>8} {'roll°/s':>7} {'driftYaw°':>9} {'side':>5} {'back':>5}"
    print(hdr)
    print("-" * len(hdr))
    for h in sorted(valid, key=lambda x: x["name"].lower()):
        ry = math.degrees(h["rate_yaw"])
        rp = math.degrees(h["rate_pitch"])
        rr = math.degrees(h["rate_roll"])
        accel = h["thrust"] / h["mass"] if h["mass"] else 0.0
        # drift = rate^2 / (2*alpha); alpha = torque/mass (in rad). drift in degrees:
        alpha_yaw = h["torque_yaw"] / h["mass"] if h["mass"] else 0.0  # rad/s^2 (torque already *mass-derived)
        drift_yaw = math.degrees(h["rate_yaw"] ** 2 / (2 * alpha_yaw)) if alpha_yaw else 0.0
        print(f"{h['name'][:22]:22} {h['mass']:7.1f} {h['speed']:7.1f} {accel:7.1f} "
              f"{ry:7.1f} {rp:8.1f} {rr:7.1f} {drift_yaw:9.1f} {h['side_mult']:5.2f} {h['back_mult']:5.2f}")

    if csv_out:
        import csv
        cols = ["name", "model", "mass", "speed", "thrust", "rate_yaw", "rate_pitch",
                "rate_roll", "torque_yaw", "torque_pitch", "torque_roll",
                "side_mult", "back_mult", "scanner_range", "max_fuel", "ecm",
                "length", "max_energy", "recharge_rate", "ripcord_speed", "ripcord_cost"]
        with open(csv_out, "w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=cols, extrasaction="ignore")
            w.writeheader()
            for h in valid:
                w.writerow(h)
        print(f"\nWrote {len(valid)} hulls -> {csv_out}")


if __name__ == "__main__":
    main()
