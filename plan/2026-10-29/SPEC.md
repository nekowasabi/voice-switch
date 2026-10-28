# SPEC: Grok Bot send-message

Environment for every command:

```sh
cd /workspace/voice-switch
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
V=.claude/skills/verify-voice-switch-grokbot/verify.sh
```

Each item is one command (or a short script) and one pass condition. Items marked **baseline-green** pass on origin/main. Items marked **until-implemented** fail until slice 1 lands. Items marked **vacuous-at-baseline** pass on an empty product diff; they become binding once those files exist.

## 1. Windows Release build — baseline-green

```sh
dotnet build dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj -c Release -r win-x64 --no-restore
```

Pass: the output contains `0 Warning(s)` and `0 Error(s)` and `Build succeeded.` Exit 0.

## 2. Full Windows.Tests run — until-implemented (count)

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore | tee /tmp/vs-grokbot-tests.log
```

Pass: exit 0. The log contains no line matching `^FAIL `. The number of lines matching `^PASS ` is at least 217 (211 on origin/main plus the six named tests in items 4–9).

## 3. Existing action URL test still passes — baseline-green

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "wake action URL carries the text as one input item"
```

Pass: stdout contains `PASS wake action URL carries the text as one input item` and no `FAIL`. Exit 0.

## 4. Default `inputParam` stays byte-identical — until-implemented

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "wake action URL with default inputParam stays byte-identical to input="
```

Pass: stdout contains `PASS wake action URL with default inputParam stays byte-identical to input=` and no `FAIL`. Exit 0.

The test must compare the composed URL bytes to today's `InputUrl` for a URL that already has `?input=Sample%20text&k=v` and for a URL with no query, using the same sentence as `WakeActionUrlCarriesInput` (`今日 a&b=c+d#e?f`).

## 5. `inputParam=text` — until-implemented

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "wake action inputParam=text puts the dictation in text="
```

Pass: stdout contains `PASS wake action inputParam=text puts the dictation in text=` and no `FAIL`. Exit 0.

The composed URL for `grokbot://app/v1/prompt?id=YOUR_BOT_ID` and text `hello world` must be exactly `grokbot://app/v1/prompt?id=YOUR_BOT_ID&text=hello%20world`.

## 6. Newline collapse — until-implemented

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "grokbot prompt collapses CR LF tab runs to one space and trims"
```

Pass: stdout contains `PASS grokbot prompt collapses CR LF tab runs to one space and trims` and no `FAIL`. Exit 0.

Input `hello\r\n\tworld\n` on a grokbot prompt URL with `inputParam=text` must produce `text=hello%20world`. Regular ASCII spaces that are not CR/LF/tab are left in place before the final trim.

## 7. 1000-character encoded cap — until-implemented

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "grokbot prompt caps percent-encoded text at 1000 without splitting %XX or UTF-8"
```

Pass: stdout contains `PASS grokbot prompt caps percent-encoded text at 1000 without splitting %XX or UTF-8` and no `FAIL`. Exit 0.

A grokbot prompt URL with 400 × `今` must produce a `text=` value of length <= 1000, a multiple of 9 (`%E4%BB%8A`), with every `%` followed by two hex digits. A non-grokbot action URL must not apply this cap.

## 8. Invalid `inputParam` rejected at load — until-implemented

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "config rejects invalid inputParam at load"
```

Pass: stdout contains `PASS config rejects invalid inputParam at load` and no `FAIL`. Exit 0.

`ConfigLoader.Load` must throw `InvalidDataException` mentioning `inputParam` for empty string, whitespace, `=`, `&`, `%`, and any character outside RFC 3986 unreserved (`A–Z a–z 0–9 - . _ ~`). Omitted `inputParam` and explicit `"input"` must load.

## 9. Automatic session never opens an action URL — until-implemented

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "automatic session never opens an action URL"
```

Pass: stdout contains `PASS automatic session never opens an action URL` and no `FAIL`. Exit 0.

The test must cover both: `StartAutomatic` with `windows.actions` containing grokbot finishes with `Action == null`; and `RegisteredSuperwhisperHandoff` given automatic origin plus a grokbot `Action` never invokes rundll32 / `LaunchUrl`.

## 10. Parity — baseline-green on fail=0; pass count may rise

```sh
python3 tests/parity/run_parity.py
```

Pass: the last line is `SUMMARY pass=N allow=23 fail=0` with `N >= 118`. Do not add `allowed_capability_gaps` for this feature. New `shared_capabilities` rows are required (see item 16).

## 11. Python unit tests — baseline-green

