---
name: verify-hud-blip
description: Model-check (TLA+) that a short sound that is not a voice (a cough, a keyboard, a door, speaker playback that blips the HAL voice detector) never shows the mic HUD on its own, on macOS and Windows, while someone who keeps talking still sees it. Use when a change touches the phase `defer` in Listener.consume, HUD.render's phase table, voiceRecentlyActive / automaticWakeAllowed / minVoiceSeconds, the idle path's speechFrames / phraseHAL, Dictation.confirmed / wordCheck in Sources/voice-switch/MacApp.swift, or PublishPhase / Confirmed / CheckWords in dotnet/VoiceSwitch.Windows/DictationRuntime.cs, and before claiming the "mic HUD flickers on small sounds" bug fixed.
---

# Verify a non-voice sound never shows the mic HUD (TLA+)

Users saw the mic HUD flash again and again with nobody talking to the app (2026-10-31). The runtime log for 08:20-08:31 JST showed 61 `hud: waiting phase=hearing` against 4 `automatic confirmed by agreed words` and 2 wake-word starts; 98 phrases ended `automatic wake blocked: no voice activity` and 103 `shorter than minSpeechMs`. `HudBlip.tla` models one pass of the frame loop per step, with the room choosing quiet, noise or voice each frame and the HAL detector allowed to blip on noise for less than `minVoiceSeconds`, and checks both platforms' HUD rule against the properties below. Real-app proof of a fix belongs in `../verify-voice-switch-mac` (Mac `--simulate`) and `../verify-voice-switch` (Windows exe); this skill covers only the loop logic. `../verify-hud-noise` is the neighbour: it checks that the HUD goes away in a room that settles, and leaves bursts between silences to this skill.

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `NoiseNeverShowsHud` (invariant) | A sound that is not a voice never shows the mic HUD on its own; the HUD is up only when a voice was heard in the current phrase or session. | The mic glyph flashes on coughs, typing and speaker playback, so the user can no longer tell when the app is really listening, and stops trusting it. |
| `VoiceShowsHud` (liveness) | Someone who keeps talking eventually sees the mic HUD. | A fix that simply hides the HUD passes the invariant; this one fails it, so the user still gets feedback that they are being recorded. |

`TypeOK` is checked as an invariant.

## Status

**A 案を採用済み。macOS は 198 状態、Windows は 194 状態を探索し、両性質を満たした**（2026-10-31 に再実行、終了コードはともに 0）。修正した点滅経路は次の 2 つ。

1. **待機中のフレーズ。** 雑音で `speechFrames > 0` と `phraseHAL > 0` になっても、`dictation == nil` なら `.idle`（既存の追従待機条件を満たす場合は `.followup`）。旧規則の `.hearing` は削除した。
2. **発声直後の自動セッション。** `voiceRecentlyActive()` により物音でセッションが開く経路は残るが、本文を聞いた自動セッションは `confirmed == false` の間 `.idle`。確認後は従来の `.recording` / `.stopping` になる。ウェイクワードとアクションの本文待ちは `.waiting` を保つ。

Windows holds both properties because `PublishPhase` shows an active automatic session only once `Confirmed(session)` (agreed words, `CheckWords`), and an idle phrase is `DictationPhase.Idle`.

## Run

```sh
sh .claude/skills/verify-hud-blip/check.sh mac
sh .claude/skills/verify-hud-blip/check.sh windows
sh .claude/skills/verify-hud-blip/break.sh
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked by the script, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` on first run, shared with `verify-hud-noise` and `verify-meet-pause`). Needs Java 11+ (`brew install openjdk` on the Mac). 合格時の出力は `Model checking completed. No error has been found.`。今回の実測は macOS 198 状態、Windows 194 状態。`sh check.sh <platform> <other.tla>` で写しを検査できる。`break.sh` は旧規則の写しだけを検査し、検査の非 0 終了と `Invariant NoiseNeverShowsHud is violated` の両方を確認したときだけ終了コード 0 を返す。今回も終了コード 0。

Read every result, pass or fail, with the `/statechart` skill: a violation as its shortest path in plain words, a pass as a chart of the states. `statechart.md` next to this file is a hand-drawn chart of the real code path from sound to judgement on both platforms; keep it in step with the model.

