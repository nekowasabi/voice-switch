# Automatic sessions never open action URLs

An automatic (no-wake) recording stays local. It must not open a Grok Bot prompt link or any other action URL. Only an explicit wake-word action opens a URL. `docs/wake-acceptance.md` states this safety gate.

## Sub-features

- `AS1` `StartAutomatic` finishes with `Action == null` even when `windows.actions` lists grokbot.
- `AS2` a handoff whose origin is automatic and whose `Action` is the grokbot action still does not call rundll32 / `LaunchUrl`.
- `AS3` a later strict wake does not upgrade the automatic session into that action.

## How to get to it (user POV)

- Leave `dictation.noWakeWord.autoIdle` on. Speak without a wake word. Nothing should open Grok Bot.
- Speak `グロック` then a message. That explicit wake is the path that may open Grok Bot.

## Driving it with verify.sh

Preconditions:

- `$V doctor` passes.
- The named unit test exists. Until it is added, this recipe fails.

- **Named test.** Run `VS_RUN_ID=<name> $V drive automatic-no-action-url`.
- **Proof.** `exit=0`. `stdout.txt` contains `PASS automatic session never opens an action URL` and no `FAIL`.
- **Docs.** `docs/wake-acceptance.md` still contains `invoke an action URL`.

## Gotchas

- `DictationHandoffSwitchesModeOnlyForAutomaticAudio` proves automatic audio is not pasted. It does not attach a grokbot `Action`. Drive the named test, not that neighbor.
- Production `StartAutomatic` never sets `Action`; the handoff also returns before `LaunchUrl` when `controlled` is true. The test must cover both.
- Stop-word ending of an automatic session pastes like a wake-word dictation (existing exception in README). That path still must not open an action URL.
