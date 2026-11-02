# SPEC: wake recognition surrogates

Environment for every command, from the repo root:

```sh
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
```

Each numbered item is one command and one pass condition. Run the items one at a time. Item 8 rewrites `DictationCore.cs` and restores it before it exits; do not build Core in another process while it runs.

Every numbered item is **baseline-green** on commit `docs(verify): add wake verification skill and SPEC/PLAN` (branch `improve/wake-hillclimb-base`). A later slice must keep every item green. Raising a hit count stays green. Adding a false wake, dropping hits below the floor, or editing the corpus bytes does not.

Real-audio recognition rate, Mac, and Windows hardware are **UNVERIFIED**. They are not numbered items. A passing item is not a claim that more spoken wake words were heard.

## 1. Windows Release build — baseline-green

```sh
python3 - <<'PY'
import re, subprocess, sys
p = subprocess.run(
    ["dotnet", "build", "dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj",
     "-c", "Release", "-r", "win-x64", "--no-restore"],
    capture_output=True, text=True)
log = p.stdout + p.stderr
warns = re.findall(r"(\d+) Warning\(s\)", log)
errors = re.findall(r"(\d+) Error\(s\)", log)
print(f"build_exit={p.returncode} warnings={warns[-1] if warns else 'missing'} errors={errors[-1] if errors else 'missing'}")
ok = p.returncode == 0 and warns and errors and warns[-1] == "0" and errors[-1] == "0" and "Build succeeded." in log
sys.exit(0 if ok else 1)
PY
```

Pass: script exit 0. The last `Warning(s)` count is 0 and the last `Error(s)` count is 0. Baseline on this host is 0 warnings and 0 errors. "No new warnings" means the last warning count stays 0.

## 2. Windows.Tests — baseline-green

```sh
python3 - <<'PY'
import subprocess, sys
p = subprocess.run(
    ["dotnet", "run", "--project", "dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj",
     "-c", "Release", "--no-restore"],
    capture_output=True, text=True)
log = p.stdout + p.stderr
lines = log.splitlines()
passes = sum(1 for line in lines if line.startswith("PASS "))
fails = sum(1 for line in lines if line.startswith("FAIL "))
print(f"test_exit={p.returncode} pass={passes} fail={fails}")
sys.exit(0 if p.returncode == 0 and fails == 0 and passes >= 238 else 1)
PY
```

Pass: script exit 0. `test_exit=0`, `fail=0`, `pass>=238`. PASS lines are counted from stdout and stderr together. Tray fixture lines that contain `Error:` are not `FAIL` lines. SKIP may stay at 11; this item does not lock SKIP.

## 3. Parity — baseline-green

```sh
python3 - <<'PY'
import re, subprocess, sys
p = subprocess.run(["python3", "tests/parity/run_parity.py"], capture_output=True, text=True)
log = p.stdout + p.stderr
found = re.findall(r"SUMMARY pass=(\d+) allow=(\d+) fail=(\d+)", log)
print(log[log.rfind("SUMMARY"):].splitlines()[0] if "SUMMARY" in log else "no SUMMARY")
print(f"parity_exit={p.returncode}")
ok = False
if p.returncode == 0 and found:
    passed, _allow, failed = (int(n) for n in found[-1])
    ok = failed == 0 and passed >= 124
sys.exit(0 if ok else 1)
PY
```

Pass: script exit 0. The last `SUMMARY` line has `fail=0` and `pass>=124`. Baseline text is `SUMMARY pass=124 allow=23 fail=0`. `allow` is not locked.

## 4. Python unittest — baseline-green

```sh
python3 - <<'PY'
import re, subprocess, sys
p = subprocess.run(
    ["python3", "-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py"],
    capture_output=True, text=True)
log = p.stdout + p.stderr
ran = re.findall(r"Ran (\d+) tests?", log)
print(f"unittest_exit={p.returncode} ran={ran[-1] if ran else 'missing'}")
ok = p.returncode == 0 and ran and int(ran[-1]) >= 40 and re.search(r"(?m)^OK$", log) and "FAILED" not in log
sys.exit(0 if ok else 1)
PY
```

Pass: script exit 0. Unittest writes `Ran N tests` and `OK` to stderr. N is at least 40. `FAILED` is absent. Baseline is `Ran 40 tests` and `OK`.

## 5. Formatter on the corpus scorer — baseline-green

```sh
dotnet format .claude/skills/verify-voice-switch-wake/corpus/WakeCorpus.csproj --include .claude/skills/verify-voice-switch-wake/corpus/Program.cs --verify-no-changes --no-restore
```

Pass: exit 0. This item does not format `dotnet/VoiceSwitch.Windows/Tray/DictationHud.cs` or `Tray/WindowFocus.cs`. Those files already have whitespace findings and are outside this work.

