---
name: verify-hud-noise
description: Model-check (TLA+) that the macOS mic HUD goes away once nobody talks and the room settles into silence or steady noise (a fan or room tone on a raw mic, echoCancellation false). Use when a change touches the frame loop in Listener.consume (automatic session opening, the dictation branch's end conditions, the phase `defer`), Segmenter.push / isSpeech / rebaseFloor, rebaseFloorAfterNoWake, or HUD.render's phase table in Sources/voice-switch/MacApp.swift and Segmenter.swift, and before claiming the "HUD never disappears" bug fixed.
---

# Verify the HUD hides in a quiet or steadily noisy room (TLA+)

Users reported the mic HUD staying on screen for good after dictating (2026-10-29). The runtime log showed the cause: after a Listener restart the Segmenter's noise floor starts below the room noise, every noise frame reads as speech, an automatic session opens on it, runs to maxSeconds, is dropped as "no speech", and the next one opens in the same second. `HudNoise.tla` models one pass of the frame loop per step, with the room choosing quiet, noise or voice each frame, and checks the property below. Real-app proof of a fix (the HUD log lines with a noise fixture) belongs in `../verify-voice-switch-mac` (`--simulate` with a noise-mixed fixture, look for `hud: hidden`); this skill covers only the loop logic.

## Property

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `HudHidesWhenNobodyTalks` (liveness) | Once nobody talks and the room settles into silence or steady noise, the mic HUD eventually goes away and stays away. | The mic glyph never leaves the screen, a 60 s automatic session records the room over and over, and stop words and wake words stop working while it does. |

`TypeOK` is checked as an invariant.

## Status

**The property passes on the fix** (60 distinct states): when an automatic session ends by `maxSeconds` without `d.confirmed`, the dictation branch calls `rebaseFloorAfterNoWake(&seg)`. A whole `maxSeconds` with no word Apple Speech and Whisper agreed on is the room, the same judgement the idle path makes on an over-cap head with no wake, so the floor rises above the noise and the next frame no longer reads it as speech. Runtime proof with `../verify-voice-switch-mac` (25 s steady pink noise, `dictation.maxSeconds` 8): with the fix, `dictation vad: floor rebased floor=0.0010->0.0080 threshold=0.0240`, then `hud: hidden` and no new session for the remaining 16 s; without it, three sessions in a row each `ended by maxSeconds` and reopened in the same second.

**Before the fix the property failed. That was the reported bug, not a modeling error.** The shortest counterexample, in code words:

1. The listener (re)starts: a new `Segmenter(cfg:)` has `floor = 0.001`, so the threshold is `vadMinRMS` (0.005), below the room noise.
2. Noise frames read as speech (`isSpeech`), so the idle path opens an automatic session (`dictation session: automatic`, `-> session opened`, `halMs=0`).
3. The open utterance passes `maxSeconds + hangover`, so `Segmenter.push` emits `.head` and starts skipping. The dictation branch never looks at `.head`, so `rebaseFloorAfterNoWake` is not called, and `seg.adaptFloor` is false while dictating.
4. No non-speech frame ever comes, so the session ends only by `maxSeconds` (`dictation ended by maxSeconds`, then `dropped: automatic audio had no speech`).
5. On the next frame the idle path sees noise as speech again and reopens a session. The utterance is still open and already skipping, so no new `.head` reaches the idle path. Back to step 3, with the HUD showing the mic glyph throughout.

A second counterexample matters for fixes: `.head` can also fall on the very frame where the idle path opens a session (it `continue`s before the head handling). So a fix that only looks at `.head` inside the dictation branch still fails (`HudNoise-fixed` variant 1 below).

## Run

```sh
sh .claude/skills/verify-hud-noise/check.sh
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked by the script, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` on first run, shared with `verify-meet-pause`). Needs Java 11+. About 5 s. A pass ends with `Model checking completed. No error has been found.` (60 distinct states with the fix). `sh check.sh <other.tla>` checks a copy, which is how variants are tried.

Read every result, pass or fail, with the `/statechart` skill: a violation as its shortest path in plain words, a pass as a chart of the states.

## Coverage record

Sync commit: `de4c434` (the model was last confirmed to describe the code at this commit). Move it only after the model describes the code at the new commit.

