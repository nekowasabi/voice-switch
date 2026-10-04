#!/usr/bin/env python3
"""Parity contract checks for the macOS Swift and Windows C# implementations."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
CONTRACT = HERE / "contracts" / "platform_parity.json"
TEXT_FIXTURE = HERE / "fixtures" / "text_matching.json"
SEGMENTER_FIXTURE = HERE / "fixtures" / "segmenter.json"
CLI_FIXTURE = HERE / "fixtures" / "cli.json"
SWIFT_HARNESS = HERE / "swift" / "main.swift"


def main() -> int:
    args = parse_args()
    contract = load_json(CONTRACT)
    text_fixture = load_json(TEXT_FIXTURE)
    segmenter_fixture = load_json(SEGMENTER_FIXTURE)
    cli_fixture = load_json(CLI_FIXTURE)
    repo = SourceTree(ROOT)
    result = Result()

    check_capability_markers(result, repo, contract)
    check_allowed_capability_gaps(result, repo, contract)
    check_static_call_chains(result, repo)
    check_dictation_test_markers(result, repo)
    check_config_fields(result, repo)
    check_example_config_fields(result, contract)
    check_cli_contract(result, repo, contract, cli_fixture)
    check_windows_core_behavior(result, text_fixture, segmenter_fixture)
    check_swift_core_behavior(result, args.require_swift)
    check_mutation_gate(result, repo, contract)

    result.note("macOS runtime execution skipped: Apple Speech, AVFoundation, and the macOS SDK are not available in this WSL environment.")
    result.print()
    return 1 if result.failures else 0


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run voice-switch platform parity checks.")
    parser.add_argument(
        "--require-swift",
        action="store_true",
        help="fail instead of skipping the native pure-Swift harness when swiftc is unavailable",
    )
    return parser.parse_args()


class SourceTree:
    def __init__(self, root: Path, overrides: dict[str, str] | None = None) -> None:
        self.root = root
        self.overrides = overrides or {}

    def read(self, relative: str) -> str:
        if relative in self.overrides:
            return self.overrides[relative]
        return (self.root / relative).read_text(encoding="utf-8")

    def code(self, relative: str) -> str:
        return strip_comments(self.read(relative))


class Result:
    def __init__(self) -> None:
        self.passes: list[str] = []
        self.failures: list[str] = []
        self.notes: list[str] = []
        self.allowed: list[str] = []

    def ok(self, message: str) -> None:
        self.passes.append(message)

    def fail(self, message: str) -> None:
        self.failures.append(message)

    def note(self, message: str) -> None:
        self.notes.append(message)

    def allow(self, message: str) -> None:
        self.allowed.append(message)

    def print(self) -> None:
        for message in self.passes:
            print(f"PASS {message}")
        for message in self.allowed:
            print(f"ALLOW {message}")
        for message in self.notes:
            print(f"NOTE {message}")
        for message in self.failures:
            print(f"FAIL {message}", file=sys.stderr)
        print(f"SUMMARY pass={len(self.passes)} allow={len(self.allowed)} fail={len(self.failures)}")


def strip_comments(source: str) -> str:
    out: list[str] = []
    i = 0
    state = "code"
    while i < len(source):
        ch = source[i]
        nxt = source[i + 1] if i + 1 < len(source) else ""
        if state == "code":
            if ch == "/" and nxt == "/":
                state = "line"
                i += 2
                continue
            if ch == "/" and nxt == "*":
                state = "block"
                i += 2
                continue
            if ch == '"':
                if source[i : i + 3] == '"""':
                    out.append('"""')
                    i += 3
                    state = "raw_string"
                    continue
                out.append(ch)
                state = "string"
                i += 1
                continue
            out.append(ch)
            i += 1
            continue
        if state == "line":
            if ch == "\n":
                out.append(ch)
                state = "code"
            i += 1
            continue
        if state == "block":
            if ch == "*" and nxt == "/":
                state = "code"
                i += 2
            else:
                if ch == "\n":
                    out.append("\n")
                i += 1
            continue
        if state == "string":
            out.append(ch)
            if ch == "\\":
                if i + 1 < len(source):
                    out.append(source[i + 1])
                    i += 2
                    continue
            elif ch == '"':
                state = "code"
            i += 1
            continue
        if state == "raw_string":
            if source[i : i + 3] == '"""':
                out.append('"""')
                i += 3
                state = "code"
                continue
            out.append(ch)
            i += 1
            continue
    return "".join(out)