A slice that edits other C# files must also leave those files clean under `dotnet format <csproj> --include <those files> --verify-no-changes --no-restore`. That extra include is part of the slice, not a second baseline command.

## 6. Recognition surrogate — baseline-green

The yardstick is `.claude/skills/verify-voice-switch-wake/corpus/corpus.json` (47 rows). Its sha256 is `97fbaf6f615971d59eb0b509560c4939027d7b813a24f9eaf46765bacc8da593`. The scorer calls production `LeadingWake`, `EarlyWake`, and `RejectedWake`.

Measured on this matcher, before any slice: configured readings `hits=15 misses=8 false_wakes=0 rejects=24`; surface-only readings `hits=7 misses=16 false_wakes=0 rejects=24`.

The printed floors stay `hits>=15` and `hits>=7` so a new hit does not fail the scorer. With 23 `expect=wake` rows, `misses<=8` is the same number as `hits>=15`. Those totals do not stop a swap. The script's `must_hit` sets are the 15 configured hits and 7 surface hits measured on this commit. Losing one of those ids fails the item even when the totals still clear the floor. A false wake fails the item. A relabeled corpus fails the sha256. The floors are not vacuous: 15 and 7 are the hit counts this frozen file produces today.

```sh
python3 - <<'PY'
import hashlib, os, re, subprocess, sys, time
from pathlib import Path
repo = Path(".").resolve()
corpus = repo / ".claude/skills/verify-voice-switch-wake/corpus/corpus.json"
sha = hashlib.sha256(corpus.read_bytes()).hexdigest()
started = time.time()
env = os.environ.copy()
env["VS_RUN_ID"] = "spec-reading-match"
env["VS_WAKE_VERIFY_ROOT"] = "/tmp/voice-switch-wake-verify"
p = subprocess.run(
    ["bash", ".claude/skills/verify-voice-switch-wake/verify.sh", "drive", "reading-match"],
    env=env, capture_output=True, text=True)
out_path = Path("/tmp/voice-switch-wake-verify/evidence/spec-reading-match/reading-match/stdout.txt")
text = out_path.read_text(encoding="utf-8") if out_path.is_file() else ""
code_path = out_path.with_name("exitcode.txt")
child = code_path.read_text(encoding="utf-8").strip() if code_path.is_file() else "missing"
dll = Path("/tmp/voice-switch-wake-verify/build/wake-corpus.dll")
dll_fresh = dll.is_file() and dll.stat().st_mtime >= started - 1

# Ids that are hits on this docs commit. A later slice that turns a miss into a
# hit adds that id here in the same commit. Do not remove an id.
must_hit = {
    "configured": {
        "exact-split", "exact-body", "paused-short", "katakana-fold", "halfwidth-kana",
        "hosei-d2", "hosei-spaced", "hosei-short-reading", "yoon-large", "choon",
        "desu-long", "onsen-whole", "onsen-ni-hairu", "early-exact", "rejected-kanji",
        "hosei-wa", "hosei-ne", "hosei-yo",
        "onsei-desu", "onsei-masu",
        "houzei-alt",
        "rejected-kana",
    },
    "surface": {
        "exact-split", "exact-body", "paused-short", "desu-long", "onsen-whole",
        "early-exact", "rejected-kanji",
        "katakana-fold", "halfwidth-kana",
        "hosei-d2", "hosei-spaced", "hosei-short-reading", "yoon-large", "choon",
        "onsen-ni-hairu",
        "hosei-wa", "hosei-ne", "hosei-yo",
        "onsei-desu", "onsei-masu",
        "houzei-alt",
        "rejected-kana",
    },
}

def outcomes(rule: str) -> dict[str, str]:
    found = {}
    for line in text.splitlines():
        m = re.match(
            rf"^row {rule} (\S+) expect=(?:wake|no-wake) got=(?:wake|no-wake) (hit|miss|false_wake|reject)$",
            line)
        if m:
            found[m.group(1)] = m.group(2)
    return found

configured = outcomes("configured")
surface = outcomes("surface")
lost = {
    rule: sorted(ids - {row for row, kind in outcomes(rule).items() if kind == "hit"})
    for rule, ids in must_hit.items()
}
false_wakes = [
    f"{rule} {row}"
    for rule, rows in (("configured", configured), ("surface", surface))
    for row, kind in rows.items()
    if kind == "false_wake"
]
need = [
    f"corpus_sha256={sha}",
    "rows=47",
    "UNVERIFIED real-audio mac windows-hardware",
    "floor configured hits>=15 misses<=8 false_wakes=0",
    "floor surface hits>=7 misses<=16 false_wakes=0",
]
print(f"drive_exit={p.returncode} child_exit={child} sha={sha} dll_fresh={dll_fresh}")
print(f"configured_rows={len(configured)} surface_rows={len(surface)} lost={lost} false_wakes={false_wakes}")
built = "built /tmp/voice-switch-wake-verify/build/wake-corpus.dll" in p.stdout
ok = (
    p.returncode == 0
    and child == "0"
    and built
    and dll_fresh
    and sha == "97fbaf6f615971d59eb0b509560c4939027d7b813a24f9eaf46765bacc8da593"
    and all(line in text for line in need)
    and len(configured) == 47
    and len(surface) == 47
    and not any(lost.values())
    and not false_wakes
)
sys.exit(0 if ok else 1)
PY
```

