"""Compare the same behavior probes in a detached baseline and the current working tree."""

import argparse
import difflib
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import struct
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
SWIFT = Path("Sources/voice-switch")
BINARY = Path(".build/release/voice-switch")
MODEL = Path("Library/Application Support/voice-switch/models/ggml-large-v3-turbo-q5_0.bin")
CLI_PROBES = ("--check", "--hud-labels", "--vad-selftest", "--simulate")
CONFIG_TYPES = ("Config", "NoiseReductionOptions", "MacrowhisperConfig", "DictationConfig",
                "TriageSettings", "Route", "MacOSActions", "WakeAction", "NoWakeWordConfig")
PARITY_SOURCE_FAILURE = re.compile(
    r"FAIL (?:(?:Config|DictationConfig) fields mismatch:.+|"
    r"sample config .+ without an allowlist reason|CLI .+ without an allowlist reason)"
)
HARNESS = {
    "triage": ["sh", "tests/triage/run.sh"],
    "journal-append": ["bash", ".claude/skills/verify-journal-append/run.sh", "check"],
    "url-actions": ["python3", "tests/url-actions/run.py", "--require-swift"],
}


def run(argv, repo, env=None, timeout=240):
    with subprocess.Popen(argv, cwd=repo, env=env, start_new_session=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE) as proc:
        try:
            stdout, stderr = proc.communicate(timeout=timeout)
        except (subprocess.TimeoutExpired, KeyboardInterrupt) as error:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            stdout, stderr = proc.communicate()
            if isinstance(error, KeyboardInterrupt):
                raise
            stderr += f"\nTimed out after {timeout} s\n".encode()
            return 124, stdout.decode(errors="replace"), stderr.decode(errors="replace")
    return proc.returncode, stdout.decode(errors="replace"), stderr.decode(errors="replace")


def normalize(text, repo, sandbox, work):
    for path in (repo, sandbox, work):
        for spelling in {str(path), str(path.resolve())}:
            text = text.replace(spelling, "<worktree>" if path == repo else "<tmp>")
    text = re.sub(r"(?m)^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d) ", "<time> ", text)
    text = re.sub(r"(?m)^(.*\b(?:direct=|stt:|recognized:).*)\bseconds=\d+(?:\.\d+)?", r"\1seconds=<elapsed>", text)
    text = re.sub(r"\+stt\d+|(?<=\bstt )\d+(?= ms)|(?<=\bms=)\d+(?:\.\d+)?", "<elapsed>", text)
    text = re.sub(r"(?<=\binputIdleSeconds=)\d+(?:\.\d+)?", "<elapsed>", text)
    text = re.sub(r"(?m)^<time> downloading speech model for [^\n]+\n", "", text)
    # The Whisper/Metal startup and compiler report machine timings and process addresses on stderr.
    text = re.sub(r"(?m)^(?:whisper_[a-z_]+|ggml_[a-z_]+):[^\n]*\n", "", text)
    return text


def mode_list(repo):
    source = (repo / SWIFT / "main.swift").read_text()
    dispatch = re.search(r"let mode\s*=.*?\[(.*?)\]", source, re.S)
    modes = re.findall(r'"(--[\w-]+)"', dispatch[1]) if dispatch else []
    handlers = re.findall(r'\bif mode == "(--[\w-]+)"', source)
    return sorted(modes), set(handlers)


def reflected_fields(repo, binary):
    """Read the Swift field descriptors by symbol, with source fallback for stripped binaries."""
    argv = str(repo / BINARY)
    status, layout, _ = run(["xcrun", "otool", "-l", argv], repo, timeout=15)
    section = re.search(
        r"sectname __swift5_fieldmd\s+segname __TEXT\s+addr (0x[0-9a-f]+)\s+size 0x[0-9a-f]+\s+offset (\d+)",
        layout,
    )
    if status or section is None:
        return {}
    delta = int(section[2]) - int(section[1], 16)
    status, symbols, _ = run(["xcrun", "nm", "-p", argv], repo, timeout=15)
    if status:
        return {}
    demangled = subprocess.run(["xcrun", "swift-demangle"], input=symbols, text=True,
                               capture_output=True, timeout=15)
    fields = {}
    for address, name in re.findall(
        r"(?m)^([0-9a-f]+) \w reflection metadata field descriptor \w+\.(.+)$", demangled.stdout
    ):
        name = re.sub(r"\.\(CodingKeys in \w+\)$", ".CodingKeys", name)
        if name not in {"WakeAction.Delivery", *(f"{kind}.CodingKeys" for kind in CONFIG_TYPES),
                        "TriageSettings.Route.CodingKeys"}:
            continue
        # The descriptor and its relative field-name pointers are in the same __TEXT segment.
        start = int(address, 16) + delta
        record_size, count = struct.unpack_from("<HI", binary, start + 10)
        names = []
        for index in range(count):
            pointer = start + 16 + index * record_size + 8
            offset = pointer + struct.unpack_from("<i", binary, pointer)[0]
            names.append(binary[offset:binary.index(b"\0", offset)].decode())
        fields[name] = names
    return fields


