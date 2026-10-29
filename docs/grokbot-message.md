# Grok Bot message via wake-word action (documented, not live-tested)

The Grok Bot prompt-link contract below is documented, not live-tested
(staff forum 2026-10-16). No Grok Bot desktop app, Launch Services, or
rundll32 is available on the Linux verification box, so the URL composition
is proven through `WakeAction.InputUrl` and source greps only. Mac-unverified.

## What it does

Speaking the grokbot wake word records a message and opens a Grok Bot
prompt link with the dictation attached:

```
grokbot://app/v1/prompt?id=YOUR_BOT_ID&text=<encoded message>
```

Copy the Bot id from the Grok Bot app (the Bot menu "Copy link" ends in the
id) into `config.example.grokbot.json` in place of `YOUR_BOT_ID`. Never
commit a real Bot id or token; the example keeps the placeholder.

Prompt links never auto-send. The app types the message into that Bot's
composer and the user presses send. The composer must have been an empty
composer already; a non-empty composer is left alone. Prompt links need a
Grok Bot build that includes prompt links.

## Setup

Windows: add the `windows.actions` entry from `config.example.grokbot.json`
(`name: grokbot`, wake words `グロック` / `grok bot`,
`url: grokbot://app/v1/prompt?id=YOUR_BOT_ID`, `inputParam: text`).
The action's dictation runs in a no-auto-paste Superwhisper mode so the text
reaches only the URL.

Mac: add the same entry under `macOS.actions`. The `open` arguments carry
the same `text=` item.

Optional `inputParam` (default `input`) selects the query name. Grok Bot
prompt links expect `text`. Other action URLs keep `input`.

## Limits

Prompt links keep line breaks (CR LF and CR become LF, sent as `%0A`), turn each tab run into one space, trim the ends, and
cap the percent-encoded value at 1000 characters, cut only at a whole UTF-8
character and never inside a `%XX` escape. Longer dictations are truncated
and the handoff logs `grokbot prompt text truncated`. These limits apply
only to prompt links (`grokbot://app/v1/prompt`), not to other action URLs.

## `--fire` vs speaking

`--fire` runs the default `command` (Windows) or `macOS.wakeURL` (Mac). It
does not compose a named action and does not fill `text=`. Speaking the
grokbot wake word is the path that fills the composer. To see that
`grokbot://` is registered, a user may point `command` / `wakeURL` at the
prompt URL, but that only opens an empty composer: `--fire` does not fill
the text either.

## Webhooks

Routine delivery through a server webhook with a bearer key is a future option
only. It is out of scope for this slice.
