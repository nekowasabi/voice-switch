# Consecutive sessions

After one dictation ends, the app goes back to listening and the next wake word starts a new, independent dictation. Each dictation produces its own handoff.

## Sub-features

- `CS1` two wake-body-stop cycles in one run produce two handoffs with disjoint ranges.

## How to get to it (user POV)

- Dictate, say the stop word, then say the wake word again without restarting the app.

## Driving it with verify.sh

Preconditions: baseline from the index, default config.

- `CS1`: `$V wav consecutive-sessions-clean`. Expect two `body-start` lines, two `standalone-stop` lines, two `handoff ... reason=StandaloneStop` lines, and `handoff_wavs=2`. The second `body-start` range starts after the first `trimExcluded=` range ends.

## Gotchas

- The session `id=` in the log is the recognition request id, not a session counter. Count `body-start` lines instead.
