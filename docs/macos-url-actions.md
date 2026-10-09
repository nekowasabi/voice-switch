# macOS URL actions

voice-switch sends a configured URL to its installed handler. It does not register
an incoming `voice-switch://` scheme. No OS settings or URL associations are changed.

Open **設定ファイルを開く** from the menu and add:

```json
"macOS": {
  "wakeURL": "superwhisper://record",
  "stopURL": "superwhisper://record"
}
```

This is a fragment of the existing config object. A complete command-mode example
is [config.example.macos-url.json](../config.example.macos-url.json).
The existing `command` field is still required for configuration compatibility.
Each omitted or null URL uses the corresponding legacy command; `stopCommand`
still defaults to the Superwhisper record toggle. An empty string is an error.
Windows .NET ignores the `macOS` object and continues using its existing commands;
use Windows-compatible `command` / `stopCommand` values when sharing a config.

- `wakeURL` replaces `command` for a standalone wake word **when `dictation` is
  absent**. Remove the `dictation` object to use this command mode. With dictation
  enabled, wake words still start voice-switch's WAV capture/handoff; the URL
  does not override that workflow. The existing macrowhisper preparation hook
  still runs before the wake action.
- `stopURL` replaces `stopCommand` only when a configured `skipWhileMicInUseBy`
  app owns the microphone. A stop word during voice-switch dictation still
  finishes that dictation; when nothing is recording it still does nothing.
- `--fire` sends the wake action directly without microphone capture, even with
  `dictation` configured. It waits for `open` and exits nonzero if URL dispatch
  fails. Legacy shell-command diagnostics retain their previous behavior.
- Edits reload between utterances. Invalid edits are logged and the last valid
  config remains active. Invalid startup config uses the existing error alert.

Other examples: `shortcuts://run-shortcut?name=My%20Shortcut` or
`https://example.com/search?q=hello%20world&lang=ja`. Install the intended handler
first. Supply an absolute URL, percent-encode spaces and query values as needed,
and do not add shell quotes around it. Raw whitespace/control characters,
malformed percent escapes, missing/invalid schemes, and `file:`, `data:`,
`javascript:` URLs are rejected. JSON escaping still applies (`\"`, `\\`).

URLs are passed unchanged as one argument to `/usr/bin/open -g -- URL`, without
`sh`, variable expansion, or command substitution. `&`, quotes, and semicolons
cannot become shell syntax. The target application controls what the URL does;
only configure actions you intend to run. A failed URL never falls back to the
legacy command. `-g` requests background opening, but the target can choose to
activate itself. An `open` success confirms dispatch, not completion of the
application's action. Errors appear in the terminal or **ログを開く**
(`~/Library/Logs/voice-switch.log`), including unregistered-handler diagnostics.

## Verification on your Mac after applying the commit

1. On macOS 26+ with a compatible Xcode/Swift toolchain, run `make build` and
   `make url-test`. The latter requires Swift and tests real config decoding,
   URL argument boundaries, reload retention, and process failure handling
   without launching apps. Optionally run the full parity suite where .NET is
   also installed: `python3 tests/parity/run_parity.py --require-swift`.
2. Copy `config.example.macos-url.json` to a separate test path. With Superwhisper
   installed, run (this intentionally invokes its recording toggle):
   ```sh
   VOICE_SWITCH_CONFIG="$PWD/config.example.macos-url.json" .build/release/voice-switch --fire
   echo $?
   ```
   Confirm recording starts; run again to stop. This first check does not prove
   microphone recognition or voice-switch's dictation handoff works.
3. In the test copy, set `wakeURL` to an unregistered scheme such as
   `voice-switch-unregistered-test://run`. Repeat `--fire`: expect a diagnostic
   and nonzero exit, with no legacy command executed. Set it to `not a URL`:
   expect a config error naming `macOS.wakeURL`, before launching any handler.
4. To test another app, use a known installed scheme, including a query with
   `%20` and `&`. Confirm that all parameters arrive intact. Do not use a URL
   that performs an unwanted destructive action.
5. For the live test, use the menu's config item to apply the command-mode
   settings (back up your existing config first), restart/open your built app,
   and grant its microphone/speech permissions if needed. Say `音声入力` and
   verify Superwhisper records. Say `入力ストップ` while Superwhisper owns the mic
   and verify the stop action. Check the log. Edit a URL to an invalid value
   while listening and confirm the log retains the previous configuration;
   correct it and confirm reload on a later utterance.
6. Restore your original config. If it contains `dictation`, separately test
   wake + body recording, stop/finish, WAV intake, transcription and paste/pane
   routing. This feature's tests do not establish that integration or live
   microphone behavior.

## Cloud evidence and limits

The implementation was prepared on Linux without Swift or .NET SDKs, Apple
frameworks, LaunchServices, microphones, or Superwhisper. No installer, downloaded
executable, or local Mac/Windows session was run.

- Before implementation, `python3 tests/url-actions/run.py` failed with
  `wake must dispatch URL action`. After implementation its static call-chain
  checks pass; it explicitly reports Swift behavior tests as skipped.
- `python3 tests/parity/run_parity.py --static-only`: 96 passed, 20 allowed
  platform differences, zero failures. This includes mutation guards and the
  legacy command fallback contract. Compiled behavior tests are excluded.
- `make win-verify` and `git diff --check` pass.
- Full parity execution fails because `dotnet` is unavailable. `make url-test`
  fails because `swiftc` is unavailable. No Mac build or executed Swift test
  pass is claimed; the commands above are required before relying on runtime
  behavior.

Design followed pstack-codex's Boundary Discipline, TDD, and Prove It Works
skills from https://github.com/ColdTbrew/pstack-codex at commit
`38a2fe89843f271ba436bab9e71c2a31fa943875`. The alternatives considered
were additional shell command examples versus a typed macOS URL override. The
latter keeps the existing command fallback while putting URL validation at
config decoding and argument separation at the process boundary.
