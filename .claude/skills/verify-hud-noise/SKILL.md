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

**A 案の表示規則に同期し、今回の検査で性質に合格**（60 状態、終了コード 0、2026-10-31）。`HudVisible` は `dict = "auto" /\ confirmed`。待機中のフレーズと本文後の未確認自動セッションは表示しない。`maxSeconds` 終了時の `rebaseFloorAfterNoWake(&seg)` は維持している。ただし表示を確認後に限ったので、HUD の性質はこの再調整を外す退行を検出しない（今回の変異検査は 38 状態、終了コード 0、違反文言なし）。録音が繰り返し開かないことの証明にはならない。

過去の実機確認（今回の再実行ではない）：`../verify-voice-switch-mac` の定常雑音音源では、再調整ありで `dictation vad: floor rebased floor=0.0010->0.0080 threshold=0.0240` の後に `hud: hidden` が続き、新しいセッションが開かなかった。再調整なしでは `ended by maxSeconds` の直後に再開するセッションが続いた。

**以下は A 案より前の表示規則で得た反例。現在の表示式での失敗証拠ではない。**

1. The listener (re)starts: a new `Segmenter(cfg:)` has `floor = 0.001`, so the threshold is `vadMinRMS` (0.005), below the room noise.
2. Noise frames read as speech (`isSpeech`), so the idle path opens an automatic session (`dictation session: automatic`, `-> session opened`, `halMs=0`).
3. The open utterance passes `maxSeconds + hangover`, so `Segmenter.push` emits `.head` and starts skipping. The dictation branch never looks at `.head`, so `rebaseFloorAfterNoWake` is not called, and `seg.adaptFloor` is false while dictating.
4. No non-speech frame ever comes, so the session ends only by `maxSeconds` (`dictation ended by maxSeconds`, then `dropped: automatic audio had no speech`).
5. On the next frame the idle path sees noise as speech again and reopens a session. The utterance is still open and already skipping, so no new `.head` reaches the idle path. Back to step 3, with the HUD showing the mic glyph throughout.

過去の別の反例では、`.head` がセッション開始と同じフレームに来ると、開始処理の `continue` により処理されなかった。このため録音分岐内の `.head` だけを見る修正も、旧表示式では失敗した（後述の変異 1）。

## Run

```sh
sh .claude/skills/verify-hud-noise/check.sh
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked by the script, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` on first run, shared with `verify-meet-pause`). Needs Java 11+. 合格出力は `Model checking completed. No error has been found.`。今回の実測は 60 状態、終了コード 0。`sh check.sh <other.tla>` で写しを検査できる。

Read every result, pass or fail, with the `/statechart` skill: a violation as its shortest path in plain words, a pass as a chart of the states.

## Coverage record

Sync commit: `cf89a2f` の作業ツリー（未コミット）。`de4c434` の雑音基準値再調整を維持し、A 案の表示判定へ同期した。コミットは作成していない。

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
| `HudVisible` | `dict = "auto" /\ confirmed`。phase の `defer` で本文後の未確認自動セッションと待機中のフレーズは `.idle`、確認後は `.recording` / `.stopping`。ウェイクの本文待ちはこのモデルの対象外 |

Bounds (`HudNoise.cfg`): `MinSpeech = 1`, `Cap = 2`, `Hang = 1`, `EndSilence = 2`, `MaxAge = 4` frames. They keep the code's order (`minSpeechMs` 300 ms < `maxSeconds + hangover` 2.8 s < `maxSeconds` 60 s; `hangoverMs` < `endSilenceMs`), not its sizes.

Assumptions: `dictation.echoCancellation` is false (the user's config) and `noWakeWord` is on; `automaticWakeAllowed()` and `NoWakePolicy.onSpeech` always allow (voice activity gate off, as in the user's log); the HAL detector fires on voice only; the gap reset of `speechFrames` after `hangoverMs` is one frame; no hotkeys, wake words or stop words (nobody talks).

## Does my change touch this?

