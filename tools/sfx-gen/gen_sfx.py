#!/usr/bin/env python3
"""Synthesize placeholder spatial-audio SFX for the wivuullegiance client.

These are deliberately crude, license-free, reproducible placeholders so the
audio system can be tested end-to-end before real assets exist. Replace the
WAVs in client/assets/audio/ later; the filenames are the contract SfxManager
loads against (keep them stable).

Stdlib only (wave, struct, math, random) — no numpy. 16-bit mono PCM.
Run:  python3 tools/sfx-gen/gen_sfx.py
"""

import math
import os
import random
import struct
import wave

SAMPLE_RATE = 44100
# Written relative to the repo root (this file lives at tools/sfx-gen/).
OUT_DIR = os.path.normpath(
    os.path.join(os.path.dirname(__file__), "..", "..", "client", "assets", "audio")
)


# ---- primitive generators (each returns a list of float samples in [-1, 1]) ----

def _n(seconds):
    return int(seconds * SAMPLE_RATE)


def silence(seconds):
    return [0.0] * _n(seconds)


def sine(freq, seconds, amp=1.0, phase=0.0):
    return [amp * math.sin(2 * math.pi * freq * (i / SAMPLE_RATE) + phase)
            for i in range(_n(seconds))]


def saw(freq, seconds, amp=1.0):
    out = []
    for i in range(_n(seconds)):
        t = (i / SAMPLE_RATE) * freq
        out.append(amp * (2.0 * (t - math.floor(t + 0.5))))
    return out


def noise(seconds, amp=1.0):
    return [amp * (random.random() * 2.0 - 1.0) for _ in range(_n(seconds))]


def mix(*tracks):
    """Sum equal-length (or ragged) tracks sample-wise."""
    n = max(len(t) for t in tracks)
    out = [0.0] * n
    for t in tracks:
        for i, s in enumerate(t):
            out[i] += s
    return out


def apply_env(samples, attack=0.01, release=0.1, sustain=1.0):
    """Linear attack / sustain / linear release amplitude envelope."""
    n = len(samples)
    a = min(_n(attack), n)
    r = min(_n(release), n - a)
    out = list(samples)
    for i in range(a):
        out[i] *= (i / a) if a else 1.0
    for i in range(r):
        out[n - 1 - i] *= (i / r) if r else 1.0
    if sustain != 1.0:
        for i in range(a, n - r):
            out[i] *= sustain
    return out


def exp_decay(samples, tau):
    """Multiply by an exponential decay e^(-t/tau)."""
    return [s * math.exp(-(i / SAMPLE_RATE) / tau) for i, s in enumerate(samples)]


def lowpass(samples, alpha=0.15):
    """One-pole IIR low-pass to take the harsh edge off raw noise."""
    out = []
    prev = 0.0
    for s in samples:
        prev = prev + alpha * (s - prev)
        out.append(prev)
    return out


def normalize(samples, peak=0.9):
    m = max((abs(s) for s in samples), default=0.0)
    if m == 0:
        return samples
    g = peak / m
    return [s * g for s in samples]


def write_wav(name, samples):
    samples = normalize(samples)
    path = os.path.join(OUT_DIR, name)
    with wave.open(path, "w") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SAMPLE_RATE)
        frames = bytearray()
        for s in samples:
            v = int(max(-1.0, min(1.0, s)) * 32767)
            frames += struct.pack("<h", v)
        w.writeframesraw(bytes(frames))
    print(f"  {name:<20} {len(samples) / SAMPLE_RATE:5.2f}s")


# ---- per-category placeholder timbres -----------------------------------------

def ambient_hum():
    # Quiet deep bed; LFO-swelled low sines. Designed to loop seamlessly (4s).
    base = mix(sine(58, 4.0, 0.5), sine(87, 4.0, 0.25), sine(116, 4.0, 0.12))
    lfo = sine(0.25, 4.0, 0.5)  # 1 full cycle/4s -> loop-safe
    return [s * (0.6 + 0.4 * (0.5 + 0.5 * l)) for s, l in zip(base, lfo)]


