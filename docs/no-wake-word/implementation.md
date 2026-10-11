# No-wake-word implementation plan

The branch starts at main `8c11668277b629dd70432ed21c6ff22ee03d8e87`. Main already contains the pane-route branch through squash merges and the replay tools required by issue #17. No AGENTS.md exists in this revision.

## Definition of done

The VM deliverable is a local branch with tested telemetry, bounded local history, immutable wake provenance, fail-closed delivery, and opt-in wake rules. All supported logic and build checks must pass. Native UI, permission, audio, and measured accuracy checks stay explicitly unverified for the human. No push, PR, merge, deployment, new credentials, or nested model calls are part of this run.

## Design

A wake has one immutable rule, an input-idle sample, and a session identity. The recording carries that value through completion. Delivery uses that recorded value, never the current configuration or the last button pressed. Automatic results use local recognition and local history, so uncertain Superwhisper model metadata cannot leak automatic audio.

History is process-local, expires after 30 minutes, and holds at most 50 entries. Restart and Clear all remove it. Only an explicit selection copies text. No audio is persisted for automatic sessions.

An alternative was to send every recording through Superwhisper and check delivery afterward. It was rejected because Superwhisper can paste or upload before voice-switch sees the result. Another alternative was to infer safety from whichever mode is currently configured. It was rejected because reloads and overlapping work would change an already-open session's authority.

## Throughput checkpoint

- Blocking first steps. Freeze acceptance criteria, capture baseline tests, and inspect recording and delivery paths before changing behavior.
- Independent workstreams. C#, Swift, and evaluation code are separate files, but cross-platform contracts are verified together after each slice.
- Shared mutable state. One worker owns this checkout. Recognition state stays on its runtime loop. History and handoff ownership have explicit synchronization.
- Smallest safe decomposition. One worker is required by the task. Implement and review each slice sequentially without nested agents.

## Ordered slices

1. S1a. Input-idle and rule telemetry, explicit MISS labels, replay and metrics support, frozen acceptance gates.
2. S1b preparation. Native validation checklist. Actual Mac and Windows observations remain a human gate.
3. S2 and S3. Bounded local history, selection UI, immutable delivery, and guarded manual paste.
4. S4. No permission prompt or event-tap interception when Accessibility is absent.
5. S5a through S5c. Disabled-by-default follow-up, input-idle wake, and reading distance 2 for 5 through 9 characters. Existing preroll supplies onset audio. No unsupported near-voice heuristic.
6. S6 preparation. Per-rule/context evaluation with explicit labels and exposure, without claiming live acceptance.

Each slice ends with tests and a local commit. Native limitations are not counted as passing checks.

## Opt-in controls

Set individual fields in `dictation.noWakeWord` after native validation. `followup` opens an 8-second history-only window after a manual recording. `autoIdle` requires at least 3 seconds without keyboard or mouse input at speech onset. `relaxedReadingDistance2` accepts only whole closed readings of 5 through 9 characters at distance 2. The existing longer-reading matcher remains unchanged. Every field defaults to false.

Automatic recording keeps the configured preroll and starts only after the existing minimum speech duration. It has no near-voice score. A strict wake word spoken after an automatic recording starts does not grant paste permission. A follow-up recording does not renew the window indefinitely. Unknown idle time and excluded applications block the start. Mac additionally requires a nonempty `dictation.superwhisperMode`.

Ctrl+Shift+F10 starts a manual recording while the listener is running. Repeating it inside an automatic session does not upgrade the session. The recording still finishes through the existing stop word, silence, or finish command.

## Controlled handoff and recovery

Production manual handoff now requires a mode whose JSON explicitly has `autoPaste: false`, and a confirmed switch to that mode. Missing or ambiguous metadata falls back to local transcription and history. A safe manual paste can then run from that history entry. This changes legacy configurations that previously let Superwhisper paste without checks. Configure a dedicated no-auto-paste mode to keep the Superwhisper path.

Mode switches write a recovery record before the switch. Startup and the next handoff check it. If the active mode is still the temporary mode, the app requests the previous mode and verifies it. If the user selected another mode, recovery preserves that choice. Unknown state blocks external handoff and uses local history. Both platforms serialize in-process handoffs and take a filesystem lock across processes. Native crash, delayed URL handling, and Superwhisper mode-schema tests remain required.

Clear all invalidates pending local transcription results. Pause invalidates queued Mac capture frames and in-flight local results by capture generation; Windows cancels its local handoff task. Neither operation can cancel an already-dispatched manual Superwhisper operation.

## Reproduce VM checks

With .NET 8 and Swift 6 on PATH, run `scripts/verify_no_wake_word.sh`. It builds the Windows app and native probe projects, runs the C# behavior suite, pure Swift parity and URL-action suites, evaluation tests, and a Mac-target syntax parse. It does not open a microphone or run the GUI. The Mac parse checks syntax only. Use the separate native checklist for a real macOS build and Windows runtime tests.

Controlled handoff ignores every Superwhisper recording directory that existed before dispatch. If Superwhisper reuses a directory rather than creating a fresh one, the result times out rather than pasting an old transcript. This conservative behavior needs a native check against the installed Superwhisper version.

Macrowhisper preparation remains available for command-mode actions. Controlled dictation does not run it, because a hook could change the verified no-auto-paste mode before file intake.
