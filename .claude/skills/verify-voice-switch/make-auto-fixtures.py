#!/usr/bin/env python3
"""Compose the automatic-recording gate fixtures from the TTS clips verify.sh writes.

usage: make-auto-fixtures.py <repo> <fixtures-dir>
needs <fixtures-dir>/say-body.wav and say-stop.wav (16 kHz mono PCM16 from scripts/windows-say.ps1).
"""
import math
import random
import struct
import sys
import wave
from pathlib import Path

repo, out = Path(sys.argv[1]), Path(sys.argv[2])
RATE = 16000


def read(path: Path) -> bytes:
    with wave.open(str(path)) as w:
        assert (w.getframerate(), w.getnchannels(), w.getsampwidth()) == (RATE, 1, 2), path
        return w.readframes(w.getnframes())


def silence(seconds: float) -> bytes:
    return b"\0\0" * int(RATE * seconds)


def hiss(seconds: float) -> bytes:
    # Modulated white noise: loud enough to open an automatic session, with no voice for Silero to find.
    # Plain keystroke or burst clips never open a session on Windows, so they cannot reach the gate.
    rng = random.Random(1)
    samples = (rng.gauss(0, 6000) * (0.5 + 0.5 * math.sin(i / RATE * 2 * math.pi * 3)) for i in range(int(RATE * seconds)))
    return b"".join(struct.pack("<h", max(-32768, min(32767, int(x)))) for x in samples)


body, stop = read(out / "say-body.wav"), read(out / "say-stop.wav")
gate = repo / "tests/parity/fixtures/speech_gate"
fixtures = {
    # Silero hears the はい, but both recognizers write at most はい (2 characters), under the 3 the gate needs.
    "auto-backchannel": [silence(1), read(gate / "noise-burst.wav"), read(gate / "backchannel-hai.wav"), read(gate / "noise-keystrokes.wav"), silence(2.5)],
    "auto-hiss": [silence(1), hiss(3), silence(2.5)],
    "auto-speech": [silence(1), body, silence(2.5)],
    "auto-stop": [silence(1), body, silence(0.3), stop, silence(2.5)],
}
for name, parts in fixtures.items():
    with wave.open(str(out / f"{name}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(parts))
    print(f"{name}.wav")
