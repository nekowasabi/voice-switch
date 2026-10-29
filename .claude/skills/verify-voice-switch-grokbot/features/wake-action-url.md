# Wake-action URL composition

A Windows (and Mac) wake-word action records like any other dictation, then opens the configured URL with the transcribed text in the `input` query item. Other query items stay as written. An existing `input` item is replaced, not duplicated.

## Sub-features

- `WA1` a URL with no query gets `?input=` plus the percent-encoded text.
- `WA2` spaces become `%20`; `&` `+` `#` do not remain raw in the value.
- `WA3` an existing `input=` value is replaced; sibling items such as `k=v` are kept.

## How to get to it (user POV)

- Add a `windows.actions` (or Mac `macOS.actions`) entry with `name`, `wakeWords`, and `url`.
- Speak that wake word, then the message. Superwhisper's result is opened as the URL.
- On this Linux box the same URL is produced by `WakeAction.InputUrl` without speaking.

## Driving it with verify.sh

Preconditions:

- `$V doctor` passes.
- No Grok Bot app and no rundll32 are required.

- **Compose default input.** Run `VS_RUN_ID=<name> $V drive wake-action-url`. The probe loads a progress action whose URL is `alter://action/26B00BAB-5655-4581-BCA0-F3880E746CC9` and asks `InputUrl("hello world")`.
- **Proof.** `exit=0`. `stdout.txt` contains `GOT alter://action/26B00BAB-5655-4581-BCA0-F3880E746CC9?input=hello%20world`, `WANT` the same string, and `MATCH`. `swift-mirror.txt` contains `ok baseline`.
- **Unit test.** `$V tests "wake action URL carries the text as one input item"` prints `PASS wake action URL carries the text as one input item` and no `FAIL`.

## Gotchas

- This recipe proves the default query name `input`. It does not prove `inputParam=text`.
- The alter:// URL is never opened. Matching the string is the proof.
- `url-probe compose` constructs `new WakeAction(name, words, url)` and therefore always uses the constructor default, not a JSON `inputParam`. Use `load` / `expect` for config keys.
