# Route the text

After a wake-word recording, Haas text goes to the same places Superwhisper's text goes today: one tmux pane, a paste into the focused app when no pane takes it, or an action URL. The mock runner stands in for the GPU.

## Sub-features

- `HJ-ROUTE-OK` covers tmux, paste, and the action URL.
- `HJ-SWIFT` covers the Swift runner contract with the same mock idea.
- `HJ-SWIFT-WIRE` checks that Mac calls the Haas path before it would open Superwhisper.

## How to get to it (user POV)

- Set `dictation.transcriber` to `haas` and `dictation.haas.command` to a program that prints the transcript.
- Say the wake word, then the body, then stop. The body WAV is handed to that command.
- If the words name one tmux pane, that pane receives them. Otherwise the focused app is pasted. An action wake opens its URL with the text and does not paste.

## Driving it with verify.sh

Preconditions:

- `verify.sh doctor` is ok.
- No Superwhisper app is required. The test supplies a mock command.

- **Windows route.** Run `verify.sh drive HJ-ROUTE-OK`. Evidence shows the three `PASS haas runner sends` / `pastes` lines. The recorded process list has the mock argv and no `superwhisper`.
- **Swift runner.** Run `verify.sh drive HJ-SWIFT`. Either the seven `PASS haas swift` lines appear, or one `SKIP HJ-SWIFT` line quotes the `swiftc` error.
- **Mac wiring.** Run `verify.sh drive HJ-SWIFT-WIRE`. The check reports the Haas call before `/usr/bin/open`.

## Gotchas

- Paste on the Superwhisper path is done by Superwhisper. The Haas path has to paste itself. A test that only checks the tmux callback misses that.
- Action text must not also go to tmux or paste.
- Automatic recordings stay in history and are not sent. Do not require a send for those.
- `HJ-SWIFT` skip does not prove the Mac binary. `HJ-SWIFT-WIRE` is the check that still runs.
