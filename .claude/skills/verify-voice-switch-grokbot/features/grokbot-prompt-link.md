# Grok Bot prompt link

Speaking a Grok Bot wake word (`グロック` or `grok bot`) records a message and opens `grokbot://app/v1/prompt?id=<BOT_ID>&text=<encoded message>` in the Grok Bot app. The app types the message into that Bot's composer. It does not send. The user presses send. The composer must have been empty. This deep-link contract is documented, not live-tested (Grok Bot staff forum, 2026-10-16).

## Sub-features

- `GB1` JSON `inputParam` of `text` puts the dictation in `text=`, not `input=`.
- `GB2` omitted `inputParam` keeps the old `input=` bytes for a non-grokbot URL.
- `GB3` `id=YOUR_BOT_ID` already on the URL is kept; `text` is appended.

## How to get to it (user POV)

- Copy the Bot id (last path part of the Bot menu "Copy link") into `config.example.grokbot.json` in place of `YOUR_BOT_ID`.
- Speak `グロック` or `grok bot`, then the message.
- On this Linux box, load that action JSON and print `InputUrl`.

## Driving it with verify.sh

Preconditions:

- `$V doctor` passes.
- After implementation, `WakeAction` honors `inputParam`. Until then this recipe fails with `GOT` using `input=` and `WANT` using `text=`. Do not weaken `WANT`.

- **Compose grokbot text.** Run `VS_RUN_ID=<name> $V drive grokbot-prompt-link`. The probe loads `name=grokbot`, `url=grokbot://app/v1/prompt?id=YOUR_BOT_ID`, `inputParam=text`, text `hello world`.
- **Proof.** `exit=0`. `stdout.txt` contains `GOT grokbot://app/v1/prompt?id=YOUR_BOT_ID&text=hello%20world` and `MATCH`. `input=` must not be the query name for the dictation.
- **Default still input.** `$V tests "wake action URL with default inputParam stays byte-identical to input="` prints that `PASS` line. `$V tests "wake action inputParam=text puts the dictation in text="` prints that `PASS` line.
- **Swift mirror.** `$V swift-mirror grokbot` exits 0.

## Gotchas

- `YOUR_BOT_ID` is a placeholder. Never substitute a real id in committed files.
- Unknown JSON keys are ignored by today's `ConfigLoader`, so a current-tree run loads the action and still writes `input=`. That is a failed proof, not a skipped one.
- Filling Grok Bot's composer, the empty-composer rule, and the no-auto-send rule cannot be proven here.
