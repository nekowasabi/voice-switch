# macOS URL action review evidence

Reviewed on 2026-10-09 in the Linux cloud checkout. This is a second review by the
implementing agent, approached from config, state-machine, process-boundary, and
repeat-delivery perspectives; it is not an independent person's approval.
No code or artifact was pushed or published during review.

## Findings and changes

The new example originally used `superwhisper://record` for both wake and stop,
following the repository's legacy defaults. This is a toggle and can reverse the
intended state if delivered twice before microphone-use observations catch up.
The [official deep-link specification](https://superwhisper.com/docs/modes/switching-modes)
explicitly supports `/record/start` and `/record/stop`, whose actions are conditional
on the current recording state. The new example and instructions now use those
endpoints. The existing general defaults are preserved for backward compatibility.
The old comment claiming a busy-mic check made starting impossible was corrected:
that check is not atomic with delivery. Installed-version behavior remains a Mac
verification item; no vendor binary or installer was downloaded or run.

No additional implementation defect was established by the source review. This
is not a compile or runtime correctness guarantee. Swift and .NET remain absent.

## Configuration and control-flow matrix

| Case | Selected behavior from source | Evidence |
|---|---|---|
| Wake URL present, command mode | `wakeURL`; never the fallback command after a URL failure | `Platform.runMacAction`, wake branch in `Listener.consume` |
| Wake URL absent/null | Required `command` through the existing shell runner | `Config.command: String`, `runMacAction` fallback |
| Stop URL present, external configured app owns mic | `stopURL`, independently of `wakeURL` | Stop branch in `consume`, `runMacAction(stop: true)` |
| Stop URL absent/null | `stopCommand`, then legacy Superwhisper toggle default if absent | `runMacAction` fallback |
| Valid URL but missing/null `command` | Config decode fails before any action | Synthesized `Config.Decodable`; added Swift regression cases, not executed |
| Empty/invalid URL, including an otherwise unused URL | Whole config decode fails; startup alerts or reload retains previous config | `ActionURL.init`, `ConfigFile`, `AppDelegate.fail` |
| Dictation configured, no active session, wake word | Existing dictation session starts; wake URL does not execute | Guard immediately before `runMacAction(config.cfg)` |
| Active voice-switch dictation, stop word | Finish existing dictation; no stop URL | Active-dictation branch continues before command branches |
| No active voice-switch dictation and external app not using mic, stop word | No action | Existing mic-use guard and `continue` |
| Same text listed as wake and stop | Stop handling takes priority, even if no app is recording | Stop branch precedes wake matching |
| `--fire`, with or without dictation | Sends wake action directly; waits for URL result | macOS `main.swift` branch |
| URL spawn failure / nonzero `open` exit | Log error, no shell fallback or automatic retry; listener remains in command listening state | `runURLProcess`, command branches that `continue` without assigning dictation |
| Repeated accepted events | Fresh process per event, no reuse, deduplication or cooldown | `urlProcess` creates `Process()` per call |

`--fire` returns failure for URL dispatch failures. Its old shell-command branch
still starts asynchronously and retains its previous exit-status semantics.
Async listener dispatch reports child failure in the log; it does not claim the
external app started recording or update a voice-switch recording flag. Custom
URLs may be non-idempotent, and multiple app/CLI instances have no shared action
lock. The mic-use guard limits triggers but cannot provide exactly-once delivery.

## URL argument boundary

`ActionURL` rejects missing/invalid schemes, raw whitespace/control characters,
bad percent escapes, and file/data/javascript schemes at decode time. Successful
values remain the original string. `/usr/bin/open` is an absolute executable;
arguments are `-g`, `--`, and that single string. Query `&`, semicolons, quotes,
`$()` and encoded whitespace do not enter a shell parser. Percent-encoded control
bytes remain encoded in the argument; what an application does after decoding a
URL is outside voice-switch's validation. No received dictation text is inserted
into these configured URLs.

The static checks inspect those boundaries as text. Only running the Swift tests
can establish what Foundation decodes and what the constructed `Process` holds;
only a Mac handler test can establish what LaunchServices and the target receive.

## What the 96 passes actually mean

`python3 tests/parity/run_parity.py --static-only` reports these 96 successful
source checks. They are mostly existing cross-platform contracts, not 96 new URL
feature tests and not 96 executions of production behavior.

| Group | Passes | What is checked |
|---|---:|---|
| Capability markers | 34 | 17 capabilities have required non-comment strings in both Swift and C# source: matching, segmenter, command runner, reload, dictation, WAV/handoff, shortcuts, focus, menu/device, mic guard and deadline |
| Call-chain contracts | 25 | Expected strings occur in selected CLI/runtime/helper bodies; includes wake/stop URL dispatch, fallback command expression, session gating and Superwhisper route/handoff references |
| Test-source marker | 1 | Windows test source contains required test names; those tests are not run |
| Config declarations | 2 | Shared top-level and dictation field names match; `macOS` is an explicit Mac-only exception |
| Example config keys | 17 | Shared keys in the existing default Mac/Windows examples; this is not value validation or a parse of the new URL example by Swift |
| Shared CLI flags | 3 | Expected shared flags occur on both platforms |
| Checker mutation gates | 14 | 13 deliberate source alterations are detected, and the unmodified baseline still passes; checks the checker rather than executing app behavior |
| **Total** | **96** | Text-based source contracts |

The 20 ALLOW rows document intentional differences (2 capability gaps, 3 sample
key gaps and 15 CLI gaps). They are not additional passes or evidence of working
runtime behavior. Source comments are stripped; brace extraction is heuristic,
not a compiler, and cannot generally prove reachability or concurrency behavior.

## Additional URL-specific checks

`python3 tests/url-actions/run.py` passes URL wiring/boundary assertions and nine
in-memory mutation checks. Removing wake dispatch, stop dispatch, CLI error exit,
URL selection, the early return preventing fallback, legacy fallback, child
exit-status propagation, the absolute executable, or the single-argument boundary
must fail the checker. These also operate on source, not a running app.

`tests/url-actions/main.swift` contains executable checks for valid/invalid URL
config, missing/null required command, null optional URL fields, exact argument
preservation, fresh processes on repeat construction, spawn failure, child
success/failure, invalid reload retention and subsequent valid reload. They were
**not compiled or run** in the cloud because `swiftc` is unavailable. Native
selection of wake/stop paths and actual recognizer events still need the Mac
manual checks, even if these unit checks pass later.

`make win-verify`, JSON syntax checks, Python syntax parsing, and
`git diff --check` pass. `make build` and `make url-test` cannot complete without
Swift. Full parity cannot complete without .NET. There is no Mac build, live
microphone, LaunchServices, Superwhisper record/transcribe/paste, or Windows
runtime validation claimed. See [the Mac checklist](macos-url-actions.md).