def engine_loop():
    # Tonal hum w/ harmonics; loops over 2.0s (all freqs integer cycles/2s).
    return mix(saw(120, 2.0, 0.5), sine(240, 2.0, 0.2), sine(60, 2.0, 0.3))


def booster_loop():
    # Airy whoosh: filtered noise + a low driving tone. 2.0s loop.
    return mix(lowpass(noise(2.0, 0.9), 0.05), sine(90, 2.0, 0.25), saw(45, 2.0, 0.2))


def weapon_fire():
    # Descending "pew": sine sweep 1400->300 Hz + a transient click.
    n = _n(0.25)
    out = []
    for i in range(n):
        t = i / SAMPLE_RATE
        f = 1400 - (1100 * (i / n))
        out.append(0.8 * math.sin(2 * math.pi * f * t))
    click = noise(0.008, 0.6)
    return apply_env(mix(out, click + [0.0] * (n - len(click))), attack=0.001, release=0.18)


def explosion():
    # Noise burst with low boom, exponential tail (~0.9s).
    body = mix(exp_decay(lowpass(noise(0.9, 1.0), 0.25), 0.22),
               exp_decay(sine(70, 0.9, 0.8), 0.3),
               exp_decay(sine(45, 0.9, 0.6), 0.35))
    return apply_env(body, attack=0.003, release=0.2)


def impact():
    # Short bright tick: brief band-ish noise + small ping.
    return apply_env(mix(noise(0.12, 0.8), sine(900, 0.12, 0.3)),
                     attack=0.001, release=0.09)


def ui_click():
    return apply_env(sine(1200, 0.05, 0.7), attack=0.002, release=0.04)


def ui_notify():
    # Two-tone chime.
    a = apply_env(sine(880, 0.15, 0.6), attack=0.005, release=0.12)
    b = apply_env(sine(1320, 0.18, 0.6), attack=0.005, release=0.14)
    return a + b


def menu_open():
    # Rising blip 500->1000 Hz.
    n = _n(0.15)
    out = [0.6 * math.sin(2 * math.pi * (500 + 500 * (i / n)) * (i / SAMPLE_RATE))
           for i in range(n)]
    return apply_env(out, attack=0.005, release=0.1)


def menu_close():
    # Falling blip 1000->500 Hz.
    n = _n(0.15)
    out = [0.6 * math.sin(2 * math.pi * (1000 - 500 * (i / n)) * (i / SAMPLE_RATE))
           for i in range(n)]
    return apply_env(out, attack=0.005, release=0.1)


def collision_thud():
    # Low dull impact (generated now; trigger stays stubbed in client).
    return apply_env(mix(exp_decay(sine(80, 0.4, 0.9), 0.12),
                         exp_decay(lowpass(noise(0.4, 0.5), 0.1), 0.1)),
                     attack=0.002, release=0.18)


SOUNDS = {
    "ambient_hum.wav": ambient_hum,
    "engine_loop.wav": engine_loop,
    "booster_loop.wav": booster_loop,
    "weapon_fire.wav": weapon_fire,
    "explosion.wav": explosion,
    "impact.wav": impact,
    "ui_click.wav": ui_click,
    "ui_notify.wav": ui_notify,
    "menu_open.wav": menu_open,
    "menu_close.wav": menu_close,
    "collision_thud.wav": collision_thud,
}


def main():
    random.seed(1)  # reproducible noise
    os.makedirs(OUT_DIR, exist_ok=True)
    print(f"Writing placeholder SFX -> {OUT_DIR}")
    for name, fn in SOUNDS.items():
        write_wav(name, fn())
    print(f"Done: {len(SOUNDS)} files.")


if __name__ == "__main__":
    main()