Pass: script exit 0. `drive` rebuilds `wake-corpus.dll` during this run (`dll_fresh=True` and stdout contains `built /tmp/voice-switch-wake-verify/build/wake-corpus.dll`). A DLL left on disk from another matcher does not pass. Every id in `must_hit` is still a `hit` on that rule. No row is a `false_wake`. Totals alone are not the gate: turning one baseline hit into a miss and one baseline miss into a hit keeps `hits>=15` and still fails, because the lost id is listed.

`misses<=8` is the same constraint as `hits>=15` while the file has 23 `expect=wake` rows and no new rows. It is not a second fact. The id list is what stops a swap. A slice that moves a row from `miss` to `hit` adds that id to `must_hit` for that rule in the same commit and does not remove any id. Giving that hit back later then fails this item. Until that edit, this command stays green on a matcher that has not moved a row. Which rows a slice must move are in `PLAN.md`.

Do not read this item as a real-world recognition rate. The phrases are typed recognized-text variants.

## 7. Automatic sessions never paste or open an action URL — baseline-green

```sh
python3 - <<'PY'
import subprocess, sys
names = [
    "automatic session never opens an action URL",
    "manual paste requires every safety observation",
    "automatic delivery tries Superwhisper first and keeps only history; wake delivery skips history",
]
failed = False
for name in names:
    p = subprocess.run(
        ["dotnet", "run", "--project", "dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj",
         "-c", "Release", "--no-restore", "--", "--only", name],
        capture_output=True, text=True)
    lines = (p.stdout + p.stderr).splitlines()
    pass_lines = [line for line in lines if line.startswith("PASS ")]
    good = p.returncode == 0 and pass_lines == [f"PASS {name}"]
    bad = any(line.startswith("FAIL ") for line in lines)
    print(f"safety exit={p.returncode} pass_lines={pass_lines} fail_line={bad}")
    failed = failed or (not good) or bad
sys.exit(1 if failed else 0)
PY
```

Pass: script exit 0. Each `--only` value is the full registered test name, and the only `PASS` line is exactly `PASS` plus that name. A shorter prefix is not accepted. `--only` with no matching test exits 0 and prints nothing; that fails this item.

## 8. WakeReplay fixture self-test — baseline-green

```sh
python3 - <<'PY'
import os, subprocess, sys, time
from pathlib import Path
core = Path("dotnet/VoiceSwitch.Windows.Core/DictationCore.cs")
before = subprocess.check_output(["git", "hash-object", str(core)], text=True).strip()
started = time.time()
env = os.environ.copy()
env["VS_RUN_ID"] = "spec-replay"
env["VS_WAKE_VERIFY_ROOT"] = "/tmp/voice-switch-wake-verify"
p = subprocess.run(
    ["bash", ".claude/skills/verify-voice-switch-wake/verify.sh", "drive", "fixture-replay"],
    env=env, capture_output=True, text=True)
after = subprocess.check_output(["git", "hash-object", str(core)], text=True).strip()
out = Path("/tmp/voice-switch-wake-verify/evidence/spec-replay/fixture-replay/stdout.txt")
text = out.read_text(encoding="utf-8") if out.is_file() else ""
dll = Path("dotnet/VoiceSwitch.WakeReplay/bin/Release/net8.0/VoiceSwitch.WakeReplay.dll")
dll_fresh = dll.is_file() and dll.stat().st_mtime >= started - 1
built = "built " in p.stdout and "VoiceSwitch.WakeReplay.dll" in p.stdout
print(f"replay_exit={p.returncode} core_same={before == after} dll_fresh={dll_fresh} in_band={'in_band=False' in text}")
ok = (
    p.returncode == 0
    and before == after
    and built
    and dll_fresh
    and "OK wake_log_replay self-test" in text
    and "in_band=False" in text
)
sys.exit(0 if ok else 1)
PY
```