def feature_list(repo, modes, handlers, artifact=b""):
    config = (repo / SWIFT / "Config.swift").read_text()
    wake = (repo / SWIFT / "WakeSafety.swift").read_text()
    source = config + "\n" + wake
    reflection = reflected_fields(repo, artifact) if artifact else {}
    keys = []
    for name in CONFIG_TYPES:
        match = re.search(rf"(?m)^([ \t]*)(?:\w+ )?struct {name}: Decodable \{{\n(.*?)^\1\}}", source, re.S)
        if match is None:
            continue
        indent, body = match.groups()
        # Swift's synthesized keys are the stored properties at this nesting level.
        explicit = re.search(rf"(?m)^{indent}    (?:private )?enum CodingKeys:[^{{]+\{{([^}}]*)\}}", body)
        if explicit:
            fields = {}
            for case in re.findall(r"\bcase\s+([^\n]+)", explicit[1]):
                for field in case.split(","):
                    field_name, _, value = field.strip().partition("=")
                    fields[field_name.strip()] = json.loads(value.strip()) if value else field_name.strip()
        else:
            fields = {key: key for key in re.findall(rf"(?m)^{indent}    (?:public )?(?:var|let) (\w+):[^\n{{]+$", body)}
        type_name = "TriageSettings.Route" if name == "Route" else name
        reflected = reflection.get(f"{type_name}.CodingKeys", fields)
        keys.extend(f"{name}.{fields.get(key, key)}" for key in reflected)
    delivery = re.search(r"enum Delivery: [^{]+\{([^}]+)\}", config)
    kinds = reflection.get("WakeAction.Delivery")
    if kinds is None:
        kinds = re.findall(r"(?:case |,)\s*(\w+)", delivery[1]) if delivery else []
    menus = (repo / SWIFT / "MacApp.swift").read_text()
    titles = re.findall(r'NSMenuItem\(title: "([^"\\]*(?:\\\([^\n]*?\)[^"\\]*)*)"', menus)
    titles.extend(re.findall(r'for \(title, uid\) in \[\("([^"]+)"', menus))
    titles.extend(re.findall(r'fallbackItem\("(.+?)", uid:', menus))
    for label_update in re.findall(r"(?m)^\s*toggle\.title = (.+)$", menus):
        titles.extend(re.findall(r'"([^"]+)"', label_update))
    return {
        "cli-modes": "\n".join(f"{mode} ({'handler' if mode in handlers else 'missing handler'})" for mode in modes),
        "config-keys": "\n".join(sorted(keys)),
        "menu-titles": "\n".join(sorted(set(titles))),
        "wake-action-kinds": "\n".join(sorted(kinds)),
    }, reflection


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base_ref")
    args = parser.parse_args()
    base = subprocess.check_output(
        ["git", "rev-parse", "--verify", "--end-of-options", args.base_ref + "^{commit}"], cwd=ROOT, text=True
    ).strip()
    started = time.monotonic()

    with tempfile.TemporaryDirectory(prefix="voice-switch-equivalence-") as temp:
        work = Path(temp).resolve()
        baseline = work / "base"
        subprocess.run(["git", "worktree", "add", "--quiet", "--detach", str(baseline), base], cwd=ROOT, check=True)
        try:
            outcome = compare(baseline, work)
        finally:
            subprocess.run(["git", "worktree", "remove", "--force", str(baseline)], cwd=ROOT, check=True)

    print(f"Equivalence {'passed' if outcome else 'failed'} ({time.monotonic() - started:.0f} s)")
    return 0 if outcome else 1