## Verification items for a noise fix (Mac and Windows)

A fix is done only when every row holds.

| # | Item | How | Pass |
|---|---|---|---|
| 1 | Noise alone never shows the HUD, Mac | `check.sh mac` with the model updated to the fix | `No error has been found` |
| 2 | Noise alone never shows the HUD, Windows | `check.sh windows` with the model updated to the change | `No error has been found` |
| 3 | A voice still shows the HUD, both | the same runs check `VoiceShowsHud` | no `Temporal properties were violated` |
| 4 | 旧規則への退行を検出する | `sh .claude/skills/verify-hud-blip/break.sh` | 終了コード 0（内部検査は非 0 かつ `Invariant NoiseNeverShowsHud is violated`） |
| 5 | Mac の実行ファイル、短い物音 | `../verify-voice-switch-mac` の `--simulate` で短い物音の音源を無音の間に置く | ウェイク開始・語の一致確認なしに、物音だけでマイク HUD が表示されない |
| 6 | Mac binary, a real phrase | the same skill with a spoken sentence fixture | `dictation session: automatic ... confirmed by agreed words` and a mic HUD line, delivery as before |
| 7 | Windows exe, short sounds | `../verify-voice-switch` with the same burst fixture | no `dictation phase: Recording` or `Waiting` in the log |
| 8 | 日常利用のログ | 1 日の利用後に `~/Library/Logs/voice-switch.log` を確認 | マイク HUD の表示が語の一致確認・ウェイク開始に対応する（`.hearing` は削除済みなので、そのログがないだけでは合格にしない） |

項目 1–4 は今回合格。項目 5–8 は実際のアプリでの確認が必要で、今回未実施。

## Coverage record

Sync commit: `cf89a2f` の作業ツリー（未コミット）。A 案の表示変更を反映し、同時に存在する `HUD.show` のアイコン修正はモデル外。コミットは作成していない。

| Model part | Code |
|---|---|
| `Frame` (one step) | one iteration of `for await frame in stream` in `Listener.consume` (MacApp.swift); one analysed frame in `DictationRuntime` (DictationRuntime.cs) |
| `h`, `HalOK` | `voiceBeingHeard()` and the HAL listener in `watchVoiceActivity` (`kAudioDevicePropertyVoiceActivityDetectionState`) |
| `halRun`, `sinceVoice`, `gate` | `voiceActivity.onSince` / `lastVoiceEnd` (set only after a run of `minVoiceSeconds`), `voiceRecentlyActive()`, `automaticWakeBlock()` -> `Listener.noVoiceActivity` |
| `speech` | `Segmenter.isSpeech`: energy over threshold or the HAL flag; noise is assumed over the threshold |
| `speechFrames`, `phraseHAL` | consume's `speechFrames` / `phraseHAL` on the idle path, reset after `hangoverMs` of gap (modeled as one frame) |
| idle path `dict' = "auto"` | `longEnough`, `NoWakePolicy.onSpeech`, `automaticWakeAllowed()`, `dictation = Dictation(... origin: origin)` |
| `w`, `dict' = "wake"` | a wake word or `macOS.actions` wake word recognized by `speaksWake` / `dictationStart` |
| dictation branch | `if var d = dictation { ... }`: `silentFrames`, end on `endSilenceMs` or `maxSeconds`, `dictation = nil; speechFrames = 0` |
| `confirmed` | `Dictation.confirmed` via `wordCheck` / `agreedWordCount` (Mac); `confirmedSession` via `CheckWords` (Windows) |
| `HudMac` | `dict = "wake" \/ (dict = "auto" /\ confirmed)`。phase の `defer` は本文後の未確認自動セッションと待機中のフレーズを `.idle` にし、確認後の `.recording` / `.stopping` とウェイクの `.waiting` を保つ |
| `HudWindows` | `PublishPhase(DictationSession, long)` and `Confirmed(session)` |

`relaxedD2` の本文待ちは `dict = "wake"` に対応させる。`heardSpeech` と `stopHeard` の組合せはモデル外。実コードでは、本文後の未確認セッションは停止語を聞いても `.idle`、本文待ちでは従来の `.waiting` / `.stopping` を維持する。後者の停止語表示は Windows の待機表示と異なり、このモデルは両実装の全時系列の一致を保証しない。