```sh
python3 -m unittest discover -s tests -p 'test_*.py'
```

Pass: stdout contains `OK`. Exit 0.

## 12. url-actions: no new failure — baseline-green (pre-existing fail allowed)

```sh
python3 - <<'PY'
import subprocess, sys
p = subprocess.run(["python3", "tests/url-actions/run.py"], capture_output=True, text=True)
out = p.stdout + p.stderr
if p.returncode == 0:
    sys.exit(0)
ok = (
    p.returncode == 1
    and 'assert "guard modeRequested, superwhisperAutoPasteDisabled" in handoff' in out
    and out.count("AssertionError") == 1
)
sys.exit(0 if ok else 1)
PY
```

Pass: exit 0. That is either a full pass, or the single pre-existing assertion on `superwhisperAutoPasteDisabled` in `MacApp.swift` `handoff`. Any other assertion, traceback, or exit code fails this item. Do not change `tests/url-actions/run.py` to make it pass.

## 13. dotnet format on touched product `.cs` files — vacuous-at-baseline

```sh
python3 - <<'PY'
import subprocess, sys
from pathlib import Path
files = subprocess.check_output(["git", "diff", "--name-only", "origin/main", "--", "dotnet"], text=True).splitlines()
cs = [f for f in files if f.endswith(".cs")]
if not cs:
    print("no product .cs files in diff; format not binding yet")
    sys.exit(0)
failed = 0
for f in cs:
    path = Path(f)
    if path.parts[1] == "VoiceSwitch.Windows.Core":
        csproj = "dotnet/VoiceSwitch.Windows.Core/VoiceSwitch.Windows.Core.csproj"
    elif path.parts[1] == "VoiceSwitch.Windows.Tests":
        csproj = "dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj"
    elif path.parts[1] == "VoiceSwitch.Windows":
        csproj = "dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj"
    else:
        print("unmapped", f, file=sys.stderr)
        failed += 1
        continue
    cmd = ["dotnet", "format", csproj, "--verify-no-changes", "--no-restore", "--include", path.name]
    r = subprocess.run(cmd)
    if r.returncode != 0:
        failed += 1
sys.exit(failed)
PY
```

Pass: exit 0. Do not run `dotnet format` on the whole tree (pre-existing whitespace errors elsewhere).

## 14. Example config loads — until-implemented

```sh
$V build && $V doctor && VS_RUN_ID=spec-14 $V drive grokbot-config-docs
```

Pass: exit 0. Evidence stdout contains `name=grokbot`. `config.example.grokbot.json` contains `YOUR_BOT_ID` and `"inputParam": "text"` in both `macOS.actions` and `windows.actions`. `ConfigLoader` accepts the file.

## 15. Swift / C# grokbot mirror — until-implemented

```sh
$V swift-mirror grokbot
```

Pass: exit 0 and stdout `ok grokbot 8 markers`. The markers are `inputParam` and `GrokBotPrompt` and `/v1/prompt` in `Sources/voice-switch/Config.swift`; `InputParam`, `GrokBotPrompt`, and `/v1/prompt` in `dotnet/VoiceSwitch.Windows.Core/*.cs`; `truncated` in `Sources/voice-switch/MacApp.swift` and in `dotnet/VoiceSwitch.Windows/SuperwhisperHandoff.cs`. A bare `1000` is not a marker (it already appears in followupMs validation). The 1000-character cap is items 7 and 16.

`command -v swiftc` is empty on this box. There is no Swift syntax checker. Mac code is Mac-unverified; this item is source presence only.

## 16. Parity contracts for the new behavior — until-implemented

```sh
python3 - <<'PY'
import json, sys
from pathlib import Path
c = json.loads(Path("tests/parity/contracts/platform_parity.json").read_text())
ids = {x["id"] for x in c["shared_capabilities"]}
need = {"wake_action_input_param", "grokbot_prompt_text_limits"}
missing = need - ids
sys.exit(1 if missing else 0)
PY
python3 tests/parity/run_parity.py
```

Pass: first script exit 0. Second script `fail=0`. Each new capability lists Swift and C# files plus markers that include `inputParam`/`InputParam` for the first and `grokbot`, `/v1/prompt`, `1000` for the second.

## 17. No secrets in the diff vs origin/main — baseline-green after this commit; binding after implementation

```sh
python3 - <<'PY'
import re, subprocess, sys
diff = subprocess.check_output(["git", "diff", "origin/main", "--"], text=True, errors="replace")
pat = re.compile(r"(sk-|xai-|Bearer )[A-Za-z0-9_\-]{16,}")
sys.exit(1 if pat.search(diff) else 0)
PY
```

