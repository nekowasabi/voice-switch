# Silence produces nothing

When nobody speaks, the app keeps listening and never starts a dictation or hands anything off.

## Sub-features

- `NS1` 2.5 s of silence produces no session and no handoff.

## How to get to it (user POV)

- Leave the tray app listening in a quiet room.

## Driving it with verify.sh

Preconditions: baseline from the index, default config.

- `NS1`: `$V wav intentional-silence-clean`. Expect `exit=0`, `handoff_wavs=0`, and no `dictation session:` line. Repeat with `intentional-silence-silence-noise2` for low noise.

## Gotchas

- With `noWakeWord.autoIdle` on, noise above the VAD threshold can start an automatic session. Use the default config for `NS1`.