Bounds (`HudBlip-*.cfg`): `MinSpeech = 1`, `BlipMax = 2`, `MinVoice = 3`, `Recent = 3`, `EndSilence = 2`, `MaxAge = 4` frames. They keep the code's order (`minSpeechMs` 300 ms < measured blips 725-1236 ms < `minVoiceSeconds` 1.5 s), not its sizes.

Assumptions: `dictation.echoCancellation` is not `false` (the user's config), so the voice activity gate is on and the detector exists; `noWakeWord` is on; `lastAutomaticBlock` is nil or `noVoiceActivity` (no app holds the mic); the detector is on for every voice frame and never on in silence; a blip on noise lasts less than `minVoiceSeconds`; the agreement recognizers work and agree on no words for noise (a recognizer that throws confirms the session on both platforms, which would show the HUD on noise); on Windows `agreedCharacters != null`; a wake word needs a voice.

Windows で一致認識器がない場合は `Confirmed(session)` が自動セッション開始時から真となり、物音でも表示され得る。この構成はモデルの仮定外。

## Does my change touch this?

Run `git diff cf89a2f -- Sources/voice-switch/MacApp.swift dotnet/VoiceSwitch.Windows/DictationRuntime.cs` from the repo root and look for: the phase `defer` and `HUD.render`'s phase table, `voiceRecentlyActive` / `automaticWakeBlock` / `minVoiceSeconds` / `watchVoiceActivity`, the idle path's `speechFrames` / `phraseHAL` / automatic opening, `Dictation.confirmed` / `wordCheck`, or Windows `PublishPhase` / `Confirmed` / `CheckWords`. Any of these needs the matching model part updated before trusting the result.

## When it fails

1. Read the trace with `/statechart` and retell it as steps in code words, as in "Status" above.
2. Check each step against the code it names. If every step is something the code can do, it is a real bug: report it with that story and the harm. Do not weaken the property, shorten `BlipMax`, or remove a path.
3. If a step is something the code cannot do, the model is wrong: fix the model to match the code, and say what you changed.

## Updating the model after a fix

Mirror the code change in `HudMac` / `HudWindows` or `Frame`, run both platforms, and expect a pass. Then put the old rule back in a scratch copy and confirm it fails again (item 4). Move the sync commit only then.

Variants（2026-10-31。A 案は採用済み、旧規則への退行は今回実行、その他は過去の試行）:

- **Fix A 採用済み**（`HudMac == dict = "wake" \/ (dict = "auto" /\ confirmed)`）：今回の検査で両性質に合格、198 状態。確認前の自動セッションでもマイクを出し続ける旧挙動を廃止した。
- **旧 Mac 規則への退行**：今回の `break.sh` が `NoiseNeverShowsHud` 違反を検出し、終了コード 0。元モデルは書き換えない。
- **Fix B、待機中の `.hearing` だけを声活動条件で制限**（`... (speechFrames > 0 /\ phraseHAL /\ (halRun >= MinVoice \/ sinceVoice < Recent))`）：過去の試行では、未確認の自動セッションによる経路 2 が残って失敗した。今回の採用案ではない。
- **Break, Windows shows an unconfirmed session** (`HudWindows == dict # "none"`, i.e. `Confirmed` always true at DictationRuntime.cs:978): fails `NoiseNeverShowsHud` in 2 states.
- **Break, Windows never shows an automatic session** (`HudWindows == dict = "wake"`): passes the invariant and fails `VoiceShowsHud`, which is why the liveness property is there.

## Not covered

- Superwhisper's own recording (`delivery = .recording`), delivery glyphs (`processing`, `done`, `saved`, `failed`) and their timers, `.ended`, `.followup`, `notify`.
- The Windows opening gate (`SpeechGate.VoicedWindows`, speaker gate): modeled as always allowing, which only adds behaviors.
- Held phrases (`phraseHeldByVoice`, `heldPhrase`), early wake, stop words, the window stop check, hotkeys.
- The floor and `rebaseFloor` (see `../verify-hud-noise`); noise here is always over the threshold.
- Timing: frames are only an order; 300 ms, 1.5 s and the recognizer delay before `confirmed` are not modeled sizes.
