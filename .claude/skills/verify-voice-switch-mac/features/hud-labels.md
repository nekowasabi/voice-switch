# HUD labels

A 64x40 HUD at the top center of the focused display shows the dictation from the wake word until superwhisper has finished with it, then closes. Superwhisper's own HUD is meant to be off, so this is the only one. Labels are emoji only (`HUDLabel` in `MacApp.swift`); the Windows tray HUD (`DictationHud.cs`) must show the same strings.

## Sub-features

- `waiting` `🎤`: the wake word was heard, speak now.
- `recording` `🔴`: recording is confirmed, or superwhisper holds the mic after its own toggle key.
- `processing` `⏳`: superwhisper works on the recording. A stop-word end shows it at once; a short recording dropped before handoff hides it after 1.5 s.
- `done` `✅`: superwhisper's run is judged (`delivery: done`, `superwhisper direct: done`); shown 1 s, then the HUD closes.
- `failed` `❌`: no result within 30 s or no words; shown 3 s, then hidden.
- `followup` `🔁📝`: continuous listening after a wake-word dictation.
- `saved` `📝👆`: an automatic recording went to history; click opens the history panel; hidden after 1.5 s.

Acceptance, checked by `$V hud`:

1. No label contains a letter (`CharacterSet.letters`, `\p{L}` on the Windows literals).
2. The 7 labels are distinct and fit the 64 pt HUD.
3. Sequences on the real HUD hold: `ended-then-done`, `failed-hides`, `new-dictation-wins`, `saved-survives-done`, `dropped-ended-hides`.
4. `direct=polished|heard-nothing|esc-deleted|esc-empty ok`, each within 2.5 s: `awaitDirectRun` waits on superwhisper's placeholder meta.json and ends on the judged one (done, failed), and treats a run folder that is deleted or stays empty 1.5 s after the mic closes as Esc (cancelled, HUD closes without ❌). Measured on real superwhisper 2026-10-17: a stop writes meta.json within 0.2 s of the mic closing; Esc deletes the folder at the same moment, and once left it empty.
5. The Mac `HUDLabel` strings equal the Windows `label.Text` literals (`parity.diff` is empty).

## How to get to it (user POV)

- Wake word, speech, stop word: `🎤`, `🔴`, `⏳`, `✅`, then closed once superwhisper pasted.
- An automatic recording that ends on silence: `🔴`, `⏳`, `📝👆`.
- superwhisper's own toggle key: `🔴` while it records, `⏳`, `✅`, then closed.

## Driving it with verify.sh

Preconditions:

- `$V build` on the tree you are proving. `$V doctor` built-from matches HEAD and dirty count.

- **Labels and sequences.** `VS_RUN_ID=<id> $V hud`. Proof is the seven `phase=... label=...` lines, five `sequence=<name> ok` lines, four `direct=<name> ok` lines, `PASS: every HUD label is letter-free, distinct, and fits the HUD; every sequence holds`, and the two `ok` source lines.
- **Listener drives delivery.** Copy `config.stop-auto.json` to your scratchpad with `"superwhisperMode": "vs-verify-missing"`, then `$V say busy-speech "[[slnc 1500]] 今日の会議は三時からです [[slnc 3000]]"` and `SIMULATE_REALTIME=1 VS_RUN_ID=<id> $V run busy-speech <config>`. `stdout.txt` must show `phase: recording`, `phase: idle`, `delivery: processing`, `dictation recognition: complete`, `delivery: done`, in that order. Proven 2026-10-17 (run `hud-delivery2`). No superwhisper is touched.
- Evidence: `evidence/<id>/hud/labels.txt`, `mac-literals.txt`, `windows-literals.txt`, `parity.diff`.

## Gotchas

- `--hud-labels` opens a real panel for about 15 s (the sequences wait for timeouts). It takes no focus and touches no microphone or superwhisper.
- `screencapture` prints `could not create image from window` without Screen Recording permission; `labels.txt` is still the proof.
- Until `DictationHud.cs` gets ⏳ and ❌, `$V hud` exits 1 on `FAIL Mac and Windows HUD literals differ`. Everything else passing is the Mac proof.
- The paste-then-close path needs real superwhisper (wake-word config with `dictation`, tell the user first).
- The direct toggle path runs only in the menu bar app, not `--simulate`, and drives the user's superwhisper (tell the user first). Recipe used on 2026-10-17: TextEdit with a new document in front, `VOICE_SWITCH_CONFIG=<scratch copy of config.safe.json> <built binary> &` beside the installed app, `open -g "superwhisper://record"` to start, then either `osascript -e 'tell application "System Events" to key code 53'` (Esc) or the same URL again to stop, then kill the PID you started and close the document unsaved. The app writes to the shared `~/Library/Logs/voice-switch.log`, not stdout, and the installed app logs the same lines, so look for a string only the new build emits. Expected: Esc gives `superwhisper direct: processing` then `cancelled` about 1.6 s later; a stop gives `done` or, for silence, `failed`. The watcher logs `superwhisper direct: recording|processing|done|failed|cancelled`.
- Esc after the recording stopped (while superwhisper's language model runs) is not measured; it falls back to ❌ after 30 s.
- Two handoffs in flight at once (an automatic one and a wake-word one) share one delivery state; the first to finish closes ⏳.
- Multi-display placement is `scripts/hud_screen_proof.py --live`, which no longer compiles standalone (`no such module 'OnnxRuntimeBindings'`, 2026-10-17).
