#!/usr/bin/env python3
"""External Haas-ja-v1.0 body transcriber (GPU side).

Reads a 16 kHz mono WAV, prints plain UTF-8 transcript on stdout, exit 0.
Library logs stay on stderr. haas_asr is imported inside main so --help
works without the dependency installed.
"""
from __future__ import annotations

import argparse
import contextlib
import json
import os
import sys
from pathlib import Path


DEFAULT_MODEL = "third-intelligence/Haas-ja-v1.0"
LONGFORM_SECONDS = 30


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Transcribe a 16 kHz mono WAV with Haas-ja-v1.0; prints only the transcript on stdout."
    )
    parser.add_argument("wav", help="input WAV path (16 kHz mono PCM16)")
    parser.add_argument("--vocab", default=None, help="optional vocabulary JSON path (object of word to readings)")
    parser.add_argument("--model", default=DEFAULT_MODEL, help=f"model id or .nemo path (default {DEFAULT_MODEL})")
    return parser


def cached_nemo() -> Path:
    root = os.environ.get("HAAS_MODEL_DIR")
    if root:
        return Path(root) / "Haas-ja-v1.0.nemo"
    return Path.home() / ".cache/voice-switch/models/Haas-ja-v1.0/Haas-ja-v1.0.nemo"


def resolve_model(requested: str) -> str:
    # make haas-model stores the repo here. load_model takes a .nemo file or a hub id.
    if requested == DEFAULT_MODEL:
        local = cached_nemo()
        if local.is_file():
            return str(local)
    return requested


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        import haas_asr
    except ImportError:
        print(
            "error: haas_asr is not installed; run `make haas-setup` or "
            'pip install "haas-asr @ git+https://github.com/third-intelligence/Haas-ASR.git"',
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
    model_ref = resolve_model(args.model)
    # Keep library chatter off stdout; the transcript is the only stdout content.
    try:
        with contextlib.redirect_stdout(sys.stderr):
            model = haas_asr.load_model(model_ref)
            audio = haas_asr.load_audio(args.wav)
            if audio.shape[0] > LONGFORM_SECONDS * haas_asr.SAMPLE_RATE:
                text = haas_asr.transcribe_longform(model, audio, batch_size=64, vocabulary=vocab)
            else:
                text = haas_asr.transcribe(model, [audio], vocabulary=vocab)[0]
    except FileNotFoundError as exc:
        missing = exc.filename or args.wav
        print(f"error: file not found: {missing}", file=sys.stderr)
        return 1
    except Exception as exc:
        print(f"error: transcription failed: {exc}", file=sys.stderr)
        return 1
    if not isinstance(text, str):
        text = str(text)
    text = text.strip()
    if not text:
        print("error: empty transcript", file=sys.stderr)
        return 1
    sys.stdout.buffer.write(text.encode("utf-8") + b"\n")
    sys.stdout.buffer.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
