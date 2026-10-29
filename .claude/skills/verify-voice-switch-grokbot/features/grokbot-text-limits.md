# Grok Bot text limits

When the action URL is a Grok Bot prompt link (scheme `grokbot`, host `app`, path `/v1/prompt`), the dictated text is forced to the documented prompt-link limits before it is written into the query: CR LF and CR become LF so line breaks reach the composer, tab runs become one space, and the string is trimmed; the percent-encoded value is at most 1000 characters, cut only at a whole UTF-8 character, never inside `%XX`. Truncation is logged. Other action URLs are not capped this way. Limits are documented, not live-tested.

## Sub-features

- `TL1` line breaks are kept as LF (`%0A`; CR LF and CR become LF), tab runs become one space, then the ends are trimmed.
- `TL2` a percent-encoded value longer than 1000 is cut to <= 1000 at a character boundary.
- `TL3` a cut never lands inside `%XX` or a multi-byte UTF-8 sequence such as `%E4%BB%8A` (今).
- `TL4` the handoff log contains the phrase `grokbot prompt text truncated` when a cut happened.

## How to get to it (user POV)

- Speak a Grok Bot wake word and a long or multi-line message.
- On this Linux box, pass that text to `InputUrl` for a grokbot prompt URL.

## Driving it with verify.sh

Preconditions:

- `$V doctor` passes.
- `inputParam=text` is implemented. Until then this recipe fails because the query name is still `input`.

- **Line breaks and cap.** Run `VS_RUN_ID=<name> $V drive grokbot-text-limits`.
- **Proof.** `exit=0`. `hello\r\nworld\t\tnow\n` produces `...&text=hello%0Aworld%20now`. A string of 400 × `今` on a grokbot prompt URL produces a `text=` value with length <= 1000, length a multiple of 9 (`%E4%BB%8A`), and every `%` followed by two hex digits. The same 400 × `今` on a non-grokbot action URL produces an encoded value longer than 1000.
- **Unit tests.** `$V tests "grokbot prompt keeps line breaks as LF, folds tabs, and trims"` and `$V tests "grokbot prompt caps percent-encoded text at 1000 without splitting %XX or UTF-8"` each print their `PASS` line.
- **Log marker.** `$V swift-mirror grokbot` includes `grokbot prompt text truncated` in `MacApp.swift` and `SuperwhisperHandoff.cs`.

## Gotchas

- The 1000 cap is on the percent-encoded value, not the raw rune count. `今` is 9 encoded characters.
- Regular spaces and line breaks are not collapsed; only tab runs become one space, then the whole string is trimmed. Blank lines stay.
- Non-grokbot action URLs must keep today's uncapped `input=` behavior. Do not apply the cap globally.
