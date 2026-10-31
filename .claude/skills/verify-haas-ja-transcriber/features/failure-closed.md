# Failure stays closed

When the runner fails, voice-switch does not deliver text and does not start Superwhisper or the local agreement transcriber in its place. The HUD failure state is the existing one.

## Sub-features

- `HJ-ROUTE-FAIL` covers non-zero exit, empty stdout, and timeout.
- The Mac failure returns `.noResult` (`HJ-SWIFT-WIRE`), which is the path that reports failure instead of local history.

## How to get to it (user POV)

- The GPU is down, the command exits non-zero, prints nothing, or hangs past `dictation.haas.timeoutSeconds`.
- Nothing is pasted, no pane receives text, and no action URL opens.
- The log says `dictation: haas runner failed`. The recording is kept. Superwhisper does not open.

## Driving it with verify.sh

Preconditions:

- `verify.sh doctor` is ok.
- The mock command is a script the test writes. It is not the Haas model.

- **Closed failures.** Run `verify.sh drive HJ-ROUTE-FAIL`. All three `PASS haas runner` failure lines are present. Callbacks for tmux, paste, and the action URL stayed at zero. The status is `NoResult`.
- **No local wrap.** The same evidence shows the handoff type is `HaasRunnerHandoff`.
- **Mac log.** Run `verify.sh drive HJ-SWIFT-WIRE`. The Haas failure returns `.noResult` and does not return `.notDispatched`.

## Gotchas

- `HandoffStatus.FailedBeforeDispatch` on Windows is what `LocalHistoryHandoff` turns into a local transcript. Haas must not use that status for a runner failure.
- `.notDispatched` on Mac stores a local transcript. Haas must not use it.
- A timeout test that uses the default 120 seconds will blow the suite. The test config sets a one-second timeout.
- Killing only the shell and leaving a child running counts as a failed timeout.
