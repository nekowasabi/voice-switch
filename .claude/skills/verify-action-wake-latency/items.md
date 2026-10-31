# Action wake latency items

`verify.sh` reads every `## <id>` and its `kind:` line. The paragraph under an item is the pass condition. A test that prints `PASS` without the observations below does not satisfy the item.

Baseline before this change, from `/workspace/vs-action-wake` at `c472ac9`: `dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore` exits 0 with 243 `PASS` lines and 0 `FAIL` lines. `python3 tests/parity/run_parity.py` exits 1 with `SUMMARY pass=126 allow=23 fail=2`. The two failures are `Swift pure harness failed to compile` (`libncurses.so.6`) and `automatic transcript policy checks failed` (the same missing library). `python3 tests/url-actions/run.py --require-swift` exits 1 because `swiftc` exits 127.

## awl-probe-wait

kind: dotnet-pass
needle: PASS probe action wake opens the action wait

A `RecognitionExtent.Probe` whose text is an action wake word (`プログレス`), with `TailSilenceSamples` 2400, opens an awaiting session whose `Action.Name` is `progress`. The default wake word is `音声入力`, which this text does not match. The same probe with `TailSilenceSamples` 0 opens nothing. `earlyWakeMs` stays opt-in. This item does not turn it on.

## awl-prefix-guard

kind: dotnet-pass
needle: PASS probe action wake keeps the prefix gap across every wake word

Two configs.

Default wake `音声`, action wake `音声入力`. Probe text `音声` with tail 960 opens nothing. The same text with tail 2400 opens an awaiting plain session (`Action` null). Probe text `音声入` with tail 2400 opens nothing. Probe text `音声入力` with tail 960 opens the action session.

Default wake `音声入力`, action wake `音声`. Probe text `音声` with tail 960 opens nothing. With tail 2400 it opens the action session. Probe text `音声入力` with tail 960 opens a plain session (`Action` null).

960 samples is 60 ms. 2400 samples is 150 ms (`PrefixGapSamples`). A fuzzy reading still never opens a session from a probe.

## awl-fuzzy

kind: dotnet-pass
needle: PASS probe action wake ignores a fuzzy reading

Default wake `音声入力`. Action wake `音声に入る`. Probe text `温泉に入る` with readings `おんせん` / `に` / `はい` / `る` and tail 2400 opens nothing.

## awl-probe-body

kind: dotnet-pass
needle: PASS probe action wake with a body opens the action recording

Default wake `音声入力`, action `progress` / `プログレス`. One probe of `プログレス今日は` whose body lexeme starts at least 150 ms after the wake word opens an active session, `Action.Name` is `progress`, and the body starts at that lexeme. A bare probe of `プログレス` with tail 2400 opens an awaiting session for that action, and a later probe that continues into a body starts the body and keeps the same action.

## awl-default-wins

kind: dotnet-pass
needle: PASS probe prefers the default wake over an action word

Default wake `音声入力`, action wake `音声入力メモ`. A probe of `音声入力メモ今日` with lexemes `音声入力` 0..4000, `メモ` 4000..6000, `今日` 8000..20000 opens a plain session (`Action` null) whose body starts at 4000. This is the same priority as `dictation action wake selects its action after the default wakes`. Do not merge every wake word into one longest-match list.

## awl-non-wake

kind: dotnet-pass
needle: PASS probe of a non-wake opens no session

A probe of `こんにちは` with tail 2400 opens nothing when the wakes are `音声入力` and an action `プログレス`.

## awl-runtime-hud

kind: dotnet-pass
needle: PASS probe action wake logs the action and shows waiting

With `EarlyWakeMs` set, a scripted probe that recognizes `プログレス` logs a line containing `dictation session: early-wake` and `action=progress`, moves the observer to `DictationPhase.Waiting`, calls `PlayWakeSound(true)`, and calls `WaitingFor` with the action named `progress`. The closed utterance of the same audio may follow. The early line belongs to the probe.

## awl-skip-mic

kind: dotnet-pass
needle: PASS probe action wake is skipped while the microphone is in use

When `skipWhileMicInUseBy` names the process `micInUseBy` reports, a probe of the action word logs `is using the microphone`, does not log `dictation session: early-wake`, and does not enter `DictationPhase.Waiting`.

## awl-check-mode

kind: dotnet-pass
needle: PASS check mode names an early action wake

`CheckMode` with `EarlyWakeMs` 10000 and an action `progress` / `プログレス` prints an early verdict whose token contains `early-wake@510ms+stt` and `action=progress`. The recognizer returns `プログレス`. The default wake word does not.

## awl-regress-early

kind: dotnet-pass
needle: PASS early wake fires a prefix wake only after the prefix gap and never from a fuzzy reading

## awl-regress-leading

kind: dotnet-pass
needle: PASS leading wake needs a pause after a wake that starts a longer wake

## awl-regress-session

kind: dotnet-pass
needle: PASS dictation session keeps probe wake audio out of the body

## awl-regress-runtime

kind: dotnet-pass
needle: PASS dictation runtime opens the wait from a probe before the closed utterance

## awl-regress-check

kind: dotnet-pass
needle: PASS check mode prints one early verdict per utterance when earlyWakeMs is set

## awl-regress-action

kind: dotnet-pass
needle: PASS dictation action wake selects its action after the default wakes

The three non-probe cases in this test stay as they are. The probe of `プログレス` with tail silence 3000 now opens the action wait. Update that assertion so the suite agrees with `awl-probe-wait`.

## awl-suite

kind: builtin

The full `VoiceSwitch.Windows.Tests` process exits 0, prints no line `FAIL `, and prints at least 243 lines `PASS `.

## awl-url-static

kind: builtin

`python3 tests/url-actions/run.py` (no `--require-swift`) prints `URL action static call-chain checks passed` and `URL static mutation checks passed`. Exit 0 passes. Exit 1 passes only when stderr contains `libncurses.so.6`, which is the broken `swiftc` on this box. Any other non-zero exit fails. After `//` comments are stripped, the body of `Segmenter.earlyWake` contains `.actions` and `prefixGapFrames`. `tests/url-actions/run.py` does not contain `not in early`.

## awl-mac-early

kind: builtin

In `MacApp.swift`, the listener block that starts at `cfg: config.cfg, tailSilentFrames` and ends at the next `guard let event else` contains `actionWakeChime`, `handoffBusy`, `deafFrames`, `action:`, and `action=`. The body of `func check(` contains `action=`.

## awl-readme

kind: builtin

`README.md` does not contain `early wake probes use only the default wake words`. It does contain `early wake probes use the default wake words and each action`. `README_ja.md` does not contain `早期ウェイクの判定は既定のウェイクワードだけを使う。` It does contain `早期ウェイクの判定は既定のウェイクワードと各アクション`.

## awl-swift-exec

kind: builtin

Run `swiftc --version`. Exit 0 means this item still has nothing to compile (there is no Linux Swift behavior driver for the Mac listener). Print `SKIP`. A non-zero exit, including 127 from `libncurses.so.6`, is also `SKIP`. Do not install libraries to flip this item.

## awl-parity

kind: builtin

`python3 tests/parity/run_parity.py` prints `SUMMARY` with `pass` at least 126 and `fail` at most 2. Every line that starts with `FAIL ` is one of these two, and either line may be absent:

- `FAIL Swift pure harness failed to compile:`
- `FAIL automatic transcript policy checks failed:`