Run `git diff cf89a2f -- Sources/voice-switch/MacApp.swift Sources/voice-switch/Segmenter.swift` from the repo root and look for: the frame loop in `Listener.consume` (opening condition, the dictation branch's end conditions, where `.head` is handled, `seg.adaptFloor`, `speechFrames` resets), `Segmenter.push` / `isSpeech` / `rebaseFloor`, `rebaseFloorAfterNoWake`, the phase `defer`, or `HUD.render`'s `switch (phase, delivery)`. Any of these needs the matching model part updated before trusting the result.

## When it fails

1. Read the trace with `/statechart` and retell it as steps in code words, as in "Status" above.
2. Check each step against the code it names. If every step is something the code can do, it is a real bug: report it with that story and the harm. Do not weaken the property, narrow the room further, or remove a path.
3. If a step is something the code cannot do, the model is wrong: fix the model to match the code, and say what you changed.

## Updating the model after a fix

コードの変更を `Frame` / `HudVisible` に同期して検査する。写しで変更を外し、終了コードと違反文言の有無を記録する。失敗しない場合は何を検出できなくなったかを明記し、過去の反例を現在の証拠として使わない。表示式を旧式へ戻す検査は `../verify-hud-blip/break.sh` を使う。

今回の変異検査（2026-10-31、写しで 1 回実行）:

- `maxSeconds` 終了時の `floorHigh' = (fh1 \/ (a >= MaxAge /\ ~c /\ skip1))` を `floorHigh' = fh1` に戻して `sh .claude/skills/verify-hud-noise/check.sh <写し>` を実行。終了コード 0、38 状態、`Model checking completed. No error has been found.`。違反文言なし。表示を確認後に限ったので、HUD の性質はこの退行を検出しない。性質と上限は変更していない。

以下は旧表示式での過去の試行（今回の再実行・現在の検出力の証拠ではない）:

- **再調整の条件を反転**（`a >= MaxAge /\ c /\ skip1`）：旧表示式では再開ループで失敗した。現在の表示式の退行検査には使えない。
- **再調整を削除**（`de4c434` より前の `ce14722`）：旧表示式では上記の再開ループで失敗した。現在の表示式では今回の検査のとおり合格する。

- **Variant 1, rebase on `.head` inside the dictation branch** (`floorHigh' = (head \/ fh1)` in the dictation branch): still fails. The head can fall on the frame that opens the session, or in an earlier session, so the dictation branch never sees it.
- **Variant 2, demo only: no automatic reopening inside an unbroken utterance, and lift the floor while skipping** (idle path opens only when `~skipping`, and `floorHigh' = (head \/ skipping \/ fh1)`): passes, 57 distinct states. Removing `~skipping` from it fails again. This shows the property can be met; it is not a recommended fix (it would also block a session the user starts mid-utterance).

The user prefers a code-level fix over threshold or floor tuning (2026-10-29): variant 1 is a floor tweak and is the kind of fix that breaks when the situation shifts.

## Not covered

- 無音を挟む物音（キーボード、ドア）：この性質は部屋の音が安定することを仮定する。A 案では未確認のセッションが開いても表示しない。物音単独の点滅は `../verify-hud-blip` で検査する。
- 未確認の自動セッションが繰り返し開く退行。表示抑止により HUD の性質は成立するため、録音の開始・終了ログによる別の検査が必要。
- `delivery` glyphs (`processing`, `done`, `saved`, `failed`) and their expiry timers; `.ended`'s 1.5 s; `notify` paused/resumed. These expire on their own and are left out.
- Superwhisper's own recording (`delivery = .recording`, `followDirectSuperwhisper`): a HUD left on after direct Superwhisper use is not modeled.
- The voice activity gate (`minVoiceSeconds`, `lastAutomaticBlock`), the speaker gate, `skipWhileMicInUseBy`, the follow-up window, early wake, wake-word dictations, stop words (see `ce14722`) and the window stop check.
- Timing: frames are only an order here; 2.5 s, 60 s and the floor's 5 % step are not modeled sizes.