Pass: script exit 0. The drive rebuilds `VoiceSwitch.WakeReplay.dll` during this run. Stdout contains `OK wake_log_replay self-test` and `in_band=False`. `DictationCore.cs` bytes after the run hash the same as before the run. The hash check is restore, not "the file still matches origin/main". A later slice may edit that file; this item still passes when the self-test puts those edited bytes back. Do not change the matcher to make `in_band` true.

## 9. Acceptance evaluator on the synthetic pass manifest — baseline-green

```sh
python3 - <<'PY'
import json, subprocess, sys
p = subprocess.run(
    ["python3", "scripts/wake_acceptance.py",
     ".claude/skills/verify-voice-switch-wake/fixtures/pass-manifest.json"],
    capture_output=True, text=True)
try:
    verdict = json.loads(p.stdout)["verdict"]
except (json.JSONDecodeError, KeyError):
    verdict = "unreadable"
print(f"acceptance_exit={p.returncode} verdict={verdict}")
sys.exit(0 if p.returncode == 0 and verdict == "pass" else 1)
PY
```

Pass: script exit 0 and top-level `"verdict"` is `pass`. This is an evaluator self-check on scaffolding. It is not the recognition metric in item 6. An empty manifest is inconclusive (exit 3). A safety incident is exit 1. Neither of those files is this item.

## 10. Near-miss census on the sample log — baseline-green

```sh
python3 - <<'PY'
import os, subprocess, sys, time
from pathlib import Path
started = time.time()
env = os.environ.copy()
env["VS_RUN_ID"] = "spec-near-miss"
env["VS_WAKE_VERIFY_ROOT"] = "/tmp/voice-switch-wake-verify"
p = subprocess.run(
    ["bash", ".claude/skills/verify-voice-switch-wake/verify.sh", "drive", "near-miss"],
    env=env, capture_output=True, text=True)
out = Path("/tmp/voice-switch-wake-verify/evidence/spec-near-miss/near-miss/stdout.txt")
text = out.read_text(encoding="utf-8") if out.is_file() else ""
dll = Path("dotnet/VoiceSwitch.WakeReplay/bin/Release/net8.0/VoiceSwitch.WakeReplay.dll")
dll_fresh = dll.is_file() and dll.stat().st_mtime >= started - 1
need = [
    "agreed_true=2 agreed_false=7 gained=1 lost=0",
    "near_miss_examples_long_wake_d1_2_misses_whole (shown 0, wake.Reading.Length>=10, !hit, whole-utterance):",
    "near_miss_examples_short_wake_d1_misses_whole (shown 0, wake.Reading.Length<10, !hit, whole-utterance, d=1):",
    "near_miss_examples_long_wake_d3_misses_whole (shown 1, wake.Reading.Length>=10, !hit, whole-utterance, d=3):",
    "d3_near_miss_intentional=1 d3_near_miss_ambient=0",
    'text="法政入力" reading="ほうぜいにゅうりょく" wake=おんせいにゅうりょく d=3 intentional=True',
]
missing = [line for line in need if line not in text]
built = "VoiceSwitch.WakeReplay.dll" in p.stdout
print(f"near_miss_exit={p.returncode} dll_fresh={dll_fresh} missing={missing}")
sys.exit(0 if p.returncode == 0 and built and dll_fresh and not missing else 1)
PY
```

Pass: script exit 0. The drive rebuilds WakeReplay during this run. Each string above is a whole field. `gained=1` by itself is not a check: it is also contained in `gained=10` through `gained=19`. The same is true of `shown 1` and `d3_near_miss_intentional=1`. `法政入力` is a synthetic distance-3 example in the fixture, not a count of live misses. A slice that changes this census (only s6 in `PLAN.md`, and only if it lands) updates this list and `verify.sh` in that same commit. Until then these strings stay.

## UNVERIFIED (not commands)

- **Real-audio recognition rate.** This host has no microphone and no speaker. Nothing in items 1–10 plays or records speech. Do not report a real-world wake rate, a miss reduction on live audio, or a user-visible recognition improvement from a green item.
- **Mac.** `swiftc` is not installed. `Sources/` is not built. Parity checks Swift source text only. Mac dictation still matches exact normalized text and does not run the Windows reading matcher.
- **Windows hardware.** The win-x64 build is not launched. SAPI, MS-IME, the tray, and a desktop session are not here. `ImeReadings` returns null off Windows, which is why item 6 scores a surface-only rule separately from the configured readings.
- **`scripts/wake_latency_eval.py`.** It needs macOS `say`, `afconvert`, and `swift`. It is not a gate.
- **`python3 tests/url-actions/run.py`.** Baseline on this host already printed PASSED, including `URL static mutation checks passed: 25`, and skipped `swiftc`. It is not a wake-rate gate and it is not re-specified here.
