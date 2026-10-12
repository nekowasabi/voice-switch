# Dictation and voice-control products: when they listen, when they transcribe, and how they avoid false activation

Scope note: this is a light pass (about 14 tool calls, search snippets plus one README fetch). Only the Handy README was read in full. Most other claims come from search snippets, so treat them as "found in snippet" and check the primary source before relying on them. Items marked **UNVERIFIED** come only from third-party pages, forum posts, or competitor blogs. Products with no usable result are listed as gaps.

## Comparison table

| Product | Start / end of input | Guard against unintended activation | Feedback (sound / visual, can it be disabled?) | Processing | Review / edit / history | Reported complaints | Confidence |
|---|---|---|---|---|---|---|---|
| **Superwhisper** | Global shortcut starts recording in the active mode; each mode can have its own shortcut; mode changer opened by press-and-hold. Recording ends by its own controls (not fully documented here). | Silence removal, described as preventing hallucinations in silent parts, not as activation control. | Two audio cues (recording start, processing done); sound volume can be set separately from system volume, or fully disabled. Recording window can be turned off, but audio cues still play. | Not checked in this pass. | Not checked in this pass. | Not checked in this pass. | Medium (official docs, snippet level) |
| **Wispr Flow** | Hold-to-talk. Hands-free toggle exists. Mac trigger conflicts between sources: Fn hold and Fn+Space toggle (cheat sheet) vs. double-tap Fn (newsletter). | Not found. | Not found. | Cloud-based by default. Not verified. | Auto-editing mentioned; details not found. | Not checked in this pass. | Low. The official help page was not found. |
| **MacWhisper** | Not found. | Not found. | Not found. | Not found. | Not found. | Not found. | Gap |
| **Aqua Voice** | Not found. Search results were all about other apps. | Not found. | Not found. | Not found. | Not found. | Not found. | Gap |
| **Talon Voice** | Always listening by default; voice phrases "go to sleep" / "wake up" toggle it. Noise (pop, hiss) can be a command input. | Sleep state (voice phrase). Custom prefix word ("computer, <command>") is a user-built pattern, not a built-in. | Not checked in this pass. | Local (not checked in this pass). | Not checked in this pass. | Users warn that forgetting to sleep causes problems when others speak or video plays (UNVERIFIED, single user post). | Low to medium |
| **Willow / Willow Voice** | Hold hotkey to dictate. Since v1.2.13 to 1.2.15 (July 2025 changelog), a hotkey toggles hands-free mode directly (no double-tap). Mac-only per YC listing. | Not found. | Not found. | Vendor claims (accuracy, languages) are not independent. | Yahoo Tech article claims editing what you said (iOS keyboard). Only weakly sourced. | Not checked in this pass. | Medium for start/stop; low for the rest |
| **Windows Voice Typing (Win+H)** | Win+H starts listening; "Stop listening" voice command ends it. Also stops on focus change and manual typing (per a competitor blog). Cloud-based. | Built-in silence timeout (about 5 to 10 s, per a competitor blog). Microsoft's stance (per the same blog, via a community support reply): intentional and not user-configurable. | Not checked in this pass. | Cloud-based (needs internet). | None found. | Stops mid-sentence after silence; weak mic reads as silence; Anki forum reports it stops in Anki but not in Chrome or Notepad. | Low to medium |
| **Windows Voice Access** | Not checked in this pass. | Not checked. | Not checked. | Not checked. | Not checked. | Not checked. | Gap |
| **Apple Dictation / Voice Control** | Voice Control listens until "Go to sleep" is said or Sleep is clicked; "Wake up" resumes. Menu bar item starts or stops listening (Sonoma 14 and later). "Command mode" / "Dictation mode" switch. No default keyboard shortcut (third-party doc, UNVERIFIED). | Sleep state (voice phrase). Dictation and command modes are separate. | Not checked in this pass. | Apple's support page is the source for the commands. Privacy claim ("nothing shared with Apple") is from a dated Catalina-era article, so verify it. | None found. | Not checked in this pass. | Medium (Apple doc for commands) |
| **Dragon (Nuance)** | Wake phrase "Wake up"; sleep phrase "Go to sleep" (or "Microphone off", version-dependent). Wake via DragonBar click, tray click, or hotkey (Windows default: numeric keypad plus). Mac auto-sleep after 1 minute of inactivity by default. | Sleep mode ignores all speech except "Wake up"; the sleep vocabulary is limited to that command (Nuance script reference). The mic keeps monitoring for "Wake Up" while asleep (IDRC tutorial). | Not checked in this pass. | Not checked in this pass. | Not checked in this pass. | Not checked in this pass. | Medium (Nuance docs, snippet level) |
| **Handy (open source, Tauri, MIT)** | Push-to-talk default: hold to record, release to transcribe. Toggle mode: tap to start and stop. Hold-only and toggle-only modes available. No always-on mode on the README. | Silero VAD filters silence (no tuning details). No always-on means no activation guard is needed. | Audio feedback off by default; enable in Settings > Advanced. Visual overlay: set "Overlay Position" to None to disable. Linux overlay off by default. | Local, offline (Whisper models or Parakeet V3). | History view stores transcripts; browse via the Raycast integration. No copy-last or retry found. | Not checked in this pass. | High (README fetched) |
| **VoiceInk (macOS, GPL v3 per third party)** | Configurable shortcuts for recording and push-to-talk. No always-on mode found. | Not found. | Not found. | Local Whisper, 100% offline per its docs. | Not found. | Not checked in this pass. | Medium for shortcuts; low for the rest |
| **whisper-writer, nerd-dictation, WhisperX-based tools, OpenWhispr** | Not checked in this pass. | Not checked. | Not checked. | Not checked. | Not checked. | Not checked. | Gap |

