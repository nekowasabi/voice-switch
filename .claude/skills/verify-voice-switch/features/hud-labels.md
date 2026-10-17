# HUD labels

The tray HUD (`dotnet/VoiceSwitch.Windows/Tray/DictationHud.cs`) shows the dictation phase at the top center of the foreground display. Its labels must be emoji or symbols only, with no letters, and must equal the Mac HUD strings.

## Sub-features

- `Followup` `🔁📝`, `Waiting` `🎤`, `Recording` `🔴` (red), the `HistoryAvailable` label `📝👆`.
- Delivery, from `DeliveryChanged` around each handoff (`Deliver`): `Ended` and a started handoff show `⏳`; `Ended` alone hides after 1.5 s, a handoff holds it. A Superwhisper result shows `✅` for 1.5 s, no result or a throw `❌` for 3 s, a dropped or local-only one hides `⏳`. `📝👆` survives a result or a drop.
- `Notify`: the `pauseShortcut` hotkey shows `🔕` (paused) or `🔔` (resumed), hidden after 1.5 s.
- Acceptance: no `\p{L}` in any label, 9 distinct labels, each fits 64x40, same 9 strings as the Mac HUD, and every glyph renders in `Segoe UI Emoji` (no tofu box).

## How to get to it (user POV)

- Say the wake word with the tray running, or end an automatic recording by silence for the history label.

## Driving it with verify.sh

- Not drivable here. `$V wav` runs the console runtime, which has no HUD.
- The source checks (letters, parity) run from the Mac skill: `.claude/skills/verify-voice-switch-mac/verify.sh hud` reads `DictationHud.cs`. Quote its `windows-literals.txt` lines.
- Rendering needs Windows: run `dotnet run --project tests/windows/HUDScreenProbe -c Release` on a Windows desktop and take a screenshot per phase. Until then report rendering as not verified, reason: needs a Windows desktop.

## Gotchas

- GDI+ in a WinForms `Label` can draw Segoe UI Emoji as monochrome outlines. A label that is distinct only by color on Mac may not be on Windows; judge the screenshot.
- `Recording` sets `ForeColor` red. That colors monochrome glyphs only, so do not rely on it for color emoji.