def require_substrings(result: Result, label: str, source: str, markers: list[str]) -> bool:
    missing = [marker for marker in markers if marker not in source]
    if missing:
        result.fail(f"{label} missing markers: {missing}")
        return False
    result.ok(label)
    return True


def check_capability_markers(result: Result, repo: SourceTree, contract: dict) -> None:
    for capability in contract["shared_capabilities"]:
        for platform in ("macos", "windows"):
            side = capability[platform]
            source = repo.code(side["file"])
            missing = [marker for marker in side["markers"] if marker not in source]
            if missing:
                result.fail(f"{capability['id']} missing {platform} markers in {side['file']}: {missing}")
            else:
                result.ok(f"{capability['id']} has {platform} implementation markers")


def check_allowed_capability_gaps(result: Result, repo: SourceTree, contract: dict) -> None:
    for gap in contract["allowed_capability_gaps"]:
        missing_platform = gap["platform"]
        present_platform = "windows" if missing_platform == "macos" else "macos"
        present = gap[present_platform]
        present_source = repo.code(present["file"])
        missing_present = [marker for marker in present["markers"] if marker not in present_source]
        if missing_present:
            result.fail(f"allowed gap {gap['id']} lost its source evidence in {present['file']}: {missing_present}")
            continue
        stale_side = gap.get(missing_platform, {})
        stale_markers = stale_side.get("stale_markers", [])
        if stale_side:
            stale_source = repo.code(stale_side["file"])
            present_stale_markers = [marker for marker in stale_markers if marker in stale_source]
            if present_stale_markers:
                result.fail(f"allowed gap {gap['id']} is stale because {missing_platform} now has markers: {present_stale_markers}")
                continue
        result.allow(f"{gap['id']} missing on {missing_platform}: {gap['reason']}")


def check_static_call_chains(result: Result, repo: SourceTree) -> None:
    check_windows_cli_call_chain(result, repo)
    check_windows_runtime_call_chain(result, repo)
    check_swift_cli_call_chain(result, repo)
    check_swift_runtime_call_chain(result, repo)


def check_dictation_test_markers(result: Result, repo: SourceTree) -> None:
    tests = repo.code("dotnet/VoiceSwitch.Windows.Tests/Program.cs")
    require_substrings(
        result,
        "Windows dictation production-path tests cover delayed recognition and handoff lifecycle",
        tests,
        [
            "DictationRuntimeKeepsBodyWhileRecognitionIsDelayed",
            "ScriptedDictationRecognizer",
            "RecordingDictationHandoff",
            "DictationHandoffTranscribesAndDeletesWav",
            "RuntimeDropsOverlappingDictationAndAwaitsInflightAtEof",
            "DictationWinMmUsesInputDataCallbackMessage",
        ],
    )


def check_windows_cli_call_chain(result: Result, repo: SourceTree) -> None:
    cli = repo.code("dotnet/VoiceSwitch.Windows.Core/CliOptions.cs")
    program = repo.code("dotnet/VoiceSwitch.Windows/Program.cs")
    parse_body = extract_braced_body(cli, r"public\s+static\s+CliOptions\s+Parse\s*\([^)]*\)\s*\{")
    main_body = extract_braced_body(program, r"public\s+static\s+int\s+Run\s*\([^)]*\)\s*\{")
    require_substrings(
        result,
        "Windows CLI --fire parse reaches Fire option",
        parse_body,
        ['case "--fire":', "fire = true", "return new CliOptions(help, selfTest, fire"],
    )
    require_substrings(
        result,
        "Windows CLI --dry-run parse reaches DryRun option",
        parse_body,
        ['case "--dry-run":', "dryRun = true", "fire && dryRun"],
    )
    require_substrings(
        result,
        "Windows CLI --fire dispatch reaches CommandRunner",
        main_body,
        ["var options = CliOptions.Parse(args)", "if (options.Fire)", "CommandRunner.Run(config.Command)"],
    )
    require_substrings(
        result,
        "Windows CLI --vad-selftest alias reaches SelfTest",
        parse_body + "\n" + main_body,
        ['case "--vad-selftest":', "selfTest = true", "if (options.SelfTest)", "SelfTest.Run()"],
    )
    selftest_body = extract_braced_body(program, r"public\s+static\s+int\s+Run\s*\(\s*\)\s*\{", after="public static class SelfTest")
    require_substrings(
        result,
        "Windows self-test verifies Segmenter",
        selftest_body,
        ["if (!VerifySegmenter())", "self-test: segmenter decision failed"],
    )


