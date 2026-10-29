# Example config and docs

A committed example config shows both `macOS.actions` and `windows.actions` grokbot entries with placeholder `YOUR_BOT_ID` and `inputParam` `text`. `docs/grokbot-message.md` plus short README sections tell a user how to copy the Bot id, the documented limits, and how to try `--fire` or speaking. Mac runtime stays unverified on this box.

## Sub-features

- `CD1` `config.example.grokbot.json` loads through `ConfigLoader` and lists `name=grokbot`.
- `CD2` both `macOS.actions` and `windows.actions` contain an entry with `"inputParam": "text"` and `YOUR_BOT_ID` in the URL. Never a real bot id or token.
- `CD3` `docs/grokbot-message.md` records the prompt-link contract as documented, not live-tested, and names webhooks as a future option only.

## How to get to it (user POV)

- Copy `config.example.grokbot.json` and replace `YOUR_BOT_ID`.
- Read `docs/grokbot-message.md` for Windows and Mac setup.

## Driving it with verify.sh

Preconditions:

- `$V doctor` passes.
- The example file and docs exist. Until they are added, this recipe fails.

- **Load the example.** Run `VS_RUN_ID=<name> $V drive grokbot-config-docs`.
- **Proof.** `exit=0`. `stdout.txt` contains `name=grokbot`. Both `macOS.actions` and `windows.actions` have `"inputParam": "text"` and `YOUR_BOT_ID` in the URL. `docs/grokbot-message.md` contains `documented, not live-tested`. `README.md` and `README_ja.md` contain `Grok Bot`.

## Gotchas

- `ConfigLoader.Validate` requires top-level `wakeWords` and `command`. The example must include them even though the grokbot path is the actions array.
- Windows loads `windows.actions` and ignores `macOS`. Swift does the reverse. Both blocks belong in the same file.
- `--fire` runs the default `command` / `wakeURL`, not a named action, and does not fill `text=`. The docs must say that speaking the grokbot wake word is the path that fills the composer.
