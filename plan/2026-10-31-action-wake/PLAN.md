# Action wake words on the early-wake probe

`earlyWakeMs` is opt-in for the periodic speech probes. With it null, a config with at least one action still gets the 60 ms and 150 ms silence probes; without actions, null keeps today's timing. Do not set it in the example configs.

## Problem

An action wake word waits out `hangoverMs` (default 300) and a closed-utterance transcription before the action dictation starts. A plain wake word can fire from the early-wake probe.

Confirmed in the tree at `c472ac9`:

- `Segmenter.earlyWake` matches only `cfg.wakeWords`. The Mac listener that calls it never sets `Dictation.action`, never plays `actionWakeChime`, and never sets `deafFrames`. The closed path in the same listener does all three, and it checks `handoffBusy` and `micInUse`.
- `DictationSession.Apply` calls `EarlyWake` on the default set only. The `windows.actions` loop runs only when the extent is not `Probe`. `DictationActionWakeSelectsAction` asserts that a probe of `プログレス` opens nothing.
- `CheckMode` and Mac `check` pass only the default wakes into the early verdict.
- `tests/url-actions/run.py` asserts that `.actions` is absent from `earlyWake`.

The closed path already prefers the default wake words, then each action in config order. Keep that priority.

Rejected hypotheses stay on the default wakes. Do not redo the merged wake-matcher slices in `plan/2026-10-29-wake/`.

## Rule

One pure function per platform judges every wake: Swift `wakeVerdict(_:sets:tailSilentFrames:closed:)` and C# `DictationBoundaries.Verdict(recognition, sets)`. The early probe, the closed utterance, and both check modes call it and only act on its verdict. The sets are ordered: the default set first (owner nil), then each action in config order (`wakeSets(cfg)` / `DictationBoundaries.WakeSets(config)`).

1. The pause guard (a word that is a proper prefix of a longer wake word needs a 150 ms pause or `、` before a body) always sees the union of every set's words. `音声入力メモ今日` with default `音声入力` and action `音声入力メモ` opens `音声入力メモ` with body `今日`; with a pause after `音声入力` the default wins with body `メモ今日`.
2. If the whole text is a proper prefix of any wake word, fire only as an exact bare word after the 150 ms prefix gap (a closed utterance counts as satisfied). The winner is the first set that contains that word. `音声入` never fires.
3. If the whole text equals a wake word, it is a bare wake for the first set that contains it; a probe needs some tail silence. `音声メモ` with default `音声` and action `音声メモ` is the action.
4. Otherwise a leading wake with a body: the default set first, then each action, first hit wins.
5. A probe never fires on a fuzzy reading (`Distance > 0`). Anything that is not a probe keeps today's fallbacks after the exact layer: a fuzzy reading of the default set, `RejectedWake` on the default set, then a fuzzy reading of each action.
6. An action verdict opens the action dictation the way the closed path does: `action` on `Dictation`, `actionWakeChime` and 600 ms `deafFrames` on a bare wait with sound on, `handoffBusy` and `micInUse` in force. Windows keeps the session's wake set; while awaiting, a probe body counts only when the verdict names that same set.
7. The early line and the check-mode verdict carry `action=<name>`.

`dictationStart` takes the union as a required `longerWords`; C# `LeadingWake` has a public one-set overload and an internal one that takes the union.

## Measurable acceptance

`.claude/skills/verify-action-wake-latency/items.md` is the list. `verify.sh drive` from that folder runs it on this Linux box. The Windows items are tests in `dotnet/VoiceSwitch.Windows.Tests`. The Mac listener is a source contract because `swiftc` exits 127 (`libncurses.so.6`). Do not install the library.

Update `tests/url-actions/run.py` so the early-wake assertion matches the new rule, and keep its mutation check honest. Update the probe assertion inside `DictationActionWakeSelectsAction`. Leave the other three cases in that test alone.

Replace the README sentence that says early probes use only the default wake words. English must contain `early wake probes use the default wake words and each action`. Japanese must contain `早期ウェイクの判定は既定のウェイクワードと各アクション`. Delete the old sentences named in item `awl-readme`.

## Regression guards

`verify.sh` item `awl-suite` requires the Windows test process to exit 0 with no `FAIL` line and at least 243 `PASS` lines. The named `awl-regress-*` items must stay green, including the prefix-gap test and the closed action-priority test. `awl-parity` fails if `python3 tests/parity/run_parity.py` prints a new `FAIL` line or drops below `pass=126`. The two baseline failures may stay. They come from `swiftc` and `libncurses.so.6`.

## Effort

This slice does not need `#xhigh`. The rule above is the spec. Run the implementer at `#high`. Move to `#xhigh` only after a `#high` slice has failed the same gate.