def compare(baseline, work):
    repos = (baseline, ROOT)
    env = os.environ.copy()
    for name in ("TRIAGE_SOURCES", "JOURNAL_TRIAGE", "SIMULATE_REALTIME", "VS_VAD_TRACE", "TYPESAFE_API_KEY", "JEV_API_KEY"):
        env.pop(name, None)
    env.update(LC_ALL="en_US.UTF-8", TZ="UTC", PYTHONHASHSEED="0", DOTNET_NOLOGO="1")
    routes = {repo: mode_list(repo) for repo in repos}
    outputs = {repo: {} for repo in repos}
    metadata = {}
    binary_titles = {}
    parity_source_failures = {}
    skips = {}
    darwin = os.uname().sysname == "Darwin"
    candidates = (shutil.which("dotnet"), Path.home() / ".dotnet/dotnet",
                  Path.home() / ".local/share/mise/shims/dotnet")
    dotnet = next((str(path) for path in candidates if path and os.access(path, os.X_OK)
                   and re.search(r"(?m)^\d+\.", run([path, "--list-sdks"], ROOT, env, 15)[1])), None)
    if dotnet:
        env["PATH"] = str(Path(dotnet).parent) + os.pathsep + env["PATH"]
    else:
        skips["windows-core"] = ".NET SDK unavailable"
    if os.name != "nt":
        skips["windows-cli"] = "Windows executable requires Windows"

    config_path = work / "config.json"
    config_path.write_text(json.dumps({
        "wakeWords": ["プログレス"],
        "command": 'printf "%s\\n" "wake action" > "$VS_EQ_WAKE_MARKER"',
        "locale": "ja_JP",
    }))
    wav = work / "speech.wav"
    if darwin:
        aiff = work / "speech.aiff"
        made, _, _ = run(["say", "-v", "Kyoko", "-o", str(aiff), "[[slnc 1000]] プログレス [[slnc 5000]]"], ROOT, env, 30)
        if made == 0:
            made, _, _ = run(["afconvert", "-f", "WAVE", "-d", "LEI16@16000", "-c", "1", str(aiff), str(wav)], ROOT, env, 30)
        if made or not wav.is_file():
            skips["cli-simulate"] = "Kyoko synthetic WAV unavailable"
    else:
        skips.update({f"cli-{mode[2:]}": "macOS required" for mode in CLI_PROBES})
        skips["mac-release-build"] = "macOS required"

    if darwin:
        model = Path.home() / MODEL
        if not model.is_file():
            skips["cli-vad-selftest"] = "Whisper model unavailable"
        if os.stat("/dev/console").st_uid != os.getuid():
            skips["cli-hud-labels"] = "no active macOS desktop for this user"

    def record(repo, name, argv, settings, timeout=240):
        rc, stdout, stderr = run(argv, repo, settings, timeout)
        if name == "cli-simulate" and rc == 0:
            marker = Path(settings["VS_EQ_WAKE_MARKER"])
            if marker.is_file():
                stdout += marker.read_text()
            else:
                rc = 1
                stderr += "\nThe synthetic wake word did not run the configured command.\n"
        output = f"exit {rc}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        if name == "parity":
            diagnostics = stderr.splitlines()
            summary = re.search(r"(?m)^SUMMARY pass=\d+ allow=\d+ fail=(\d+)$", stdout)
            if (rc == 1 and diagnostics and summary and int(summary[1]) == len(diagnostics)
                    and all(PARITY_SOURCE_FAILURE.fullmatch(line) for line in diagnostics)):
                # Preserve the actual exit and diagnostics in the comparison; only pre-existing
                # source-contract differences are comparable when parity itself exits nonzero.
                parity_source_failures[repo] = len(diagnostics)
        outputs[repo][name] = (rc, normalize(output, repo, Path(settings["TMPDIR"]).parent, work))

    can_check = darwin and all("--check" in modes and "--check" in handlers for modes, handlers in routes.values())
    full_parity = bool(dotnet)
    for repo in repos:
        modes, handlers = routes[repo]
        revision = "base" if repo == baseline else "head"
        sandbox = work / f"{revision}-run"
        sandbox.mkdir()
        tmp = sandbox / "tmp"
        tmp.mkdir()
        isolated = {**env, "TMPDIR": str(tmp)}
        built = False
        if darwin:
            # A pinned dependency can be built from cache even if its remote is unavailable.
            rc, stdout, stderr = run(["swift", "build", "-c", "release", "--skip-update"], repo, isolated)
            built = rc == 0 and (repo / BINARY).is_file()
            if built:
                outputs[repo]["mac-release-build"] = (0, "Release binary built\n")
            else:
                outputs[repo]["mac-release-build"] = (rc or 1, normalize(stdout + stderr, repo, sandbox, work))

        artifact = (repo / BINARY).read_bytes() if built else b""
        features, metadata[repo] = feature_list(repo, modes, handlers, artifact)
        for name, value in features.items():
            outputs[repo][name] = (0, value + "\n") if value else (1, "No entries extracted\n")
        binary_titles[repo] = {title for title in features["menu-titles"].splitlines() if title.encode() in artifact}

        fixtures = repo / "tests/parity/fixtures"
        digests = [f"{hashlib.sha256(path.read_bytes()).hexdigest()} {path.relative_to(fixtures)}"
                   for path in sorted(fixtures.rglob("*")) if path.is_file()]
        outputs[repo]["parity-fixtures"] = (0, "\n".join(digests)) if digests else (1, "No fixtures found\n")
        for name, argv in HARNESS.items():
            record(repo, name, argv, isolated)
        parity_options = ["--require-swift"] if full_parity and can_check and built else ["--static-only"]
        # The two C# cough-policy projects share an obj directory and can reuse the wrong apphost.
        parity_env = {**isolated, "UseAppHost": "false"}
        record(repo, "parity", ["python3", "tests/parity/run_parity.py", *parity_options], parity_env)
        if dotnet:
            record(repo, "windows-core", [dotnet, "run", "--project", "dotnet/VoiceSwitch.SharedChecks", "-c", "Release"], isolated)

        if not darwin:
            continue
        home = sandbox / "home"
        model_link = home / MODEL
        model_link.parent.mkdir(parents=True)
        if model.is_file():
            model_link.symlink_to(model)
        cli_env = {
            **isolated, "HOME": str(home), "CFFIXED_USER_HOME": str(home),
            "VOICE_SWITCH_CONFIG": str(config_path), "VS_EQ_WAKE_MARKER": str(sandbox / "wake.txt"),
        }
        hud = sandbox / "hud"
        hud.mkdir()
        check_audio = wav if wav.is_file() else (baseline / "tests/parity/fixtures/speech_gate/speech-sentence.wav")
        commands = {"--check": [str(check_audio)], "--simulate": [str(wav)],
                    "--hud-labels": [str(hud)], "--vad-selftest": []}
        for mode, argv in commands.items():
            name = f"cli-{mode[2:]}"
            if mode not in modes or mode not in handlers:
                outputs[repo][name] = (1, f"{mode} dispatch missing\n")
            elif name not in skips:
                if built:
                    record(repo, name, [str(repo / BINARY), mode, *argv], cli_env, 90)
                else:
                    outputs[repo][name] = (1, "Release binary unavailable\n")

    if not full_parity or not can_check:
        print("SKIP parity-runtime (full parity requires .NET SDK and macOS --check on both revisions)")
    reflected = set(metadata[ROOT]) & set(metadata[baseline])
    source_keys = [name for name in CONFIG_TYPES
                   if ("TriageSettings.Route" if name == "Route" else name) + ".CodingKeys" not in reflected]
    print("INFO CLI dispatch comes from source, with four release binary probes.")
    print(f"INFO Config keys checked against binary metadata. Source-only types: {', '.join(source_keys) or 'none'}.")
    kinds_from = "release binary metadata" if "WakeAction.Delivery" in reflected else "source fallback"
    print(f"INFO Wake action kinds come from {kinds_from}.")
    title_count = len(binary_titles[ROOT] & binary_titles[baseline])
    print(f"INFO Menu titles come from source. Both binaries contain {title_count} of those titles as literals.")
    success = True
    for name in sorted(set(skips) | set(outputs[ROOT]) | set(outputs[baseline])):
        if name in skips and not any(name in result for result in outputs.values()):
            print(f"SKIP {name} ({skips[name]})")
            continue
        base_rc, base_text = outputs[baseline].get(name, (1, "No baseline output\n"))
        head_rc, head_text = outputs[ROOT].get(name, (1, "No current output\n"))
        old_parity_failures_match = name == "parity" and len(parity_source_failures) == len(repos)
        if base_text == head_text and (base_rc == head_rc == 0 or old_parity_failures_match):
            if old_parity_failures_match:
                print(f"INFO Parity exited 1 on both revisions with {parity_source_failures[baseline]} identical source-contract failures.")
            print(f"SAME {name}")
            continue
        print(f"DIFF {name}")
        success = False
        diff = difflib.unified_diff(base_text.splitlines(), head_text.splitlines(), "base", "current", lineterm="")
        if base_text == head_text:
            print(f"  Both probes failed (base={base_rc}, current={head_rc})")
            diff = iter(head_text.splitlines()[-20:])
        for _, line in zip(range(20), diff):
            print(line)
    return success


if __name__ == "__main__":
    raise SystemExit(main())
