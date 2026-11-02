# Automatic safety

Automatic safety runs three existing Windows tests. They check that an automatic session does not paste or open an action URL, that manual paste still requires every safety observation, and that automatic delivery does not keep a wake body's text as history.

## Sub-features

- `no-action-url` is the test `automatic session never opens an action URL`.
- `manual-paste-gates` is the test `manual paste requires every safety observation`.
- `delivery-history` is the full test name `automatic delivery tries Superwhisper first and keeps only history; wake delivery skips history`. The drive passes that whole string to `--only` and requires that exact `PASS` line. A shorter prefix is not this check.

## How to get to it (user POV)

- Run `verify.sh drive safety`.
- The same checks exist inside the full `dotnet run` of `VoiceSwitch.Windows.Tests`. This drive runs only these three names.

## Driving it with verify-wake

Preconditions:

- `verify.sh doctor` prints `doctor: ok`.
- `dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj` has been restored.
- No second test host is required. These three runs are sequential.

- **No action URL.** The drive runs `dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "automatic session never opens an action URL"`. Evidence stdout contains `PASS automatic session never opens an action URL`. Exit of that process is 0.
- **Manual paste.** The next process uses `--only "manual paste requires every safety observation"`. Evidence stdout contains `PASS manual paste requires every safety observation`.
- **Delivery.** The next process uses `--only "automatic delivery tries Superwhisper first and keeps only history; wake delivery skips history"`. Evidence stdout contains that string as the entire `PASS` line, not a prefix of a longer name.
- **All three.** `verify.sh drive safety` exits 0 only when all three PASS lines were captured. Run `VS_RUN_ID=safety verify.sh drive safety`.

## Gotchas

- `--only` with a name that matches nothing exits 0 and prints no PASS line. That is a failed drive. Match on the PASS line, not on the exit code alone.
- PASS is on stdout. FAIL is on stderr. Read both files in the evidence folder.
- These tests do not open a browser. A green run means the assertions in those tests held, including `session.Action` staying null for the automatic session that has a grokbot action configured.
- The full suite's baseline on this branch is 238 PASS, 0 FAIL, and 11 SKIP. This drive does not replace that suite.
- Tray HUD, shortcuts, and log redaction are not part of this feature. Do not "fix" whitespace in `DictationHud.cs` while driving it.