def check_windows_runtime_call_chain(result: Result, repo: SourceTree) -> None:
    program = repo.code("dotnet/VoiceSwitch.Windows/Program.cs")
    body = extract_braced_body(program, r"private\s+(?:void|bool)\s+HandleRecognizerLine\s*\([^)]*\)\s*\{")
    require_substrings(
        result,
        "Windows recognized text path reaches command runner",
        body,
        [
            "RecognizerMessage.TryParse(line, out var message)",
            'message.Type != "recognized"',
            "TextMatching.Decide(message.Text, config)",
            'decision.Kind == "run-command"',
            "runCommand(decision.Command)",
        ],
    )
    main_body = extract_braced_body(program, r"public\s+static\s+int\s+Run\s*\([^)]*\)\s*\{")
    require_substrings(
        result,
        "Windows normal runtime injects CommandRunner",
        main_body,
        ["options.DryRun", "SpeechPowerShell.Start", "CommandRunner.Run"],
    )


def check_swift_cli_call_chain(result: Result, repo: SourceTree) -> None:
    main_swift = repo.code("Sources/voice-switch/main.swift")
    require_substrings(
        result,
        "Swift CLI --fire branch reaches Platform.runCommand",
        main_swift,
        ['"--fire"', 'mode == "--fire"', "ConfigFile(path: configPath).cfg", "Platform.runCommand(cfg.command)"],
    )
    require_substrings(
        result,
        "Swift CLI --vad-selftest branch reaches vadSelftest",
        main_swift,
        ['"--vad-selftest"', 'mode == "--vad-selftest"', "vadSelftest()"],
    )


def check_swift_runtime_call_chain(result: Result, repo: SourceTree) -> None:
    mac = repo.code("Sources/voice-switch/MacApp.swift")
    body = extract_braced_body(mac, r"private\s+func\s+consume\s*\([^)]*\)\s+async\s*\{")
    require_substrings(
        result,
        "Swift runtime wake path reaches configured command",
        body,
        [
            "var seg = Segmenter(cfg: config.cfg)",
            "let event = seg.push(f)",
            "transcript = try await transcribe",
            "let hit = !isHead && config.cfg.wakeWords.map(normalize).contains(t)",
            "Platform.runCommand(config.cfg.command)",
        ],
    )
    require_substrings(
        result,
        "Swift runtime stop path is session-gated",
        body,
        [
            "config.cfg.stopWords ?? []",
            "micInUse(by: config.cfg.skipWhileMicInUseBy ?? [])",
            "Platform.runCommand(config.cfg.stopCommand ?? Platform.defaultSuperwhisperToggle)",
            "stop word, nothing is recording",
        ],
    )


def extract_braced_body(source: str, signature_pattern: str, after: str | None = None) -> str:
    offset = source.find(after) if after is not None else 0
    if offset < 0:
        return ""
    match = re.search(signature_pattern, source[offset:], flags=re.DOTALL)
    if not match:
        return ""
    start = offset + match.end()
    depth = 1
    pos = start
    while pos < len(source) and depth:
        if source[pos] == "{":
            depth += 1
        elif source[pos] == "}":
            depth -= 1
        pos += 1
    return source[start : pos - 1]


def check_config_fields(result: Result, repo: SourceTree) -> None:
    swift = repo.code("Sources/voice-switch/Config.swift")
    cs = repo.code("dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs")
    swift_config = extract_swift_struct_fields(swift, "Config")
    swift_dictation = extract_swift_struct_fields(swift, "DictationConfig")
    cs_config = extract_cs_record_fields(cs, "VoiceSwitchConfig")
    cs_dictation = extract_cs_record_fields(cs, "DictationConfig")
    compare_sets(result, "Config fields", swift_config, cs_config)
    compare_sets(result, "DictationConfig fields", swift_dictation, cs_dictation)


def extract_swift_struct_fields(source: str, struct_name: str) -> set[str]:
    body = block_after(source, rf"struct\s+{struct_name}\s*:\s*Decodable\s*\{{")
    return set(re.findall(r"^\s*var\s+([A-Za-z][A-Za-z0-9_]*)\s*:", body, flags=re.MULTILINE))


def extract_cs_record_fields(source: str, record_name: str) -> set[str]:
    match = re.search(rf"record\s+{record_name}\s*\((.*?)\)\s*(?:\{{|;)", source, flags=re.DOTALL)
    if not match:
        return set()
    fields = set()
    for raw in split_parameters(match.group(1)):
        raw = raw.split("=")[0].strip()
        if not raw:
            continue
        name = raw.split()[-1].strip("?[]")
        fields.add(name[:1].lower() + name[1:])
    return fields