Pass: exit 0. Do not print the diff. Placeholder `YOUR_BOT_ID` is required in the example config (item 14) and is not a secret.

## 18. Docs exist with the documented contract — until-implemented

```sh
test -f docs/grokbot-message.md
grep -q 'documented, not live-tested' docs/grokbot-message.md
grep -q 'grokbot://app/v1/prompt' docs/grokbot-message.md
grep -q 'YOUR_BOT_ID' docs/grokbot-message.md
grep -q '1000' docs/grokbot-message.md
grep -q 'webhook' docs/grokbot-message.md
grep -q 'Mac-unverified\|Mac unverified\|macOS runtime is unverified\|Mac 実機' docs/grokbot-message.md
grep -q 'Grok Bot' README.md
grep -q 'Grok Bot' README_ja.md
grep -q 'invoke an action URL' docs/wake-acceptance.md
```

Pass: every command exits 0. `docs/grokbot-message.md` states the prompt link never auto-sends, fills an empty composer only, needs a Grok Bot build that includes prompt links, and names routine webhooks as a future option only. It describes `--fire` as a handler check of `command` / `wakeURL` (it does not fill `text=`) and speaking the grokbot wake word as the path that fills the composer.

## 19. Skill probe: existing `input=` URL — baseline-green after this commit

```sh
$V build && $V doctor && VS_RUN_ID=spec-19 $V drive wake-action-url
```

Pass: exit 0. Evidence `stdout.txt` contains:

```
GOT alter://action/26B00BAB-5655-4581-BCA0-F3880E746CC9?input=hello%20world
WANT alter://action/26B00BAB-5655-4581-BCA0-F3880E746CC9?input=hello%20world
MATCH
```

`$V cleanup` deletes the build directory and leaves that evidence folder in place.

## 20. Skill probe: grokbot `text=` URL — until-implemented

```sh
$V build && $V doctor && VS_RUN_ID=spec-20 $V drive grokbot-prompt-link
```

Pass: exit 0. Evidence `stdout.txt` contains:

```
GOT grokbot://app/v1/prompt?id=YOUR_BOT_ID&text=hello%20world
WANT grokbot://app/v1/prompt?id=YOUR_BOT_ID&text=hello%20world
MATCH
```

A `GOT` that uses `input=` is a fail. Do not change `WANT`.

## 21. Skill probe: grokbot text limits — until-implemented

```sh
$V build && $V doctor && VS_RUN_ID=spec-21 $V drive grokbot-text-limits
```

Pass: exit 0. The drive encodes newline collapse to `hello%20world` and a 400 × `今` cap as in item 7.

## 22. Skill probe: automatic-session test — until-implemented

```sh
$V build && $V doctor && VS_RUN_ID=spec-22 $V drive automatic-no-action-url
```

Pass: exit 0. Evidence stdout contains `PASS automatic session never opens an action URL`.

## 23. New VoiceSwitch.Windows sources are in the Tests csproj — vacuous-at-baseline

```sh
python3 - <<'PY'
import subprocess, sys
from pathlib import Path
diff = subprocess.check_output(["git", "diff", "--name-only", "origin/main", "--", "dotnet/VoiceSwitch.Windows"], text=True).splitlines()
new_cs = [f for f in diff if f.endswith(".cs") and f.startswith("dotnet/VoiceSwitch.Windows/") and "/Tray/" not in f]
# files already Compile-included in VoiceSwitch.Windows.Tests.csproj, plus Tray extras
csproj = Path("dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj").read_text()
missing = []
for f in new_cs:
    name = Path(f).name
    if name not in csproj and f not in csproj:
        # already-tracked files edited in place do not need a new Include
        status = subprocess.check_output(["git", "diff", "--diff-filter=A", "--name-only", "origin/main", "--", f], text=True).strip()
        if status:
            missing.append(f)
sys.exit(1 if missing else 0)
PY
```

Pass: exit 0. New `.cs` files under `dotnet/VoiceSwitch.Windows/` (not Core, not Tests) must appear in `VoiceSwitch.Windows.Tests.csproj` `<Compile Include>`. Core and Tests SDK compile is enough.

## 24. win-x64 `--fire` is not a grokbot URL printer — environment fact

```sh
command -v swiftc; echo swiftc_exit=$?
dotnet build dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj -c Release -r win-x64 --no-restore >/dev/null
# the produced exe is win-x64; running --fire here is not required and is not proof
```

Pass: `swiftc_exit` is 1. Record Mac-unverified and Linux-cannot-launch-grokbot. This item does not prove the feature; items 5, 19, and 20 do.
