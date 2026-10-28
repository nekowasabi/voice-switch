# PLAN: Grok Bot send-message

The intended design matches the code. Mac `WakeAction.openArguments(input:)` and Windows `WakeAction.InputUrl` both replace or append a query item named `input`. Automatic sessions never set `Action` (`StartAutomatic`) and the handoff returns before opening a URL when `controlled` / `origin.isAutomatic` is true (`if controlled { return .delivered }` / `if (controlled) return …` then `LaunchUrl`). `--fire` runs `command` (Windows) or `wakeURL` (Mac); it does not compose a named action or fill `text=`.

One slice. No second branch.

## Slice 1 — Grok Bot prompt via `inputParam` + prompt-link limits

- **branch:** `feat/grokbot-send-message`
- **goal:** A grokbot wake-word action opens `grokbot://app/v1/prompt?id=<BOT_ID>&text=<encoded>` on Windows and Mac. Optional `inputParam` (default `input`) selects the query name. Grok Bot prompt URLs (scheme `grokbot`, host `app`, path `/v1/prompt`) collapse CR/LF/tab runs to one space, trim, and cap the percent-encoded value at 1000 characters on a whole UTF-8 character / whole `%XX`. Automatic sessions still never open action URLs. Example config, docs, README, and parity contracts land in the same slice.
- **files:**
  - `dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs` — `WakeAction.InputParam`, load-time unreserved-name check, `InputUrl` uses that name. Keep omitted/`null` behaving as `"input"` so existing URLs stay byte-identical.
  - `dotnet/VoiceSwitch.Windows.Core/GrokBotPrompt.cs` (new, Core SDK include; no Tests csproj entry) — type name **`GrokBotPrompt`** (SPEC 15 greps that identifier), `IsPrompt(url)`, `Fit(text) -> (string Fitted, bool Truncated)`. `InputUrl` calls `Fit` when `IsPrompt`. Mirror the same type name and logic in Swift in `Config.swift` next to `openArguments`.
  - `dotnet/VoiceSwitch.Windows/SuperwhisperHandoff.cs` — if `Fit` truncated, `Log.Info` a line containing the phrase `grokbot prompt text truncated` (and the action name) before `LaunchUrl`. A bare `truncated` does not satisfy SPEC 15.
  - `Sources/voice-switch/Config.swift` — `inputParam` on `WakeAction` (default `"input"`), same validation in `init(from:)`, `openArguments` filters/appends that name, same `Fit` for grokbot prompt URLs. Mirror the C# logic line for line.
  - `Sources/voice-switch/MacApp.swift` — if truncated, `log` a line containing the phrase `grokbot prompt text truncated` before `openArguments`. Keep `if controlled { return .delivered }` before the action URL. Do not "fix" the pre-existing missing `guard modeRequested, superwhisperAutoPasteDisabled` string; `tests/url-actions/run.py` stays at its origin/main failure.
  - `dotnet/VoiceSwitch.Windows.Tests/Program.cs` (and `NoWakeTests.cs` if the automatic case fits there) — tests whose **names are exactly** SPEC items 4–9. Existing `wake action URL carries the text as one input item` stays.
  - `tests/parity/contracts/platform_parity.json` — add `wake_action_input_param` and `grokbot_prompt_text_limits`. Do not weaken existing markers.
  - `config.example.grokbot.json` — top-level `wakeWords` + `command` so `ConfigLoader.Validate` accepts it; both `macOS.actions` and `windows.actions` with `name: grokbot`, wake words `グロック` / `grok bot`, `url: grokbot://app/v1/prompt?id=YOUR_BOT_ID`, `inputParam: text`. No real bot id.
  - `docs/grokbot-message.md` — Win+Mac setup, how to copy BOT_ID, limits as **documented, not live-tested** (staff forum 2026-10-16), `--fire` vs speaking, Mac-unverified, webhooks as a future option only.
  - `README.md`, `README_ja.md` — short section pointing at the example and the doc.
  - `docs/wake-acceptance.md` — keep `invoke an action URL`; mention that grokbot is an action URL and follows the same gate.
- **acceptance:** SPEC 1–23. Item 24 is an environment note, not a product check.
- **risk:** Mac cannot be compiled here (`swiftc` missing; no Swift formatter). A Swift typo ships until a Mac build. Mitigate by mirroring C# line for line and by SPEC 15–16 greps. Truncation must not split `%XX` or a multi-byte UTF-8 sequence. Default `inputParam` must not change existing `input=` bytes. Do not invent secrets. Do not merge, push, or open a PR. Leave `.claude/sw0.md` and `.claude/sw0b.md` untracked.
- **effort:** #high

### Implementation notes

- `inputParam` valid values: non-empty RFC 3986 unreserved query names. Validate at config load on both OS.
- Grok Bot limits apply only when the action URL is a prompt link, not to every action, and not because `inputParam` is `text`.
- `Fit` is pure in Core / Config.swift. Logging stays at the handoff (`Log.Info` / Mac `log`) because Core has no `Log`. Calling `Fit` twice is idempotent.
- New files under `dotnet/VoiceSwitch.Windows/` need `<Compile Include>` in `VoiceSwitch.Windows.Tests.csproj`. New files under Core or Tests do not.
- `dotnet format --verify-no-changes --include` only the touched product `.cs` files (SPEC 13).
- `--fire` docs: Windows `--fire` runs `config.command`; Mac `--fire` runs `macOS.wakeURL`. To see that `grokbot://` is registered, point that field at the prompt URL (composer stays empty). Filling `text=` requires speaking the grokbot wake word. This Linux box cannot launch that URL.
- Webhooks with a bearer key are out of scope; one sentence in the doc as a future option.

## Verification skill (already on this branch)

`.claude/skills/verify-voice-switch-grokbot/` drives `WakeAction.InputUrl` through `url-probe` (`dotnet exec`). Proven on this box: `$V build`, `$V doctor`, `VS_RUN_ID=skill-proof-3 $V drive wake-action-url` produced `MATCH` on `input=hello%20world`; `$V cleanup` removed `/tmp/voice-switch-grokbot-verify/build` and left `/tmp/voice-switch-grokbot-verify/evidence/skill-proof-3/wake-action-url/`. Swiftc is absent. The grokbot recipes (SPEC 14, 20–22) are expected red until this slice.
