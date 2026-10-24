"""CLI for offline journal triage. `try` never calls the network."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path

from voice_switch_ai.classify import Config
from voice_switch_ai.pipeline import read_approvals, run, write_private

FORBIDDEN = {"paste", "send", "github", "issue", "push", "delete", "clipboard"}


def repo_root() -> Path:
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "scripts" / "voice-switch-ai").exists():
            return parent
    return Path.cwd()


def expand_path(value: str) -> Path:
    def repl(match: re.Match[str]) -> str:
        return os.environ.get(match.group(1), match.group(0))

    text = re.sub(r"%([^%]+)%", repl, value)
    text = os.path.expandvars(text)
    if text.startswith("~/"):
        text = str(Path.home()) + text[1:]
    return Path(text).expanduser()


def default_journal_dir() -> Path:
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "voice-switch" / "journal"
    if sys.platform == "win32":
        local = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
        return Path(local) / "voice-switch" / "journal"
    return Path.home() / ".local" / "share" / "voice-switch" / "journal"


def _as_bool(value, default: bool = False) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        lowered = value.lower()
        if lowered in {"1", "true", "yes", "on"}:
            return True
        if lowered in {"0", "false", "no", "off"}:
            return False
    return default


def load_config(path: Path | None) -> tuple[Config, list[str]]:
    warnings: list[str] = []
    if path is None:
        return Config(), warnings
    data = json.loads(path.read_text(encoding="utf-8"))
    dictation = data.get("dictation") or {}
    ai = dictation.get("ai") or {}
    raw_floor = ai.get("jevMinConfidence", None)
    floor = None if raw_floor is None else float(raw_floor)
    allowed_raw = ai.get("allowedExecutions", ["summarize", "recall"])
    allowed = []
    for item in allowed_raw:
        if str(item).lower() in FORBIDDEN:
            warnings.append(f"ai: ignored write capability {item}")
            continue
        allowed.append(str(item))
    journal_dir = dictation.get("journalDir")
    derived = ai.get("derivedDir")
    config = Config(
        cloud=_as_bool(ai.get("cloud", False)),
        jev_min_confidence=floor,
        allowed_executions=tuple(allowed),
        journal_dir=journal_dir,
        derived_dir=derived,
    )
    return config, warnings


def _hash_tree(path: Path) -> str:
    digest = hashlib.sha256()
    files = [path] if path.is_file() else sorted(path.glob("*.jsonl"))
    for file in files:
        digest.update(file.name.encode())
        digest.update(file.read_bytes())
    return digest.hexdigest()


def _journal_from(args, config: Config) -> Path:
    if getattr(args, "journal", None):
        return expand_path(args.journal)
    if config.journal_dir:
        return expand_path(config.journal_dir)
    return default_journal_dir()


def _out_from(args, config: Config) -> Path:
    if getattr(args, "out", None):
        return expand_path(args.out)
    if config.derived_dir:
        return expand_path(config.derived_dir)
    journal = _journal_from(args, config)
    parent = journal.parent if journal.suffix == ".jsonl" else journal.parent
    return parent / "ai"


def _maybe_jev(config: Config):
    if not config.cloud or config.jev_min_confidence is None:
        return None
    key = os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY")
    if not key:
        return None
    from voice_switch_ai.jev import LiveJev

    return LiveJev(key)


def _print_logs(result) -> None:
    for line in result.logs:
        print(line)
    for warning in result.warnings:
        print(warning, file=sys.stderr)


def cmd_doctor(_args) -> int:
    root = repo_root()
    fixture = root / "tests" / "fixtures" / "ai" / "journal"
    print(f"python={sys.version.split()[0]}")
    print("cloud=off")
    print("jevMinConfidence=unset")
    print(f"fixture={'yes' if fixture.is_dir() else 'no'}")
    print(f"journalDefault={default_journal_dir()}")
    print("journalDefaultMac=~/Library/Application Support/voice-switch/journal")
    print("journalDefaultWindows=%LOCALAPPDATA%\\voice-switch\\journal")
    print("ready=yes" if fixture.is_dir() else "ready=no")
    return 0 if fixture.is_dir() else 1


def cmd_triage(args) -> int:
    config, warnings = load_config(Path(args.config) if args.config else None)
    for warning in warnings:
        print(warning, file=sys.stderr)
    result = run(_journal_from(args, config), _out_from(args, config), config, jev=_maybe_jev(config))
    _print_logs(result)
    print(f"out={result.out}")
    return 0


def cmd_digest(args) -> int:
    config, warnings = load_config(Path(args.config) if args.config else None)
    for warning in warnings:
        print(warning, file=sys.stderr)
    out = _out_from(args, config)
    result = run(_journal_from(args, config), out, config, jev=_maybe_jev(config))
    _print_logs(result)
    if args.span == "daily":
        path = out / "digests" / f"{args.date}.json"
    else:
        from datetime import date as date_cls

        day = date_cls.fromisoformat(args.date)
        iso = day.isocalendar()
        path = out / "digests" / f"{iso.year}-W{iso.week:02d}.json"
    if not path.exists():
        print(f"digest: missing {path}", file=sys.stderr)
        return 1
    print(f"digest: {path}")
    print(path.read_text(encoding="utf-8"))
    return 0


def _read_jsonl(path: Path) -> list[dict]:
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def cmd_tray(args) -> int:
    out = expand_path(args.out)
    rows = _read_jsonl(out / "tray.jsonl")
    if args.tray_cmd == "list":
        print(_json(rows))
        print(f"tray: count={len(rows)}")
        return 0
    if args.tray_cmd == "show":
        for row in rows:
            if row["id"] == args.id:
                print(_json(row))
                return 0
        print("tray: missing", file=sys.stderr)
        return 1
    found = False
    for row in rows:
        if row["id"] == args.id:
            row["status"] = "approved"
            row["needsApproval"] = False
            row["externalWrite"] = False
            found = True
    if not found:
        print("tray: missing", file=sys.stderr)
        return 1
    approved = read_approvals(out)
    approved.add(args.id)
    write_private(out / "tray.jsonl", "".join(_json(row) + "\n" for row in rows))
    write_private(
        out / "approvals.jsonl",
        "".join(_json({"status": "approved", "trayId": tray_id}) + "\n" for tray_id in sorted(approved)),
    )
    print("approved locally; no write API called")
    return 0


def _json(obj) -> str:
    return json.dumps(obj, ensure_ascii=False, sort_keys=True)


def cmd_search(args) -> int:
    config, warnings = load_config(Path(args.config) if args.config else None)
    for warning in warnings:
        print(warning, file=sys.stderr)
    if not args.query.strip():
        print("search: empty query", file=sys.stderr)
        return 2
    result = run(_journal_from(args, config), _out_from(args, config), config, jev=_maybe_jev(config))
    from voice_switch_ai.pipeline import search_notes

    hits = search_notes(result.notes, args.query)
    print(_json(hits))
    print(f"search: hits={len(hits)}")
    if any(hit["conclusionChange"] for hit in hits):
        print("search: conclusion-change")
    return 0


def cmd_try(_args) -> int:
    root = repo_root()
    journal = root / "tests" / "fixtures" / "ai" / "journal"
    out = Path(tempfile.gettempdir()) / "voice-switch-ai-try"
    if out.exists():
        shutil.rmtree(out)
    before = _hash_tree(journal)
    result = run(journal, out, Config(), jev=None)
    after = _hash_tree(journal)
    print(f"out={out}")
    print("cloud=off")
    print(f"journal_unchanged={'yes' if before == after else 'no'}")
    for line in result.logs:
        print(line)
    dates = sorted({note.timestamp.date().isoformat() for note in result.notes})
    for day in dates:
        path = out / "digests" / f"{day}.json"
        body = json.loads(path.read_text(encoding="utf-8"))
        print(
            f"digest: date={day} decisions={len(body['決めたこと'])} "
            f"openLoops={len(body['openLoops'])} stuck={len(body['引っかかっていること'])}"
        )
    weeks = sorted(path.name for path in (out / "digests").glob("*-W*.json"))
    for name in weeks:
        print(f"digest: week={name[:-5]}")
    tray = _read_jsonl(out / "tray.jsonl")
    print(f"tray: count={len(tray)}")
    from voice_switch_ai.pipeline import search_notes

    hits = search_notes(result.notes, "保存先")
    print(f"search: hits={len(hits)}")
    if any(hit["conclusionChange"] for hit in hits):
        print("search: conclusion-change")
    return 0 if before == after else 1


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="voice-switch-ai")
    sub = parser.add_subparsers(dest="cmd", required=True)

    doctor = sub.add_parser("doctor")
    doctor.set_defaults(func=cmd_doctor)

    triage = sub.add_parser("triage")
    _add_paths(triage)
    triage.set_defaults(func=cmd_triage)

    digest = sub.add_parser("digest")
    digest.add_argument("span", choices=("daily", "weekly"))
    digest.add_argument("--date", required=True)
    _add_paths(digest)
    digest.set_defaults(func=cmd_digest)

    tray = sub.add_parser("tray")
    tray_sub = tray.add_subparsers(dest="tray_cmd", required=True)
    tray_list = tray_sub.add_parser("list")
    tray_list.add_argument("--out", required=True)
    tray_list.set_defaults(func=cmd_tray, tray_cmd="list")
    tray_show = tray_sub.add_parser("show")
    tray_show.add_argument("--out", required=True)
    tray_show.add_argument("--id", required=True)
    tray_show.set_defaults(func=cmd_tray, tray_cmd="show")
    tray_ok = tray_sub.add_parser("approve")
    tray_ok.add_argument("--out", required=True)
    tray_ok.add_argument("--id", required=True)
    tray_ok.set_defaults(func=cmd_tray, tray_cmd="approve")

    search = sub.add_parser("search")
    search.add_argument("query")
    _add_paths(search)
    search.set_defaults(func=cmd_search)

    trial = sub.add_parser("try")
    trial.set_defaults(func=cmd_try)
    return parser


def _add_paths(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--journal")
    parser.add_argument("--out")
    parser.add_argument("--config")


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"voice-switch-ai: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
