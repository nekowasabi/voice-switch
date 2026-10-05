#!/usr/bin/env python3
"""Closed-catalog pane route. Unique label substring wins. No Jev.

Agent names come from /proc/*/environ TMUX_PANE plus comm/argv0, allowlisted.
The Swift app keeps the same rule. Mac has no /proc; this Linux proof is the contract.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOCK = f"/tmp/voice-switch-pane-route-{os.getpid()}.sock"
FMT = "#{pane_id}\t#{window_name}\t#{pane_title}\t#{pane_current_command}"
PANE_ID = re.compile(r"^%[0-9]+$")
COMM_LIMIT = 16
ALLOW = (
    "claude",
    "aider",
    "gemini",
    "copilot",
    "codex",
    "devin",
    "hermes",
    "opencode",
    "pi",
    "grok",
    "cursor-agent",
)
DAEMON_MARKERS = (
    " app-server",
    " mcp-server",
    " --chrome-native-host",
    "opencode serve",
    " bg-pty-host",
    " bg-spare",
    " daemon run",
)
BASE_CASES = [
    ("nvimに移動", "exact", "%0"),
    ("shell", "exact", "%1"),
    ("logsを見て", "exact", "%2"),
    ("test", "miss", None),
    ("dev", "ambiguous", None),
    ("bash", "ambiguous", None),
]
CLAUDE_UTTERANCE = "claudeへ送って"


def fail(msg):
    print(f"FAIL {msg}", file=sys.stderr)
    return 1


def tmux(args, start=False):
    cmd = ["tmux", "-S", SOCK]
    if start:
        cmd += ["-f", "/dev/null"]
    cmd += list(args)
    return subprocess.run(cmd, capture_output=True, text=True)


def canonical_agent(raw):
    if not raw:
        return None
    name = raw[:COMM_LIMIT]
    if name in ALLOW:
        return name
    if name.startswith("grok-"):
        return "grok"
    return None


def discover_agents(socket_path):
    found = {}
    proc = Path("/proc")
    if not proc.is_dir():
        return found
    for entry in proc.iterdir():
        if not entry.name.isdigit():
            continue
        try:
            env_raw = (entry / "environ").read_bytes()
        except OSError:
            continue
        tmux_val = ""
        pane = ""
        for item in env_raw.split(b"\0"):
            if b"=" not in item or not item:
                continue
            key, value = item.split(b"=", 1)
            if key == b"TMUX":
                tmux_val = value.decode("utf-8", "replace")
            elif key == b"TMUX_PANE":
                pane = value.decode("utf-8", "replace")
        if not tmux_val or not pane:
            continue
        if tmux_val.split(",", 1)[0] != socket_path:
            continue
        if not PANE_ID.fullmatch(pane):
            continue
        try:
            cmdline = (entry / "cmdline").read_bytes().split(b"\0")
        except OSError:
            cmdline = []
        parts = [part.decode("utf-8", "replace") for part in cmdline if part]
        joined = " ".join(parts)
        if any(marker in joined for marker in DAEMON_MARKERS):
            continue
        try:
            comm = (entry / "comm").read_bytes().split(b"\0", 1)[0].decode("utf-8", "replace").strip()
        except OSError:
            comm = ""
        argv0 = Path(parts[0]).name if parts else ""
        names = []
        for raw in (comm, argv0):
            canon = canonical_agent(raw.strip())
            if canon and canon not in names:
                names.append(canon)
        if not names:
            continue
        bucket = found.setdefault(pane, [])
        for name in names:
            if name not in bucket:
                bucket.append(name)
    for pane in found:
        found[pane].sort()
    return found


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


def hits(utterance, panes, agents):
    folded = utterance.casefold()
    found = []
    for pane in panes:
        labels = [pane["title"], pane["window"], pane["command"]]
        labels.extend(agents.get(pane["id"], []))
        labels = [label for label in labels if label]
        if any(label.casefold() in folded for label in labels):
            found.append(pane)
    return found


def choose(utterance, panes, agents):
    found = hits(utterance, panes, agents)
    if len(found) == 1:
        return "exact", found[0]["id"]
    if len(found) > 1:
        return "ambiguous", None
    return "miss", None


def check_swift():
    src = (ROOT / "Sources/voice-switch/PaneRoute.swift").read_text()
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
    if "let commNameLimit = 16" not in src:
        return fail("swift comm limit is not 16")
    for name in ALLOW:
        if f'"{name}"' not in src:
            return fail(f"swift allowlist missing {name}")
    if 'name.hasPrefix("grok-")' not in src:
        return fail("swift grok- prefix missing")
    if "TMUX_PANE" not in src or '"/proc"' not in src:
        return fail("swift /proc TMUX_PANE walk missing")
    if "agents[pane.id]" not in src:
        return fail("swift match ignores discovered agent names")
    if "Mac has no `/proc`" not in src:
        return fail("swift does not document the Mac gap")
    for marker in DAEMON_MARKERS:
        if marker not in src:
            return fail(f"swift daemon marker missing {marker}")
    return 0


def check_normalizer():
    if canonical_agent("grok-1.0.4-macos-aarch64") != "grok":
        return fail("16-char grok prefix")
    if canonical_agent("cursor-agent") != "cursor-agent":
        return fail("cursor-agent")
    if canonical_agent("node") is not None or canonical_agent("bash") is not None:
        return fail("host runtime was allowlisted")
    print("NORM_16 grok")
    return 0


def score(panes, agents, cases):
    bad = 0
    for utterance, status, pane_id in cases:
        got, got_id = choose(utterance, panes, agents)
        if got != status or (status == "exact" and got_id != pane_id):
            bad += 1
            print(
                f"FAIL {utterance!r} got {got} {got_id} want {status} {pane_id}",
                file=sys.stderr,
            )
    if bad:
        print(f"FAIL {bad}", file=sys.stderr)
        return 1
    print(f"PASS {len(cases)}/{len(cases)}")
    return 0


def list_panes():
    listed = tmux(["list-panes", "-a", "-F", FMT])
    if listed.returncode != 0:
        raise RuntimeError(listed.stderr.strip())
    return catalog(listed.stdout)


def send_marker(panes, agents, utterance, pane_id, prefix):
    status, got = choose(utterance, panes, agents)
    if status != "exact" or got != pane_id:
        return fail(f"winner {status} {got}")
    if not PANE_ID.fullmatch(pane_id):
        return fail(f"pane id {pane_id}")
    marker = f"{prefix}_{uuid.uuid4().hex}"
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
        if found == [pane_id]:
            print(f"MARKER_ONLY {pane_id} {marker}")
            return 0
        if any(hit != pane_id for hit in found):
            return fail(f"marker in {found}")
        time.sleep(0.1)
    return fail(f"marker panes {found}")


def send_nothing(panes, agents, utterance):
    status, got = choose(utterance, panes, agents)
    if status != "ambiguous" or got is not None:
        return fail(f"ambiguous want, got {status} {got}")
    marker = f"VS_AMBIG_{uuid.uuid4().hex}"
    # The route sends only on an exact unique hit. This utterance is not sent.
    sent = False
    if status == "exact" and got and PANE_ID.fullmatch(got):
        sent = True
        subprocess.run(
            ["tmux", "-S", SOCK, "send-keys", "-t", got, "-l", "--", marker],
            capture_output=True,
            text=True,
        )
    if sent:
        return fail("ambiguous utterance was sent")
    ids = []
    for pane in panes:
        cap = tmux(["capture-pane", "-p", "-t", pane["id"]])
        if cap.returncode != 0:
            return fail(f"capture {pane['id']} {cap.stderr.strip()}")
        if marker in cap.stdout:
            ids.append(pane["id"])
    if ids:
        return fail(f"ambiguous marker in {ids}")
    matched = [pane["id"] for pane in hits(utterance, panes, agents)]
    print(f"AMBIGUOUS {utterance} {' '.join(matched)}")
    print("SENT_NOTHING")
    return 0


def stop_server():
    killed = tmux(["kill-server"])
    print(f"KILL_SERVER {killed.returncode}")
    listed = tmux(["list-sessions"])
    # tmux 3.5a says "no server running"; 3.6a says "server exited unexpectedly" while the socket lingers.
    no_server = listed.returncode != 0 and (
        "no server running" in listed.stderr
        or "server exited unexpectedly" in listed.stderr
        or "error connecting" in listed.stderr
    )
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


def wait_node_pane():
    deadline = time.time() + 3
    last = None
    while time.time() < deadline:
        try:
            panes = list_panes()
        except RuntimeError as exc:
            return None, str(exc)
        agents = discover_agents(SOCK)
        last = (panes, agents)
        node = [pane for pane in panes if pane["command"] == "node"]
        if len(node) == 1 and agents.get(node[0]["id"]) == ["claude"]:
            others = [pane_id for pane_id, names in agents.items() if "claude" in names and pane_id != node[0]["id"]]
            if not others:
                return node[0]["id"], None
        time.sleep(0.1)
    return None, f"node+claude not ready {last}"


def main():
    code = check_swift()
    if code:
        return code
    code = check_normalizer()
    if code:
        return code
    if os.path.exists(SOCK):
        return fail(f"socket already present {SOCK}")
    work = Path(tempfile.mkdtemp(prefix="voice-switch-claude-"))
    try:
        bindir = work / "bin"
        bindir.mkdir()
        claude = bindir / "claude"
        claude.write_text("#!/bin/sh\nwhile true; do sleep 30; done\n")
        claude.chmod(0o755)
        host = work / "node-host.js"
        host.write_text(
            "const {spawn} = require('child_process');\n"
            "const child = spawn('claude', [], {stdio: 'ignore'});\n"
            "child.on('exit', () => {});\n"
            "setInterval(() => {}, 1000);\n"
        )
        started = tmux(
            ["new-session", "-d", "-s", "vsproof", "-n", "dev", "bash", "--noprofile", "--norc"],
            start=True,
        )
        if started.returncode != 0:
            return fail(f"new-session {started.stderr.strip()}")
        node = shutil.which("node")
        if not node:
            return fail("node not found")
        path = f"{bindir}:/usr/bin:/bin"
        node_cmd = f"exec env PATH={path} {node} {host}"
        steps = (
            ["split-window", "-d", "-t", "%0", "bash", "--noprofile", "--norc"],
            ["new-window", "-d", "-n", "logs", "bash", "--noprofile", "--norc"],
            ["new-window", "-d", "-n", "agent", node_cmd],
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
        node_id, err = wait_node_pane()
        if err:
            return fail(err)
        titled = tmux(["select-pane", "-t", node_id, "-T", "host"])
        if titled.returncode != 0:
            return fail(f"title host {titled.stderr.strip()}")
        renamed = tmux(["rename-window", "-t", node_id, "agent"])
        if renamed.returncode != 0:
            return fail(f"rename agent {renamed.stderr.strip()}")
        try:
            panes = list_panes()
        except RuntimeError as exc:
            return fail(str(exc))
        agents = discover_agents(SOCK)
        got = [(pane["id"], pane["window"], pane["title"], pane["command"]) for pane in panes]
        want = [
            ("%0", "dev", "nvim", "bash"),
            ("%1", "dev", "shell", "bash"),
            ("%2", "logs", "logs", "bash"),
            (node_id, "agent", "host", "node"),
        ]
        if got != want:
            return fail(f"catalog {got}")
        if agents.get(node_id) != ["claude"]:
            return fail(f"agents {agents}")
        bare, _ = choose(CLAUDE_UTTERANCE, panes, {})
        if bare != "miss":
            return fail(f"claude matched without discovery {bare}")
        print(f"AGENT_ONLY {node_id} claude")
        cases = BASE_CASES + [(CLAUDE_UTTERANCE, "exact", node_id)]
        code = score(panes, agents, cases)
        if code:
            return code
        code = send_marker(panes, agents, "nvimに移動", "%0", "VS_NVIM_ONLY")
        if code:
            return code
        code = send_marker(panes, agents, CLAUDE_UTTERANCE, node_id, "VS_CLAUDE_ONLY")
        if code:
            return code
        extra = tmux(["new-window", "-d", "-n", "claude", "bash", "--noprofile", "--norc"])
        if extra.returncode != 0:
            return fail(f"ambiguous window {extra.stderr.strip()}")
        try:
            panes = list_panes()
        except RuntimeError as exc:
            return fail(str(exc))
        titled = [pane for pane in panes if pane["window"] == "claude"]
        if len(titled) != 1:
            return fail(f"claude window {panes}")
        plain = tmux(["select-pane", "-t", titled[0]["id"], "-T", "plain"])
        if plain.returncode != 0:
            return fail(plain.stderr.strip())
        try:
            panes = list_panes()
        except RuntimeError as exc:
            return fail(str(exc))
        agents = discover_agents(SOCK)
        code = score(panes, agents, BASE_CASES)
        if code:
            return code
        return send_nothing(panes, agents, CLAUDE_UTTERANCE)
    finally:
        shutil.rmtree(work, ignore_errors=True)


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
