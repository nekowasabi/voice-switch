# Action wake words on the early-wake probe

`earlyWakeMs` is opt-in. Null keeps today's timing. Do not set it in the example configs.

## Problem

An action wake word waits out `hangoverMs` (default 300) and a closed-utterance transcription before the action dictation starts. A plain wake word can fire from the early-wake probe.

Confirmed in the tree at `c472ac9`:

- `Segmenter.earlyWake` matches only `cfg.wakeWords`. The Mac listener that calls it never sets `Dictation.action`, never plays `actionWakeChime`, and never sets `deafFrames`. The closed path in the same listener does all three, and it checks `handoffBusy` and `micInUse`.
- `DictationSession.Apply` calls `EarlyWake` on the default set only. The `windows.actions` loop runs only when the extent is not `Probe`. `DictationActionWakeSelectsAction` asserts that a probe of `プログレス` opens nothing.
- `CheckMode` and Mac `check` pass only the default wakes into the early verdict.
- `tests/url-actions/run.py` asserts that `.actions` is absent from `earlyWake`.

The closed path already prefers the default wake words, then each action in config order. Keep that priority. `音声入力メモ今日` with default `音声入力` and action `音声入力メモ` stays a plain wake whose body starts at `メモ`. Do not put every wake word in one longest-match list.

Rejected hypotheses stay on the default wakes. Do not redo the merged wake-matcher slices in `plan/2026-10-29-wake/`.

## Rule

One rule on both platforms. Wake sets stay ordered. The default set is first. Each action follows in config order. The first set that matches wins.

1. If the whole probe text is a proper prefix of any wake word in any set, fire only when the text equals a wake word and the tail silence is at least 150 ms (`prefixGapFrames` / `PrefixGapSamples` 2400). The winner is the first set that contains that exact word. A non-equal prefix such as `音声入` never fires. A bare word still needs some tail silence when this gate does not apply. Distance greater than 0 never fires from a probe.
2. Otherwise try the default set with today's early-wake match, using only the default words for its pause check. A hit is a plain wake.
3. If the default set misses, try each action with the same match. The pause-before-body check for that match sees every wake word, so an action word `音声` does not open inside `音声認識` when `音声入力` is configured anywhere.
4. A hit on an action opens the action dictation the way the closed path does. Mac sets `action` on `Dictation`. A bare wait with the sound preference on plays `actionWakeChime` and sets `deafFrames` to 600 ms. A one-breath body does not play the chime. `handoffBusy` and `micInUse` stay in force. Windows sets `Action` and the session's wake set. `PublishPhase` already calls `PlayWakeSound(true)` and `WaitingFor(action)` for `DictationPhase.Waiting`. While a session is awaiting, a later probe uses that session's wake set, not only the default set, so the body can start before the hangover.
5. Log the action name on the existing early line. Mac `early:` gains `action=<name>` when the hit is an action. Windows `dictation session: early-wake` gains `action=<name>`. Mac `check` and Windows `CheckMode` put the same `action=<name>` on the early verdict token.

`Segmenter.earlyWake` itself reads `cfg.macOS?.actions`. `dictationStart` may grow an optional longer-word list. The default argument keeps every current caller. `EarlyWake` may grow an optional guard list. With the guard omitted, `EarlyWakeFiresOnlySafeProbes` stays true.

## Measurable acceptance

`.claude/skills/verify-action-wake-latency/items.md` is the list. `verify.sh drive` from that folder runs it on this Linux box. The Windows items are tests in `dotnet/VoiceSwitch.Windows.Tests`. The Mac listener is a source contract because `swiftc` exits 127 (`libncurses.so.6`). Do not install the library.

Update `tests/url-actions/run.py` so the early-wake assertion matches the new rule, and keep its mutation check honest. Update the probe assertion inside `DictationActionWakeSelectsAction`. Leave the other three cases in that test alone.

Replace the README sentence that says early probes use only the default wake words. English must contain `early wake probes use the default wake words and each action`. Japanese must contain `早期ウェイクの判定は既定のウェイクワードと各アクション`. Delete the old sentences named in item `awl-readme`.

## Regression guards

`verify.sh` item `awl-suite` requires the Windows test process to exit 0 with no `FAIL` line and at least 243 `PASS` lines. The named `awl-regress-*` items must stay green, including the prefix-gap test and the closed action-priority test. `awl-parity` fails if `python3 tests/parity/run_parity.py` prints a new `FAIL` line or drops below `pass=126`. The two baseline failures may stay. They come from `swiftc` and `libncurses.so.6`.

## Effort

This slice does not need `#xhigh`. The rule above is the spec. Run the implementer at `#high`. Move to `#xhigh` only after a `#high` slice has failed the same gate.
