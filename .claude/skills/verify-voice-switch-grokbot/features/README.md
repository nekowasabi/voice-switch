# Grok Bot wake-action verification map

This directory is the maintained source for verifying that voice-switch would open a Grok Bot prompt URL with the dictated text. Read this index before driving, then use the matching feature file as the recipe.

## Baseline preconditions

- `V=/workspace/voice-switch/.claude/skills/verify-voice-switch-grokbot/verify.sh`
- `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1`
- `$V build` from the working tree you want to prove.
- `$V doctor` passes and its build commit matches `git rev-parse HEAD`.
- Never drive the user's installed tray or `make win-publish`. Never open `grokbot://`.

## Driving conventions

- Every recipe is `VS_RUN_ID=<name> $V drive <feature>`.
- The probe loads a JSON config and prints `WakeAction.InputUrl`. Assert that string.
- Group a proof under one `VS_RUN_ID` so the evidence sits together.
- Swift stays Mac-unverified on this box. After a Windows URL proof, run `$V swift-mirror grokbot` and require exit 0.

## Proof and skip reporting

- Quote `GOT` and `WANT` from `stdout.txt`, plus `exit=`.
- A feature that needs the Grok Bot desktop app, Launch Services, or rundll32 cannot be proven here. Report it as not verified with that reason. Do not report it as verified because the URL string matched.
- Record the config used. It is saved in the evidence folder.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph describing the user-visible behavior. It then uses exactly four H2 sections in this order: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify.sh`, `Gotchas`.

## Features

- [Wake-action URL composition](./wake-action-url.md) covers the existing `input=` query item. Drive this to prove the harness on the current tree.
- [Grok Bot prompt link](./grokbot-prompt-link.md) covers `inputParam=text` on `grokbot://app/v1/prompt`.
- [Grok Bot text limits](./grokbot-text-limits.md) covers newline collapse and the 1000-character encoded cap.
- [Automatic sessions never open action URLs](./automatic-no-action-url.md) covers the wake-acceptance safety gate for the new action.
- [Example config and docs](./grokbot-config-docs.md) covers `config.example.grokbot.json` and the setup docs.