| Model part | Code |
|---|---|
| `Frame` (one step) | one iteration of `for await frame in stream` in `Listener.consume` (MacApp.swift) |
| `speech` | `Segmenter.isSpeech`: `rms > threshold` or the HAL voice flag (`voiceBeingHeard()`), modeled as voice only |
| `floorHigh`, `fh1` | `Segmenter.floor` / `threshold`; the floor follows non-speech frames only while `seg.adaptFloor = dictation == nil` |
| `uttLen`, `skipping`, `silent`, `close`, `head` | `Segmenter.push`: utterance buffering, `.head` past `maxFrames + hangover`, `.utterance` / `.tail` after `hangover` silent frames |
| dictation branch | `if var d = dictation { ... }`: `silentFrames`, end on `noWakeWord.endSilenceMs` or `maxSeconds`, `dictation = nil; speechFrames = 0` |
| `confirmed`, `c` | `Dictation.confirmed`, set by `wordCheck` (`agreedWordCount` >= `NoWakePolicy.minAgreedCharacters`); modeled as confirming on the first voice frame of the session |
| maxSeconds end → `floorHigh` | `if reason == "maxSeconds", d.origin.isAutomatic, !d.confirmed { rebaseFloorAfterNoWake(&seg) }` just before `dictation = nil` |
| idle path opening | `speechFrames += 1` while speaking, `longEnough`, `NoWakePolicy.onSpeech`, `automaticWakeAllowed()`, `dictation = Dictation(...)` then `continue` |
| idle path `head` → `floorHigh` | `rebaseFloorAfterNoWake(&seg)` on an over-cap head (the three call sites after the opening block) |
| `HudVisible` | the phase `defer` (`dictation` → `.waiting`/`.hearing`/`.recording`, else `.hearing` when `speechFrames > 0` and `echoCancellation == false`) and `HUD.render`, which draws those as the mic glyph |

Bounds (`HudNoise.cfg`): `MinSpeech = 1`, `Cap = 2`, `Hang = 1`, `EndSilence = 2`, `MaxAge = 4` frames. They keep the code's order (`minSpeechMs` 300 ms < `maxSeconds + hangover` 2.8 s < `maxSeconds` 60 s; `hangoverMs` < `endSilenceMs`), not its sizes.

Assumptions: `dictation.echoCancellation` is false (the user's config) and `noWakeWord` is on; `automaticWakeAllowed()` and `NoWakePolicy.onSpeech` always allow (voice activity gate off, as in the user's log); the HAL detector fires on voice only; the gap reset of `speechFrames` after `hangoverMs` is one frame; no hotkeys, wake words or stop words (nobody talks).

## Does my change touch this?

Run `git diff de4c434 -- Sources/voice-switch/MacApp.swift Sources/voice-switch/Segmenter.swift` from the repo root and look for: the frame loop in `Listener.consume` (opening condition, the dictation branch's end conditions, where `.head` is handled, `seg.adaptFloor`, `speechFrames` resets), `Segmenter.push` / `isSpeech` / `rebaseFloor`, `rebaseFloorAfterNoWake`, the phase `defer`, or `HUD.render`'s `switch (phase, delivery)`. Any of these needs the matching model part updated before trusting the result.

## When it fails

1. Read the trace with `/statechart` and retell it as steps in code words, as in "Status" above.
2. Check each step against the code it names. If every step is something the code can do, it is a real bug: report it with that story and the harm. Do not weaken the property, narrow the room further, or remove a path.
3. If a step is something the code cannot do, the model is wrong: fix the model to match the code, and say what you changed.

## Updating the model after a fix

Mirror the code change in `Frame`, run the check, and expect a pass. Then break the new code in the model the way the property guards against (remove the new condition) and confirm it fails again. Move the sync commit only then.

Variants already tried (as scratch copies, not committed):

- **The fix inverted** (rebase on a `maxSeconds` end only when confirmed, `a >= MaxAge /\ c /\ skip1`): fails with the reopen loop. Use it as the break after editing the fix.
- **The fix removed** (the code at `ce14722`, before `de4c434`): fails with the loop in "Status".

- **Variant 1, rebase on `.head` inside the dictation branch** (`floorHigh' = (head \/ fh1)` in the dictation branch): still fails. The head can fall on the frame that opens the session, or in an earlier session, so the dictation branch never sees it.
- **Variant 2, demo only: no automatic reopening inside an unbroken utterance, and lift the floor while skipping** (idle path opens only when `~skipping`, and `floorHigh' = (head \/ skipping \/ fh1)`): passes, 57 distinct states. Removing `~skipping` from it fails again. This shows the property can be met; it is not a recommended fix (it would also block a session the user starts mid-utterance).

The user prefers a code-level fix over threshold or floor tuning (2026-10-29): variant 1 is a floor tweak and is the kind of fix that breaks when the situation shifts.

## Not covered

- Bursts of noise between silences (keyboard, door): each burst can open a session and flash the HUD. The property assumes the room settles; the first run without that assumption failed on an alternating quiet/noise room.
- `delivery` glyphs (`processing`, `done`, `saved`, `failed`) and their expiry timers; `.ended`'s 1.5 s; `notify` paused/resumed. These expire on their own and are left out.
- Superwhisper's own recording (`delivery = .recording`, `followDirectSuperwhisper`): a HUD left on after direct Superwhisper use is not modeled.
- The voice activity gate (`minVoiceSeconds`, `lastAutomaticBlock`), the speaker gate, `skipWhileMicInUseBy`, the follow-up window, early wake, wake-word dictations, stop words (see `ce14722`) and the window stop check.
- Timing: frames are only an order here; 2.5 s, 60 s and the floor's 5 % step are not modeled sizes.
