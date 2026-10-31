#!/usr/bin/env python3
"""External Haas-ja-v1.0 body transcriber (GPU side).

Reads a 16 kHz mono WAV, prints plain UTF-8 transcript on stdout, exit 0.
Library logs stay on stderr. haas_asr is imported inside main so --help
works without the dependency installed.
"""
from __future__ import annotations

import argparse
import contextlib
import io
import json
import sys
import wave


DEFAULT_MODEL = "third-intelligence/Haas-ja-v1.0"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Transcribe a 16 kHz mono WAV with Haas-ja-v1.0; prints only the transcript on stdout."
    )
    parser.add_argument("wav", help="input WAV path (16 kHz mono PCM16)")
    parser.add_argument("--vocab", default=None, help="optional vocabulary JSON path (object of word to readings)")
    parser.add_argument("--model", default=DEFAULT_MODEL, help=f"model id (default {DEFAULT_MODEL})")
    return parser


def wav_duration_seconds(path: str) -> float:
    with wave.open(path, "rb") as handle:
        frames = handle.getnframes()
        rate = handle.getframerate() or 16000
        return frames / float(rate)


def load_audio(path: str):
    # haas_asr accepts a path; keep raw bytes out of stdout. Real decode
    # happens inside haas_asr; here we just pass the path through.
    return path


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        import haas_asr  # type: ignore
    except Exception:
        print(
            "error: haas_asr is not installed; run `make haas-setup` or `pip install haas-asr`",
            file=sys.stderr,
        )
        return 1
    vocab = None
    if args.vocab:
        try:
            with open(args.vocab, encoding="utf-8") as handle:
                raw = json.load(handle)
        except Exception as exc:
            print(f"error: cannot read vocabulary JSON: {exc}", file=sys.stderr)
            return 1
        try:
            vocab = haas_asr.Vocabulary(raw)
        except Exception as exc:
            print(f"error: haas_asr.Vocabulary failed: {exc}", file=sys.stderr)
            return 1
    try:
        duration = wav_duration_seconds(args.wav)
    except FileNotFoundError:
        print(f"error: WAV not found: {args.wav}", file=sys.stderr)
        return 1
    except Exception as exc:
        print(f"error: cannot read WAV: {exc}", file=sys.stderr)
        return 1
    audio = load_audio(args.wav)
    # Keep library chatter off stdout; the transcript is the only stdout content.
    buffer = io.StringIO()
    try:
        with contextlib.redirect_stdout(sys.stderr):
            model = haas_asr.load_model(args.model)
            if duration > 30:
                text = haas_asr.transcribe_longform(model, audio, batch_size=64, vocabulary=vocab)
            else:
                text = haas_asr.transcribe(model, [audio], vocabulary=vocab)[0]
    except Exception as exc:
        print(f"error: transcription failed: {exc}", file=sys.stderr)
        return 1
    if not isinstance(text, str):
        text = str(text)
    text = text.strip()
    if not text:
        print("error: empty transcript", file=sys.stderr)
        return 1
    sys.stdout.write(text + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