## Per-product notes

**Superwhisper.** Two sound cues (recording start, processing complete) can be turned off or set to a separate volume (Settings > Sound). Silence removal exists "to help prevent AI hallucinations in silent moments," so it is a transcription-quality feature, not an activation filter. Per-mode shortcuts exist. Source: https://superwhisper.com/docs/get-started/settings-sound and https://superwhisper.com/docs/get-started/essential-settings. Not checked: file transcription via URL scheme, and recording-mode details. The URL-scheme feature was not searched for, so it is still open.

**Wispr Flow.** Hold-to-talk is the stated workflow. Hands-free exists. The cheat sheet (https://video.pie-menu.com/wispr-flow-cheat-sheet.pdf, third party) gives Fn hold for push-to-talk and Fn+Space for hands-free toggle. A newsletter (https://rewskidotcom.substack.com/p/wispr-flow-the-dictation-superpower) gives double-tap Fn on Mac and Ctrl+Win+Alt on Windows. The two conflict, so both are UNVERIFIED until checked in the app. No official help page was found. Accessibility framing (replacing typing for RSI or mobility limits): https://wisprflow.ai/post/revolutionizing-accessibility-tools.

**Talon Voice.** Always listening is the default. Voice phrases "go to sleep" and "wake up" toggle it (user post, UNVERIFIED as a source; see https://archive.ejfox.com/wiki/Talon_Voice_Control for a community wiki). Talon's official docs list pop and hiss noise recognition, with more planned (https://talonvoice.com/docs/). A user reported that a missed sleep leads to accidental commands when others speak or a video plays. Found no documented way to sleep or wake Talon with a noise. Noise handling beyond this summary was not checked.

**Willow.** Hold the hotkey and text lands at the cursor (https://willowvoice.com/features/dictation). Hands-free toggle via the same hotkey, with multiple custom hotkeys, from the July 2025 changelog (https://willowvoice.featurebase.app/changelog). The old double-tap entry is removed. The YC listing says Mac-only for now (https://www.ycombinator.com/companies/willow), and a later iOS keyboard launch is reported by Yahoo Tech (https://tech.yahoo.com/ai/meta-ai/articles/willow-voice-keyboard-lets-type-072100796.html). Accuracy and language claims are vendor claims.

**Windows Voice Typing (Win+H).** The silence timeout is the main reported complaint. A competitor blog (https://www.yaps.ai/de/blog/windows-voice-typing-keeps-stopping, English version not fetched) says it stops after roughly 5 to 10 seconds of silence, citing a Microsoft community reply that calls it intentional and not user-configurable. This is UNVERIFIED because it comes from a competitor. An Anki forum thread (https://forums.ankiweb.net/t/windows-voice-typing-functionality-is-limited-on-this-app/23255) reports app-specific stopping. Win+H is cloud-based. Microsoft's own support page was not located, so the "Stop listening" command is unverified from a primary source.

**Apple Voice Control.** This is the clearest official sleep/wake model found. "Go to sleep" stops command interpretation; "Wake up" resumes it. A menu bar item starts or stops listening (Sonoma 14 and later). Source: https://support.apple.com/guide/mac-help/control-your-mac-and-apps-using-voice-control-mh40719/11.0/mac/11.0. The privacy claim comes from an older Computerworld article (https://www.computerworld.com/article/3442721/how-to-use-voice-control-in-macos-catalina.html) and is dated.

**Dragon.** Sleep and wake by phrase, plus a sleep vocabulary limited to "Wake Up." This is a constraint on what can be recognized while asleep (Nuance script reference: https://www.nuance.com/products/help/dragon/dragon-for-pc/scriptref/Content/scrptref/gotosleep.htm). Mac auto-sleep after one minute of inactivity (Mac microphone modes: https://nuance.com/products/help/dragon/dragon-for-mac/enx/Content/Introduction/Microphone/MicrophoneModes.html). The IDRC tutorial (https://legacy.idrc.ocadu.ca/resources/idrc-online/51-tutorials/tutorials/130-dragon-naturally-speaking-basic-features.html) says the mic still monitors for "Wake Up" while asleep.

**Handy.** The most useful open-source reference found, based on a full README fetch (https://github.com/cjpais/Handy). Default is push-to-talk. Toggle mode is an option. Audio feedback and the overlay are both optional and both easy to turn off. Silero VAD removes silence (https://github.com/cjpais/handy, search snippet). It has no always-on listening, so it avoids the activation problem entirely.

**VoiceInk.** Shortcut-driven recording with push-to-talk (https://tryvoiceink.com/llms.txt). No always-on listening found. Price and license came from a competitor blog and are UNVERIFIED.

## Key questions

### 1. Which products offer always-on or hands-free listening, and how do they keep false activations low?

**Takeaway:** Talon and Apple Voice Control and Dragon all use an always-listening-with-sleep model, where the sleep state restricts what is acted on. Hands-free in Willow and Wispr is a hotkey toggle, not a wake word. No product found in this pass uses energy-only auto-start like the user's 3-second idle mode.

#### Cited Findings
- Apple Voice Control listens until "Go to sleep", and resumes on "Wake up" (https://support.apple.com/guide/mac-help/control-your-mac-and-apps-using-voice-control-mh40719/11.0/mac/11.0).
- Dragon's sleep mode ignores everything except "Wake Up" (https://nuance.com/products/help/dragon/dragon-for-mac/enx/Content/Introduction/Microphone/MicrophoneModes.html). Mac sleeps after one minute of inactivity by default (same source).
- Talon is always listening by default; "go to sleep" and "wake up" toggle it (UNVERIFIED user report, https://www.greaterwrong.com/posts/SsTzdyaCqqyxanp3C/how-did-you-integrate-voice-to-text-ai-into-your-workflow/answer/fgm6jzCcetwjpytTm may not be the source; treat as unsourced).
- Willow's hands-free mode is toggled by a hotkey, and the double-tap was removed (https://willowvoice.featurebase.app/changelog).

#### Inferences
- Restricting the sleep-state vocabulary to one wake phrase (Dragon) is a plausible way to limit false triggers. This is my inference, not a stated design goal.
- Auto-off timers (Dragon's one-minute Mac auto-sleep) address the same "stays open too long" problem that caused the user's distraction.

#### Gaps
- No product found whose energy-based VAD auto-start was documented. Whether any tool keeps a short idle auto-start is unknown.
- Talon's noise-based handling and any sleep tuning were not fully checked.

### 2. Which products deliberately avoid always-on listening, and what reasons do they give?

**Takeaway:** Handy and VoiceInk default to explicit key-driven recording. The only stated reason found is privacy and offline processing (Handy, VoiceInk). Windows Voice Typing and Superwhisper did not state a reason for avoiding always-on. The Windows timeout is presented as intentional.

#### Cited Findings
- Handy: "Your words appear in any text field without sending your voice to the cloud" (privacy framing, https://github.com/cjpais/handy search snippet). Push-to-talk default (https://github.com/cjpais/Handy).
- VoiceInk: offline, local Whisper (https://tryvoiceink.com/llms.txt).
- Windows silence timeout is described by a Microsoft community reply as intentional, relayed by a competitor (UNVERIFIED, https://www.yaps.ai/de/blog/windows-voice-typing-keeps-stopping).

#### Inferences
- Most of these tools choose explicit start as a privacy and accuracy default. Silence-driven stopping is the main cause of the Windows complaints, so an explicit stop is more predictable.

#### Gaps
- No product's official docs were found that give a false-activation reason in writing. Not verified.

### 3. Is there any product that uses "record always, decide later" (retroactive) or "draft then confirm"?

**Takeaway:** Not found. Handy keeps a History of transcripts, which is post-hoc review but not retroactive capture. No product found that buffers audio before a trigger and lets the user keep or drop it.

#### Cited Findings
- Handy: History view stores transcripts; browse via Raycast (https://github.com/cjpais/Handy).
- Willow: "actually edit what you said" (Yahoo Tech, https://tech.yahoo.com/ai/meta-ai/articles/willow-voice-keyboard-lets-type-072100796.html). The phrase refers to editing, and the mechanism is not described.

#### Inferences
- The "record then confirm" pattern appears to be rare among these products. This is an inference from absence in this light search, not a verified negative.

#### Gaps
- Superwhisper history and any "paste last" features were not checked.
- Aqua Voice, MacWhisper, and the open-source tools beyond Handy and VoiceInk were not searched successfully.

### 4. What do power users report as the best way to start dictation?

**Takeaway:** Hold-to-talk and toggle are the common ways. Hotkey-toggled hands-free (Willow, Wispr) is the second-most reported. Wake phrases are used by Dragon, Apple, and Talon, typically for command mode rather than plain dictation. No foot-pedal evidence was found.

#### Cited Findings
- Handy supports hold, tap-to-toggle, and both modes separately (https://github.com/cjpais/Handy).
- Willow's hotkey toggles hands-free (https://willowvoice.featurebase.app/changelog).
- Wispr hold-to-talk is the stated workflow (https://wisprflow.ai/post/accessibility-voice-tools, not fetched in full; the cheat sheet gives Fn, UNVERIFIED).
- Dragon's wake phrase and hotkey wake (https://www.nuance.com/products/help/dragon/dragon-for-pc/scriptref/Content/scrptref/gotosleep.htm).

#### Inferences
- The least-ambiguous start is a hold key, since it has a natural end (release). This matches the design of a hotkey with a visible state.

#### Gaps
- Foot pedal use was not searched. No evidence either way.
- Forum or Reddit user reports (requested sources) were not searched. Only vendor and product pages were used, so user complaints are thin.

## Notes on the user's problem

Relevant patterns found:
- An explicit hold or toggle start (Handy, Willow, Wispr) avoids the activation problem by design. The energy-based 3-second auto mode is the riskiest pattern in this list.
- Where always-on exists (Talon, Apple, Dragon), the design uses a sleep state with a restricted vocabulary plus an auto-sleep timer. This is the closest match to "silent unless intended."
- Feedback that can be disabled exists in Superwhisper (sound settings) and Handy (audio feedback off by default, overlay can be set to None). Both are good models for a silent default.
- Silence removal in Superwhisper and Silero VAD in Handy reduce hallucinations. They do not decide when to listen.

## Next steps (not done in this pass)

- Fetch Superwhisper's URL-scheme and recording-mode docs.
- Search Reddit and Hacker News for "Wispr Flow hands-free", "Win+H stops", and "Talon sleep" complaints.
- Check Aqua Voice, MacWhisper, Windows Voice Access, nerd-dictation, whisper-writer, WhisperX tools, and OpenWhispr directly from their official pages.