def block_after(source: str, pattern: str) -> str:
    match = re.search(pattern, source)
    if not match:
        return ""
    start = match.end()
    depth = 1
    pos = start
    while pos < len(source) and depth:
        if source[pos] == "{":
            depth += 1
        elif source[pos] == "}":
            depth -= 1
        pos += 1
    return source[start : pos - 1]


def split_parameters(value: str) -> list[str]:
    parts: list[str] = []
    depth = 0
    start = 0
    for index, char in enumerate(value):
        if char in "([":
            depth += 1
        elif char in ")]":
            depth -= 1
        elif char == "," and depth == 0:
            parts.append(value[start:index])
            start = index + 1
    parts.append(value[start:])
    return parts


def compare_sets(result: Result, label: str, swift_fields: set[str], cs_fields: set[str]) -> None:
    missing_in_windows = sorted(swift_fields - cs_fields)
    missing_in_macos = sorted(cs_fields - swift_fields)
    if missing_in_windows or missing_in_macos:
        result.fail(f"{label} mismatch: missing in Windows={missing_in_windows}, missing in macOS={missing_in_macos}")
    else:
        result.ok(f"{label} match between Swift and C#")


def check_example_config_fields(result: Result, contract: dict) -> None:
    mac = load_json(ROOT / "config.example.json")
    win = load_json(ROOT / "config.example.windows.json")
    mac_keys = flatten_keys(mac)
    win_keys = flatten_keys(win)
    allowed = {
        (gap["key"], gap["platform"]): gap["reason"]
        for gap in contract.get("allowed_sample_config_gaps", [])
    }
    for key in sorted(mac_keys & win_keys):
        result.ok(f"sample config key {key} is shared")
    for key in sorted(mac_keys - win_keys):
        reason = allowed.get((key, "windows"))
        if reason:
            result.allow(f"sample config {key} omitted on windows: {reason}")
        else:
            result.fail(f"sample config {key} exists only on macOS without an allowlist reason")
    for key in sorted(win_keys - mac_keys):
        reason = allowed.get((key, "macos"))
        if reason:
            result.allow(f"sample config {key} omitted on macos: {reason}")
        else:
            result.fail(f"sample config {key} exists only on Windows without an allowlist reason")
    for (key, platform), _reason in sorted(allowed.items()):
        missing = platform == "windows" and key not in win_keys and key in mac_keys
        missing = missing or (platform == "macos" and key not in mac_keys and key in win_keys)
        if not missing:
            if key in mac_keys and key in win_keys:
                result.note(f"sample config gap allowlist for {key} on {platform} is currently unused because both samples include it")
            else:
                result.fail(f"sample config gap {key} for {platform} has no source evidence")


def flatten_keys(value: object, prefix: str = "") -> set[str]:
    if not isinstance(value, dict):
        return set()
    keys = set()
    for key, child in value.items():
        name = f"{prefix}.{key}" if prefix else key
        keys.add(name)
        keys.update(flatten_keys(child, name))
    return keys


def check_cli_contract(result: Result, repo: SourceTree, contract: dict, cli_fixture: dict) -> None:
    mac_flags = extract_flags(repo.code("Sources/voice-switch/main.swift"))
    win_source = "\n".join(
        repo.code(relative)
        for relative in sorted(windows_cs_files())
    )
    win_flags = extract_flags(win_source)
    for flag in sorted(set(contract["shared_cli_flags"]) | set(cli_fixture["shared_flags"])):
        aliases = set(cli_fixture.get("aliases", {}).get(flag, []))
        if flag not in mac_flags and not aliases.intersection(mac_flags):
            result.fail(f"shared CLI flag {flag} missing on macOS Swift entry")
        elif flag not in win_flags and not aliases.intersection(win_flags):
            result.fail(f"shared CLI flag {flag} missing on Windows C# entry")
        else:
            result.ok(f"shared CLI flag {flag} is present on both platforms")

    allowed = {(gap["flag"], gap["platform"]): gap["reason"] for gap in contract["allowed_cli_gaps"]}
    all_flags = mac_flags | win_flags
    for flag in sorted(all_flags - set(contract["shared_cli_flags"])):
        in_mac = flag in mac_flags
        in_win = flag in win_flags
        if in_mac == in_win:
            for platform in ("macos", "windows"):
                if (flag, platform) in allowed:
                    result.fail(f"CLI gap {flag} for {platform} is stale because both platforms expose it")
            continue
        missing_platform = "windows" if in_mac else "macos"
        reason = allowed.get((flag, missing_platform))
        if reason:
            result.allow(f"CLI {flag} missing on {missing_platform}: {reason}")
        else:
            result.fail(f"CLI {flag} exists on only one platform without an allowlist reason")


