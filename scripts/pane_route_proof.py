#!/usr/bin/env python3
"""Closed-catalog pane route. Unique label substring wins. No Jev."""
import os
import re
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOCK = f"/tmp/voice-switch-pane-route-{os.getpid()}.sock"
FMT = "#{pane_id}\t#{window_name}\t#{pane_title}\t#{pane_current_command}"
PANE_ID = re.compile(r"^%[0-9]+$")
CASES = [
    ("nvimに移動", "exact", "%0"),
    ("shell", "exact", "%1"),
    ("logsを見て", "exact", "%2"),
    ("test", "miss", None),
    ("dev", "ambiguous", None),
    ("bash", "ambiguous", None),
]


def fail(msg):
    print(f"FAIL {msg}", file=sys.stderr)
    return 1


def tmux(args, start=False):
    cmd = ["tmux", "-S", SOCK]
    if start:
        cmd += ["-f", "/dev/null"]
    cmd += list(args)
    return subprocess.run(cmd, capture_output=True, text=True)


def catalog(text):
    panes = []
    for line in text.splitlines():
        if line == "":
            continue
        fields = line.split("\t")
        if len(fields) != 4:
            raise ValueError(f"not four fields: {line!r}")
        panes.append({
            "id": fields[0],
            "window": fields[1],
            "title": fields[2],
            "command": fields[3],
        })
    return panes


def hits(utterance, panes):
    folded = utterance.casefold()
    found = []
    for pane in panes:
        labels = [pane["title"], pane["window"], pane["command"]]
        labels = [label for label in labels if label]
        if any(label.casefold() in folded for label in labels):
            found.append(pane)
    return found


def choose(utterance, panes):
    found = hits(utterance, panes)
    if len(found) == 1:
        return "exact", found[0]["id"]
    if len(found) > 1:
        return "ambiguous", None
    return "miss", None


def check_swift():
    src = (ROOT / "Sources/voice-switch/main.swift").read_text()
    needle = 'p.arguments = ["tmux", "send-keys", "-t", id, "-l", "--", text]'
    if needle not in src:
        return fail("swift send-keys argv missing")
    if 'URL(fileURLWithPath: "/usr/bin/env")' not in src:
        return fail("swift executable is not /usr/bin/env")
    swift_fmt = '"#{pane_id}\\t#{window_name}\\t#{pane_title}\\t#{pane_current_command}"'
    if swift_fmt not in src:
        return fail("swift list-panes format missing")
    if "p.environment" in src:
        return fail("swift sets process environment")
    if "computer-use-jev" in src or "macrowhisper" in src:
        return fail("swift names an out-of-scope tool")
    return 0


def score(panes):
    bad = 0
    for utterance, status, pane_id in CASES:
        got, got_id = choose(utterance, panes)
        if got != status or (status == "exact" and got_id != pane_id):
            bad += 1
            print(
                f"FAIL {utterance!r} got {got} {got_id} want {status} {pane_id}",
                file=sys.stderr,
            )
    if bad:
        print(f"FAIL {bad}", file=sys.stderr)
        return 1
    print(f"PASS {len(CASES)}/{len(CASES)}")
    return 0


def send_marker(panes):
    status, pane_id = choose("nvimに移動", panes)
    if status != "exact" or pane_id != "%0":
        return fail(f"winner {status} {pane_id}")
    if not PANE_ID.fullmatch(pane_id):
        return fail(f"pane id {pane_id}")
    marker = f"VS_NVIM_ONLY_{uuid.uuid4().hex}"
    sent = subprocess.run(
        ["tmux", "-S", SOCK, "send-keys", "-t", pane_id, "-l", "--", marker],
        capture_output=True,
        text=True,
    )
    if sent.returncode != 0:
        return fail(f"send-keys {sent.returncode} {sent.stderr.strip()}")
    deadline = time.time() + 2
    found = []
    while time.time() < deadline:
        found = []
        for pane in panes:
            cap = tmux(["capture-pane", "-p", "-t", pane["id"]])
            if cap.returncode != 0:
                return fail(f"capture {pane['id']} {cap.stderr.strip()}")
            if marker in cap.stdout:
                found.append(pane["id"])
        if found == ["%0"]:
            print(f"MARKER_ONLY %0 {marker}")
            return 0
        if any(hit != "%0" for hit in found):
            return fail(f"marker in {found}")
        time.sleep(0.1)
    return fail(f"marker panes {found}")


def stop_server():
    killed = tmux(["kill-server"])
    print(f"KILL_SERVER {killed.returncode}")
    listed = tmux(["list-sessions"])
    no_server = listed.returncode != 0 and "no server running" in listed.stderr
    if not no_server:
        print(f"FAIL server still up {listed.stderr.strip()}", file=sys.stderr)
        return 1
    print("NO_SERVER")
    if os.path.exists(SOCK):
        # tmux 3.5a exits without unlinking the socket it bound.
        print("SOCKET_LEFT_BY_TMUX")
        os.remove(SOCK)
    if os.path.exists(SOCK):
        print("FAIL socket remains", file=sys.stderr)
        return 1
    print("SOCKET_GONE")
    return 0


def main():
    code = check_swift()
    if code:
        return code
    if os.path.exists(SOCK):
        return fail(f"socket already present {SOCK}")
    started = tmux(
        ["new-session", "-d", "-s", "vsproof", "-n", "dev", "bash", "--noprofile", "--norc"],
        start=True,
    )
    if started.returncode != 0:
        return fail(f"new-session {started.stderr.strip()}")
    steps = (
        ["split-window", "-d", "-t", "%0", "bash", "--noprofile", "--norc"],
        ["new-window", "-d", "-n", "logs", "bash", "--noprofile", "--norc"],
        ["set-option", "-g", "automatic-rename", "off"],
        ["set-option", "-g", "allow-rename", "off"],
        ["rename-window", "-t", "%0", "dev"],
        ["rename-window", "-t", "%2", "logs"],
        ["select-pane", "-t", "%0", "-T", "nvim"],
        ["select-pane", "-t", "%1", "-T", "shell"],
        ["select-pane", "-t", "%2", "-T", "logs"],
    )
    for args in steps:
        step = tmux(args)
        if step.returncode != 0:
            return fail(f"{args} {step.stderr.strip()}")
    listed = tmux(["list-panes", "-a", "-F", FMT])
    if listed.returncode != 0:
        return fail(f"list-panes {listed.stderr.strip()}")
    try:
        panes = catalog(listed.stdout)
    except ValueError as exc:
        return fail(str(exc))
    got = [(pane["id"], pane["window"], pane["title"], pane["command"]) for pane in panes]
    want = [("%0", "dev", "nvim", "bash"), ("%1", "dev", "shell", "bash"), ("%2", "logs", "logs", "bash")]
    if got != want:
        return fail(f"catalog {got}")
    code = score(panes)
    if code:
        return code
    return send_marker(panes)


if __name__ == "__main__":
    status = 1
    try:
        status = main()
    finally:
        down = 0
        if os.path.exists(SOCK):
            down = stop_server()
        elif status == 0:
            print("FAIL socket missing before cleanup", file=sys.stderr)
            down = 1
        if down:
            status = 1
    sys.exit(status)
