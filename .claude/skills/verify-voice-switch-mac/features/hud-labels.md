# HUD glyphs

A 64x40 HUD at the top center of the focused display shows the dictation from the wake word until superwhisper has finished with it, then closes. Superwhisper's own HUD is meant to be off, so this is the only one. The HUD draws vector glyphs (`HUDLabel` + `HUDGlyphView` in `MacApp.swift`, `HudGlyph` in `DictationHud.cs`), not emoji: emoji rendered differently on each OS and read as decoration. The two platforms share one 24x24 design space and one set of glyph names.

## Sub-features

- `waiting` (microphone): the wake word was heard, speak now.
- `recording` (red dot with a pulse ring): recording is confirmed, or superwhisper holds the mic after its own toggle key.
- `processing` (spinning arc): superwhisper works on the recording. A stop-word end shows it at once; a short recording dropped before handoff hides it after 1.5 s.
- `done` (green check): superwhisper's run is judged (`delivery: done`, `superwhisper direct: done`); shown 1 s, then the HUD closes.
- `failed` (red cross): no result within 30 s or no words; shown 3 s, then hidden.
- `followup` (loop arrow): continuous listening after a wake-word dictation.
- `saved` (green check over a rule): an automatic recording went to history; click opens the history panel; hidden after 1.5 s.
- `paused` (two bars) / `resumed` (play triangle): the `pauseShortcut` hotkey (or the menu item) toggled listening; shown 1 s over any other glyph, which then comes back.
- Action icons sit on waiting and recording. They are pngs named by `ActionIcons`, not extra `HUDLabel` cases. `grok-bot` is the builtin for a Grok Bot name or prompt URL. Another product is a row in that table, or `"icon": "stem"` plus `stem.png`.

Acceptance, checked by `$V hud`:

1. The 9 glyphs are distinct, each is centered in the 64x40 HUD, and none is larger than the HUD.
2. Sequences on the real HUD hold: `ended-then-done`, `failed-hides`, `new-dictation-wins`, `saved-survives-done`, `dropped-ended-hides`, `notice-then-phase`.
3. `direct=polished|heard-nothing|esc-deleted|esc-empty ok`, each within 2.5 s: `awaitDirectRun` waits on superwhisper's placeholder meta.json and ends on the judged one (done, failed), and treats a run folder that is deleted or stays empty 1.5 s after the mic closes as Esc (cancelled, HUD closes without the failed glyph). Measured on real superwhisper 2026-10-17: a stop writes meta.json within 0.2 s of the mic closing; Esc deletes the folder at the same moment, and once left it empty.
4. The Mac `HUDLabel` names equal the Windows `HudGlyph` names (`parity.diff` is empty), so neither platform can grow a state the other lacks.
5. Neither HUD draws a text or emoji label (`label.Text` / `labelWithString` absent), which is the regression guard for this change.

Shapes are drawn twice, once per platform, so a shape must be changed in both files. The Windows side flips its graphics transform to Y-up so the same coordinate table means the same drawing on both.

## How to get to it (user POV)

- Wake word, speech, stop word: mic, red dot, spinner, green check, then closed once superwhisper pasted.
- An automatic recording that ends on silence: red dot, spinner, check-over-rule.
- superwhisper's own toggle key: red dot while it records, spinner, green check, then closed.

## Driving it with verify.sh

Preconditions:

- `$V build` on the tree you are proving. `$V doctor` built-from matches HEAD and dirty count.

- **Glyphs and sequences.** `VS_RUN_ID=<id> $V hud`. Proof is the nine `phase=... glyph=... centered=yes` lines, six `sequence=<name> ok` lines, four `direct=<name> ok` lines, `PASS: every HUD glyph is distinct, centered, and fits the HUD; every sequence holds`, and the two `ok` source lines.
- **Listener drives delivery.** Copy `config.stop-auto.json` to your scratchpad with `"superwhisperMode": "vs-verify-missing"`, then `$V say busy-speech "[[slnc 1500]] 今日の会議は三時からです [[slnc 3000]]"` and `SIMULATE_REALTIME=1 VS_RUN_ID=<id> $V run busy-speech <config>`. `stdout.txt` must show `phase: recording`, `phase: idle`, `delivery: processing`, `dictation recognition: complete`, `delivery: done`, in that order. Proven 2026-10-17 (run `hud-delivery2`). No superwhisper is touched.
- Evidence: `evidence/<id>/hud/glyphs.txt`, `mac-glyphs.txt`, `windows-glyphs.txt`, `parity.diff`.

## Gotchas

- `--hud-labels` opens a real panel for about 15 s (the sequences wait for timeouts). It takes no focus and touches no microphone or superwhisper.
- `screencapture` prints `could not create image from window` without Screen Recording permission; `glyphs.txt` is still the proof.
- Rendering still needs a Windows desktop: run `dotnet run --project tests/windows/HUDScreenProbe -c Release` and read its glyph assertions plus a screenshot per phase.
- A glyph added to one platform without the other fails `$V hud` on `FAIL Mac and Windows HUD glyph names differ`.
- The paste-then-close path needs real superwhisper (wake-word config with `dictation`, tell the user first).
- The direct toggle path runs only in the menu bar app, not `--simulate`, and drives the user's superwhisper (tell the user first). Recipe used on 2026-10-17: TextEdit with a new document in front, `VOICE_SWITCH_CONFIG=<scratch copy of config.safe.json> <built binary> &` beside the installed app, `open -g "superwhisper://record"` to start, then either `osascript -e 'tell application "System Events" to key code 53'` (Esc) or the same URL again to stop, then kill the PID you started and close the document unsaved. The app writes to the shared `~/Library/Logs/voice-switch.log`, not stdout, and the installed app logs the same lines, so look for a string only the new build emits. Expected: Esc gives `superwhisper direct: processing` then `cancelled` about 1.6 s later; a stop gives `done` or, for silence, `failed`. The watcher logs `superwhisper direct: recording|processing|done|failed|cancelled`.
- Esc after the recording stopped (while superwhisper's language model runs) is not measured; it falls back to the failed glyph after 30 s.
- Two handoffs in flight at once (an automatic one and a wake-word one) share one delivery state; the first to finish closes the spinning arc.
- Multi-display placement is `scripts/hud_screen_proof.py --live`, which no longer compiles standalone (`no such module 'OnnxRuntimeBindings'`, 2026-10-17).
