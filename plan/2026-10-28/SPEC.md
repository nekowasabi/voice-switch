# Verification items for the .NET structure refactor

Run every item from `/workspace/voice-switch-refactor` with this environment already exported:

```sh
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
```

Do not set `VS_VERIFY_ROOT`. CLI evidence uses `/tmp/voice-switch-dotnet-verify/evidence`. Each item is one `bash` script. Pass means that script exits 0. A non-zero exit fails the item.

`SKIP` lines from the behavior suite are allowed. `FAIL` lines are not. `python3 tests/url-actions/run.py` is item 14 and is a pre-existing failure. Leave it failing. Do not edit `Sources/` or `tests/parity/` to change these results.

## 1. Restore

```bash
set -euo pipefail
{
  dotnet restore dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj -r win-x64 --ignore-failed-sources --disable-parallel
  dotnet restore dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj --ignore-failed-sources --disable-parallel
} > /tmp/vs-refactor-restore.out
test -s /tmp/vs-refactor-restore.out
```

Pass: exit 0. Both restore commands exit 0 and the captured log is non-empty. A warm cache prints `up-to-date for restore` and may print `Restored` only for `VoiceSwitch.Windows.Core.csproj`. The script does not require the word `Restored` on `VoiceSwitch.Windows.csproj` or `VoiceSwitch.Windows.Tests.csproj`.

## 2. WinExe build

```bash
set -euo pipefail
dotnet build dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj -c Release -r win-x64 --no-restore > /tmp/vs-refactor-build.out 2>&1
grep -F -q '0 Warning(s)' /tmp/vs-refactor-build.out
grep -F -q '0 Error(s)' /tmp/vs-refactor-build.out
```

Pass: exit 0. `/tmp/vs-refactor-build.out` contains `0 Warning(s)` and `0 Error(s)`. The csproj does not treat warnings as errors, so this grep is what rejects a warning. A build that only exits 0 is not enough.

## 3. Behavior suite

```bash
set -euo pipefail
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore > /tmp/vs-refactor-tests.out
pass=$(grep -c '^PASS ' /tmp/vs-refactor-tests.out)
fail=$(grep -c '^FAIL ' /tmp/vs-refactor-tests.out || true)
test "$pass" -eq 211
test "$fail" -eq 0
```

Pass: exit 0. That is 211 lines starting with `PASS ` and 0 lines starting with `FAIL `.

## 4. Parity

```bash
set -euo pipefail
python3 tests/parity/run_parity.py > /tmp/vs-refactor-parity.out
grep -F -q 'SUMMARY pass=118 allow=23 fail=0' /tmp/vs-refactor-parity.out
```

Pass: exit 0. `/tmp/vs-refactor-parity.out` contains `SUMMARY pass=118 allow=23 fail=0`. The script itself prints nothing on success.

## 5. Python unit tests

```bash
set -euo pipefail
python3 -m unittest discover -s tests -p 'test_*.py' > /tmp/vs-refactor-py.out 2>&1
grep -F -q 'Ran 40 tests' /tmp/vs-refactor-py.out
grep -F -q 'OK' /tmp/vs-refactor-py.out
```

Pass: exit 0. `/tmp/vs-refactor-py.out` contains `Ran 40 tests` and `OK`. Unittest writes that text to stderr; the script merges it into the file. The script itself prints nothing on success.

## 6. Test project links every top-level Windows source

```bash
python3 - <<'PY'
from pathlib import Path
import xml.etree.ElementTree as ET
repo = Path("/workspace/voice-switch-refactor")
csproj = repo / "dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj"
root = ET.parse(csproj).getroot()
linked = set()
for node in root.iter("Compile"):
    inc = node.attrib.get("Include")
    if inc:
        linked.add(Path(inc.replace("\\", "/")).name)
missing = [path.name for path in sorted((repo / "dotnet/VoiceSwitch.Windows").glob("*.cs")) if path.name not in linked]
if missing:
    raise SystemExit("test csproj is missing Compile Include for: " + ", ".join(missing))
print("top-level windows sources linked: ok")
PY
```

Pass: exit 0, and stdout contains `top-level windows sources linked: ok`. A new `dotnet/VoiceSwitch.Windows/*.cs` file extracted from an existing one fails this item until the test csproj has a `Compile Include` for it.

## 7. Swift sources and parity contracts stay untouched

```bash
set -euo pipefail
diff_out=$(git diff --stat origin/main -- Sources/ tests/parity)
status_out=$(git status --short --untracked-files=all -- Sources/ tests/parity)
test -z "$diff_out"
test -z "$status_out"
```

Pass: exit 0. The diff against `origin/main` is empty, and `git status --short --untracked-files=all` prints nothing for those paths. `git diff` alone ignores untracked files, so the status check is required.

## 8. Existing verification skills stay untouched

```bash
set -euo pipefail
diff_out=$(git diff --stat origin/main -- .claude/skills/verify-voice-switch .claude/skills/verify-voice-switch-mac)
status_out=$(git status --short --untracked-files=all -- .claude/skills/verify-voice-switch .claude/skills/verify-voice-switch-mac)
test -z "$diff_out"
test -z "$status_out"
```

Pass: exit 0. The diff against `origin/main` is empty, and `git status --short --untracked-files=all` prints nothing for those two directories. `.claude/skills/verify-voice-switch-dotnet/` is the new Linux skill and is allowed to differ. Untracked files under the two named directories fail this item.

## 9. Formatter is clean on touched C# files

