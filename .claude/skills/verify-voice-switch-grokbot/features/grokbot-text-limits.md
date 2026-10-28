# Grok Bot text limits

When the action URL is a Grok Bot prompt link (scheme `grokbot`, host `app`, path `/v1/prompt`), the dictated text is forced to the documented prompt-link limits before it is written into the query: CR/LF/tab runs become one space and the string is trimmed; the percent-encoded value is at most 1000 characters, cut only at a whole UTF-8 character, never inside `%XX`. Truncation is logged. Other action URLs are not capped this way. Limits are documented, not live-tested.

## Sub-features

- `TL1` CR, LF, and tab runs collapse to one space, then trim, so the value is one line.
- `TL2` a percent-encoded value longer than 1000 is cut to <= 1000 at a character boundary.
- `TL3` a cut never lands inside `%XX` or a multi-byte UTF-8 sequence such as `%E4%BB%8A` (今).
- `TL4` the handoff log contains `truncated` when a cut happened.

## How to get to it (user POV)

- Speak a Grok Bot wake word and a long or multi-line message.
- On this Linux box, pass that text to `InputUrl` for a grokbot prompt URL.

## Driving it with verify.sh

Preconditions:

- `$V doctor` passes.
- `inputParam=text` is implemented. Until then this recipe fails because the query name is still `input`.

- **Collapse and cap.** Run `VS_RUN_ID=<name> $V drive grokbot-text-limits`.
- **Proof.** `exit=0`. Newlines `hello\r\n\tworld\n` produce `...&text=hello%20world`. A string of 400 × `今` produces a `text=` value with length <= 1000, length a multiple of 9 (`%E4%BB%8A`), and every `%` followed by two hex digits.
- **Unit tests.** `$V tests "grokbot prompt collapses CR LF tab runs to one space and trims"` and `$V tests "grokbot prompt caps percent-encoded text at 1000 without splitting %XX or UTF-8"` each print their `PASS` line.
- **Log marker.** `$V swift-mirror grokbot` includes `truncated` in `MacApp.swift` and `SuperwhisperHandoff.cs`.

## Gotchas

- The 1000 cap is on the percent-encoded value, not the raw rune count. `今` is 9 encoded characters.
- Regular spaces are not collapsed; only CR/LF/tab runs become one space, then the whole string is trimmed.
- Non-grokbot action URLs must keep today's uncapped `input=` behavior. Do not apply the cap globally.
