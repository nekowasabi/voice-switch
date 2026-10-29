# HUD glyphs

The tray HUD (`dotnet/VoiceSwitch.Windows/Tray/DictationHud.cs`) shows the dictation phase at the top center of the foreground display. It draws vector glyphs (`HudGlyph`) with GDI+, not emoji text, so the artwork no longer depends on `Segoe UI Emoji` and matches the Mac HUD shape for shape. An action icon is a png named by `ActionIcons`, drawn over waiting and recording. It is not a phase glyph. The nine phase names must equal the Mac `HUDLabel` cases.

## Sub-features

- `Followup` (loop arrow), `Waiting` (microphone), `Recording` (red dot with a pulse ring), the `HistoryAvailable` glyph `Saved` (check over a rule).
- Delivery, from `DeliveryChanged` around each handoff (`Deliver`): `Ended` and a started handoff show `Processing` (spinning arc); `Ended` alone hides after 1.5 s, a handoff holds it. A Superwhisper result shows `Done` (green check) for 1.5 s, no result or a throw `Failed` (red cross) for 3 s, a dropped or local-only one hides `Processing`. `Saved` survives a result or a drop.
- `Notify`: the `pauseShortcut` hotkey shows `Paused` (two bars) or `Resumed` (play triangle), hidden after 1.5 s.
- Action icons: `ActionIcons.Builtin` or an action's `icon` stem. `grok-bot` covers the Grok Bot name and the prompt URL. A new product is one row, or `"icon": "stem"`, plus `stem.png`.
- Acceptance: 9 distinct phase glyphs, each centered in and no larger than 64x40, the same 9 names as the Mac HUD, and `VisibleGlyph` reports null once the native window is hidden.

## How to get to it (user POV)

- Say the wake word with the tray running, or end an automatic recording by silence for the history label.

## Driving it with verify.sh

- Not drivable here. `$V wav` runs the console runtime, which has no HUD.
- The source checks (glyph names, no text labels) run from the Mac skill: `.claude/skills/verify-voice-switch-mac/verify.sh hud` reads `DictationHud.cs`. Quote its `windows-glyphs.txt` and `text-labels.txt` results.
- Rendering needs Windows: run `dotnet run --project tests/windows/HUDScreenProbe -c Release` on a Windows desktop and take a screenshot per phase. Until then report rendering as not verified, reason: needs a Windows desktop.

## Gotchas

- The glyphs are drawn twice, once per platform. A shape changed in `HUDGlyphView` must be changed in `HudGlyph` too; only the names are checked automatically.
- Windows flips the graphics transform to Y-up so the two files share one coordinate table. Without that flip the whole glyph set draws upside down.
- `Showing` a state starts a 30 fps timer for `Recording` and `Processing` only. Both HUDs stop the timer when they hide, so a hidden HUD costs no CPU.