```bash
set -euo pipefail
mapfile -t files < <(
  {
    git diff --name-only --diff-filter=ACMR origin/main -- '*.cs'
    git ls-files --others --exclude-standard -- '*.cs'
  } | sed '/^$/d' | sort -u
)
if [ "${#files[@]}" -eq 0 ]; then
  echo "no csharp files differ from origin/main"
  exit 0
fi
status=0
for f in "${files[@]}"; do
  case "$f" in
    dotnet/VoiceSwitch.Windows.Core/*) proj=dotnet/VoiceSwitch.Windows.Core/VoiceSwitch.Windows.Core.csproj ;;
    dotnet/VoiceSwitch.Windows.Tests/*) proj=dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj ;;
    dotnet/VoiceSwitch.Windows/*) proj=dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj ;;
    *) echo "no format project for $f"; exit 1 ;;
  esac
  dotnet format "$proj" --verify-no-changes --no-restore --include "$f" || status=1
done
exit "$status"
```

Pass: exit 0. With no tracked or untracked `*.cs` difference from `origin/main`, the script prints `no csharp files differ from origin/main` and does not run `dotnet format`. After a slice, every such file passes `dotnet format <its csproj> --verify-no-changes --no-restore --include <file>`. `git diff` alone misses untracked files, so the `git ls-files --others` list is required.

`NoWakeTests.cs` has two pre-existing WHITESPACE findings, at lines 137 and 210. Leave that file unchanged. This item formats it only if the diff or the untracked list includes it.

`DictationRuntime.cs` has three pre-existing WHITESPACE findings, at lines 2147–2149 (the two comment lines and `internal const string Script` above the recognizer script). On this baseline the file matches `origin/main`, so item 9 does not format it and those findings stay. Slice 1 edits the file, and this item then runs `dotnet format --verify-no-changes` on it. That fails until those three lines are corrected. Running `dotnet format` on the touched file does that whitespace rewrite. Keep it. Do not change the characters inside the `"""` script.

## 10. CLI help

```bash
set -euo pipefail
VS_RUN_ID=spec-help bash .claude/skills/verify-voice-switch-dotnet/verify.sh cli -- --help
out=/tmp/voice-switch-dotnet-verify/evidence/spec-help/stdout.txt
err=/tmp/voice-switch-dotnet-verify/evidence/spec-help/stderr.txt
test ! -s "$err"
grep -F -q 'voice-switch (Windows)' "$out"
grep -F -q '  voice-switch.exe --self-test           pure behavior tests, no mic and no command' "$out"
grep -F -q '  voice-switch.exe --help                this text' "$out"
grep -F -q 'Default command: rundll32 url.dll,FileProtocolHandler superwhisper://record' "$out"
grep -F -q 'Dictation handoff: ' "$out"
grep -F -q 'Incompatible flags: --dry-run cannot be combined with --fire.' "$out"
```

Pass: exit 0. stderr is empty, and stdout contains those lines. `Config:` and the handoff path may differ with `USERPROFILE` and `LOCALAPPDATA`.

## 11. CLI self-test

```bash
set -euo pipefail
VS_RUN_ID=spec-self-test bash .claude/skills/verify-voice-switch-dotnet/verify.sh cli -- --self-test
out=/tmp/voice-switch-dotnet-verify/evidence/spec-self-test/stdout.txt
err=/tmp/voice-switch-dotnet-verify/evidence/spec-self-test/stderr.txt
printf 'self-test: ok\n' | cmp -s - "$out"
test ! -s "$err"
```

Pass: exit 0. stdout is exactly `self-test: ok` plus a newline, and stderr is empty.

## 12. CLI unknown flag

```bash
set -euo pipefail
set +e
VS_RUN_ID=spec-unknown bash .claude/skills/verify-voice-switch-dotnet/verify.sh cli -- --no-such-flag
code=$?
set -e
test "$code" -eq 1
grep -F -q 'fatal: unknown argument: --no-such-flag' /tmp/voice-switch-dotnet-verify/evidence/spec-unknown/stderr.txt
grep -F -q 'hint: copy config.example.windows.json to ' /tmp/voice-switch-dotnet-verify/evidence/spec-unknown/stdout.txt
```

Pass: exit 0. The app exits 1, stderr contains that fatal tail, and stdout contains that hint tail.

## 13. CLI rejects --fire with --dry-run

```bash
set -euo pipefail
set +e
VS_RUN_ID=spec-fire-dry-run bash .claude/skills/verify-voice-switch-dotnet/verify.sh cli -- --fire --dry-run
code=$?
set -e
test "$code" -eq 1
grep -F -q 'fatal: --dry-run cannot be combined with --fire.' /tmp/voice-switch-dotnet-verify/evidence/spec-fire-dry-run/stderr.txt
grep -F -q 'hint: copy config.example.windows.json to ' /tmp/voice-switch-dotnet-verify/evidence/spec-fire-dry-run/stdout.txt
```

Pass: exit 0. The app exits 1, stderr contains that fatal tail, and stdout contains that hint tail. The fatal text is the `CliOptions.Parse` rejection. The script does not record child processes.

## 14. Pre-existing url-actions failure

```bash
set -euo pipefail
set +e
python3 tests/url-actions/run.py >/tmp/vs-refactor-url.out 2>/tmp/vs-refactor-url.err
code=$?
set -e
test "$code" -ne 0
grep -F -q 'AssertionError' /tmp/vs-refactor-url.err
grep -F -q 'guard modeRequested, superwhisperAutoPasteDisabled' /tmp/vs-refactor-url.err
```

Pass: exit 0. `tests/url-actions/run.py` itself exits non-zero, and its stderr contains `AssertionError` and `guard modeRequested, superwhisperAutoPasteDisabled`. That assertion reads Swift source under `Sources/`. It already fails on `origin/main`. Do not change Swift to make it pass.