def extract_flags(source: str) -> set[str]:
    return set(re.findall(r'"(--[a-z][a-z-]*)"', source))


def windows_cs_files() -> list[str]:
    return [
        path.relative_to(ROOT).as_posix()
        for project in ("VoiceSwitch.Windows.Core", "VoiceSwitch.Windows")
        for path in sorted((ROOT / "dotnet" / project).glob("**/*.cs"))
    ]


def check_windows_core_behavior(result: Result, text_fixture: dict, segmenter_fixture: dict) -> None:
    dotnet = shutil.which("dotnet") or str(Path.home() / ".local/share/mise/shims/dotnet")
    if not Path(dotnet).exists() and shutil.which("dotnet") is None:
        result.fail("dotnet not found; cannot execute Windows C# core behavior harness")
        return

    with tempfile.TemporaryDirectory(prefix="voice-switch-parity-") as tmp:
        tmp_path = Path(tmp)
        core_tmp = tmp_path / "VoiceSwitch.Windows.Core"
        core_tmp.mkdir()
        for source in (ROOT / "dotnet" / "VoiceSwitch.Windows.Core").glob("*.cs"):
            shutil.copy2(source, core_tmp / source.name)
        (core_tmp / "VoiceSwitch.Windows.Core.csproj").write_text(core_project(), encoding="utf-8")
        (tmp_path / "Harness.csproj").write_text(harness_project(core_tmp / "VoiceSwitch.Windows.Core.csproj"), encoding="utf-8")
        (tmp_path / "Program.cs").write_text(harness_program(text_fixture, segmenter_fixture), encoding="utf-8")
        completed = subprocess.run(
            [
                dotnet,
                "run",
                "--project",
                str(tmp_path / "Harness.csproj"),
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=120,
        )
        if completed.returncode == 0:
            for line in completed.stdout.splitlines():
                if line.startswith("PASS "):
                    result.ok(f"C# core {line[5:]}")
            if "PASS " not in completed.stdout:
                result.ok("C# core harness completed")
        else:
            result.fail("C# core behavior harness failed:\n" + completed.stdout + completed.stderr)


def check_swift_core_behavior(result: Result, require_swift: bool) -> None:
    swiftc = shutil.which("swiftc")
    if swiftc is None:
        message = "Swift pure harness skipped because swiftc is unavailable; rerun with --require-swift to make this a failure"
        if require_swift:
            result.fail(message)
        else:
            result.note(message)
        return

    with tempfile.TemporaryDirectory(prefix="voice-switch-swift-parity-") as tmp:
        binary = Path(tmp) / "swift-parity"
        completed = subprocess.run(
            [
                swiftc,
                str(ROOT / "Sources/voice-switch/Config.swift"),
                str(ROOT / "Sources/voice-switch/Transcript.swift"),
                str(ROOT / "Sources/voice-switch/Segmenter.swift"),
                str(SWIFT_HARNESS),
                "-o",
                str(binary),
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=120,
        )
        if completed.returncode != 0:
            result.fail("Swift pure harness failed to compile:\n" + completed.stdout + completed.stderr)
            return
        completed = subprocess.run(
            [str(binary), str(TEXT_FIXTURE), str(SEGMENTER_FIXTURE)],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=120,
        )
        if completed.returncode == 0:
            for line in completed.stdout.splitlines():
                if line.startswith("PASS "):
                    result.ok(f"Swift pure {line[5:]}")
            if "PASS " not in completed.stdout:
                result.ok("Swift pure harness completed")
        else:
            result.fail("Swift pure harness failed:\n" + completed.stdout + completed.stderr)


def core_project() -> str:
    return textwrap.dedent(
        """\
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <RollForward>LatestMajor</RollForward>
            <LangVersion>latest</LangVersion>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """
    )


def harness_project(core: Path) -> str:
    return textwrap.dedent(
        f"""\
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <TargetFramework>net8.0</TargetFramework>
            <RollForward>LatestMajor</RollForward>
            <LangVersion>latest</LangVersion>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Program.cs" />
            <ProjectReference Include="{core}" />
          </ItemGroup>
        </Project>
        """
    )


def harness_program(text_fixture: dict, segmenter_fixture: dict) -> str:
    text_json = json.dumps(text_fixture, ensure_ascii=False)
    segmenter_json = json.dumps(segmenter_fixture, ensure_ascii=False)
    return textwrap.dedent(
        f"""\
        using System.Text.Json;
        using VoiceSwitch.Windows.Core;

        var textFixture = JsonDocument.Parse({cs_string(text_json)}).RootElement;
        var segmenterFixture = JsonDocument.Parse({cs_string(segmenter_json)}).RootElement;
        var failures = new List<string>();

        foreach (var item in textFixture.GetProperty("normalization").EnumerateArray())
        {{
            var actual = TextMatching.Normalize(item.GetProperty("input").GetString()!);
            var expected = item.GetProperty("expected").GetString();
            Check(actual == expected, $"normalize {{item.GetProperty("name").GetString()}} expected={{expected}} actual={{actual}}");
        }}

        var configElement = textFixture.GetProperty("decision_config");
        var config = new VoiceSwitchConfig(
            configElement.GetProperty("wakeWords").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            configElement.GetProperty("locale").GetString(),
            configElement.GetProperty("command").GetString()!,
            MaxSeconds: configElement.GetProperty("maxSeconds").GetDouble(),
            HangoverMs: configElement.GetProperty("hangoverMs").GetInt32(),
            PrerollMs: configElement.GetProperty("prerollMs").GetInt32(),
            MinSpeechMs: configElement.GetProperty("minSpeechMs").GetInt32(),
            VadRatio: configElement.GetProperty("vadRatio").GetSingle(),
            VadMinRMS: configElement.GetProperty("vadMinRMS").GetSingle(),
            StopWords: configElement.GetProperty("stopWords").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            StopCommand: configElement.GetProperty("stopCommand").GetString(),
            Dictation: new DictationConfig(
                configElement.GetProperty("dictation").GetProperty("recordingsDir").GetString(),
                configElement.GetProperty("dictation").GetProperty("endSilenceMs").GetInt32(),
                configElement.GetProperty("dictation").GetProperty("maxSeconds").GetDouble(),
                configElement.GetProperty("dictation").GetProperty("excludeBundleIDs").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                configElement.GetProperty("dictation").GetProperty("startTimeoutMs").GetInt32()));

        foreach (var item in textFixture.GetProperty("decisions").EnumerateArray())
        {{
            var decision = TextMatching.Decide(item.GetProperty("recognized").GetString()!, config);
            Check(decision.Kind == item.GetProperty("kind").GetString(), $"decision kind {{item.GetProperty("name").GetString()}}");
            Check(decision.Command == OptionalString(item, "command"), $"decision command {{item.GetProperty("name").GetString()}}");
            Check(decision.Reason == item.GetProperty("reason").GetString(), $"decision reason {{item.GetProperty("name").GetString()}}");
            Check(decision.Text == item.GetProperty("text").GetString(), $"decision text {{item.GetProperty("name").GetString()}}");
        }}

        var segmenterConfig = new VoiceSwitchConfig(["test"], null, "true", MaxSeconds: 2.5, HangoverMs: 300, PrerollMs: 300, MinSpeechMs: 300, VadRatio: 3, VadMinRMS: 0.005f);
        CheckSegmenter(segmenterFixture.GetProperty("selftest"), segmenterConfig);
        CheckSegmenter(segmenterFixture.GetProperty("head"), segmenterConfig);

        if (failures.Count > 0)
        {{
            foreach (var failure in failures) Console.Error.WriteLine("FAIL " + failure);
            return 1;
        }}

        Console.WriteLine("PASS behavior fixtures");
        return 0;

        void Check(bool condition, string message)
        {{
            if (!condition) failures.Add(message);
        }}

        static string? OptionalString(JsonElement item, string name)
        {{
            var value = item.GetProperty(name);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
        }}

        void CheckSegmenter(JsonElement spec, VoiceSwitchConfig segmenterConfig)
        {{
            var segmenter = new Segmenter(segmenterConfig);
            var quiet = Enumerable.Repeat(0.0001f, Segmenter.FrameLength).ToArray();
            var loud = Enumerable.Range(0, Segmenter.FrameLength).Select(i => (float)(0.2 * Math.Sin(i * 0.5))).ToArray();
            SegmenterEvent? seen = null;
            for (var i = 0; i < spec.GetProperty("quietFramesBefore").GetInt32(); i++) segmenter.Push(quiet);
            for (var i = 0; i < spec.GetProperty("loudFrames").GetInt32(); i++)
            {{
                seen ??= segmenter.Push(loud);
            }}
            if (spec.TryGetProperty("quietFramesAfter", out var after))
            {{
                for (var i = 0; i < after.GetInt32(); i++) seen ??= segmenter.Push(quiet);
            }}
            var expectedKind = spec.GetProperty("expectedKind").GetString();
            Check(seen?.Kind == expectedKind, $"segmenter expected {{expectedKind}} actual {{seen?.Kind ?? "<none>"}}");
            Check((seen?.Samples.Length ?? 0) >= spec.GetProperty("minSamples").GetInt32(), $"segmenter sample count for {{expectedKind}}");
        }}
        """
    )


def cs_string(value: str) -> str:
    return json.dumps(value)


def check_mutation_gate(result: Result, repo: SourceTree, contract: dict) -> None:
    target = "dotnet/VoiceSwitch.Windows.Core/TextMatching.cs"
    original = repo.read(target)
    stop_block = """\
        if ((config.StopWords ?? []).Select(Normalize).Contains(text))
        {
            if (string.IsNullOrWhiteSpace(config.StopCommand)
                || string.Equals(config.StopCommand.Trim(), PlatformDefaults.SuperwhisperToggle, StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeDecision.Ignore(text, "stop command disabled");
            }

            return RuntimeDecision.RunCommand(text, config.StopCommand, "stop");
        }

"""
    mutated = original.replace(stop_block, "", 1)
    if mutated == original:
        result.fail("mutation setup failed: could not remove Windows stop-word handler in memory")
        return
    probe = Result()
    mutated_repo = SourceTree(ROOT, {target: mutated})
    stop_capability = {
        "shared_capabilities": [
            cap for cap in contract["shared_capabilities"] if cap["id"] == "whole_utterance_stop_runs_stop_command"
        ]
    }
    check_capability_markers(probe, mutated_repo, stop_capability)
    if probe.failures:
        result.ok("mutation gate fails when the Windows stop-word handler is removed from an in-memory source copy")
    else:
        result.fail("mutation gate did not fail after removing the Windows stop-word handler")

    original_probe = Result()
    check_capability_markers(original_probe, repo, stop_capability)
    if original_probe.failures:
        result.fail("original Windows stop-word handler does not pass after mutation check")
    else:
        result.ok("original source still passes after mutation check")

    check_runtime_call_site_mutation(result, repo)
    check_windows_selftest_segmenter_mutation(result, repo)
    check_platform_only_cli_mutation(result, repo, contract)
    check_platform_only_config_mutation(result, repo)
    check_commented_implementation_mutation(result, repo, contract)
    check_dictation_capability_mutations(result, repo, contract)
    check_dictation_test_marker_mutation(result, repo)


def check_runtime_call_site_mutation(result: Result, repo: SourceTree) -> None:
    target = "dotnet/VoiceSwitch.Windows/Program.cs"
    original = repo.read(target)
    mutated = original.replace("runCommand(decision.Command);", "")
    if mutated == original:
        result.fail("mutation setup failed: could not remove Windows runtime command dispatch call site")
        return
    probe = Result()
    check_windows_runtime_call_chain(probe, SourceTree(ROOT, {target: mutated}))
    if probe.failures:
        result.ok("mutation gate fails when Windows runtime command call site is removed while TextMatching remains")
    else:
        result.fail("mutation gate did not fail after removing Windows runtime command call site")


def check_windows_selftest_segmenter_mutation(result: Result, repo: SourceTree) -> None:
    target = "dotnet/VoiceSwitch.Windows/Program.cs"
    original = repo.read(target)
    mutated = original.replace("        if (!VerifySegmenter())", "        if (false && !VerifySegmenter())", 1)
    if mutated == original:
        result.fail("mutation setup failed: could not bypass Windows self-test Segmenter call")
        return
    probe = Result()
    check_windows_cli_call_chain(probe, SourceTree(ROOT, {target: mutated}))
    if probe.failures:
        result.ok("mutation gate fails when Windows self-test Segmenter verification is bypassed")
    else:
        result.fail("mutation gate did not fail after bypassing Windows self-test Segmenter verification")


def check_platform_only_cli_mutation(result: Result, repo: SourceTree, contract: dict) -> None:
    target = "dotnet/VoiceSwitch.Windows.Core/CliOptions.cs"
    original = repo.read(target)
    mutated = original.replace('case "--help":', 'case "--windows-only":\n                case "--help":', 1)
    if mutated == original:
        result.fail("mutation setup failed: could not add a Windows-only CLI switch")
        return
    probe = Result()
    check_cli_contract(probe, SourceTree(ROOT, {target: mutated}), contract, load_json(CLI_FIXTURE))
    if probe.failures:
        result.ok("mutation gate fails when a platform-only CLI switch is added without classification")
    else:
        result.fail("mutation gate did not fail after adding a platform-only CLI switch")


def check_platform_only_config_mutation(result: Result, repo: SourceTree) -> None:
    target = "dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs"
    original = repo.read(target)
    mutated = original.replace("NoiseReductionOptions? NoiseReduction = null)", "NoiseReductionOptions? NoiseReduction = null,\n    string? WindowsOnly = null)", 1)
    if mutated == original:
        result.fail("mutation setup failed: could not add a Windows-only config member")
        return
    probe = Result()
    check_config_fields(probe, SourceTree(ROOT, {target: mutated}))
    if probe.failures:
        result.ok("mutation gate fails when a platform-only config member is added without classification")
    else:
        result.fail("mutation gate did not fail after adding a platform-only config member")


def check_commented_implementation_mutation(result: Result, repo: SourceTree, contract: dict) -> None:
    target = "dotnet/VoiceSwitch.Windows.Core/TextMatching.cs"
    original = repo.read(target)
    mutated = original.replace(
        'return RuntimeDecision.RunCommand(text, config.StopCommand, "stop");',
        '// return RuntimeDecision.RunCommand(text, config.StopCommand, "stop");',
        1,
    )
    if mutated == original:
        result.fail("mutation setup failed: could not comment out Windows stop-word implementation")
        return
    probe = Result()
    stop_capability = {
        "shared_capabilities": [
            cap for cap in contract["shared_capabilities"] if cap["id"] == "whole_utterance_stop_runs_stop_command"
        ]
    }
    check_capability_markers(probe, SourceTree(ROOT, {target: mutated}), stop_capability)
    if probe.failures:
        result.ok("mutation gate fails when a required implementation is only left in a comment")
    else:
        result.fail("mutation gate did not fail after commenting out a required implementation")


def check_dictation_capability_mutations(result: Result, repo: SourceTree, contract: dict) -> None:
    cases = [
        ("dictation_runtime_routes_to_pcm_runtime", "dotnet/VoiceSwitch.Windows/Program.cs", "new WindowsDictationRuntime"),
        ("dictation_wake_prefix_trims_audio_body", "dotnet/VoiceSwitch.Windows.Core/DictationCore.cs", "LeadingWake"),
        ("dictation_closed_stop_guard", "dotnet/VoiceSwitch.Windows.Core/DictationCore.cs", "StandaloneStopRange"),
        ("dictation_wav_encode", "dotnet/VoiceSwitch.Windows.Core/DictationCore.cs", "writer.Write(16000)"),
        ("dictation_handoff_lifecycle", "dotnet/VoiceSwitch.Windows/SuperwhisperHandoff.cs", "\"llmResult\", \"result\""),
        ("dictation_one_handoff_at_a_time", "dotnet/VoiceSwitch.Windows/DictationRuntime.cs", "dictation dropped: previous one still in flight"),
    ]
    for capability_id, target, marker in cases:
        original = repo.read(target)
        mutated = original.replace(marker, "")
        if mutated == original:
            result.fail(f"mutation setup failed: could not remove {marker} for {capability_id}")
            continue
        probe = Result()
        capability = {
            "shared_capabilities": [
                cap for cap in contract["shared_capabilities"] if cap["id"] == capability_id
            ]
        }
        check_capability_markers(probe, SourceTree(ROOT, {target: mutated}), capability)
        if probe.failures:
            result.ok(f"mutation gate fails when {capability_id} marker is removed")
        else:
            result.fail(f"mutation gate did not fail after removing {capability_id} marker")


def check_dictation_test_marker_mutation(result: Result, repo: SourceTree) -> None:
    target = "dotnet/VoiceSwitch.Windows.Tests/Program.cs"
    original = repo.read(target)
    mutated = original.replace("DictationRuntimeKeepsBodyWhileRecognitionIsDelayed", "RemovedDelayedRecognitionTest")
    if mutated == original:
        result.fail("mutation setup failed: could not remove dictation delayed recognition test marker")
        return
    probe = Result()
    check_dictation_test_markers(probe, SourceTree(ROOT, {target: mutated}))
    if probe.failures:
        result.ok("mutation gate fails when the delayed-recognition body persistence test is removed")
    else:
        result.fail("mutation gate did not fail after removing the delayed-recognition body persistence test")


if __name__ == "__main__":
    raise SystemExit(main())
