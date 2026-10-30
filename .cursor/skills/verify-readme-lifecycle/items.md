# README and lifecycle verification items

Acceptance record for the human README and the Mac and Windows start, stop, and restart commands. Each id is one check. `verify.sh drive` prints `PASS <id>`, `FAIL <id>`, or `SKIP <id>`.

`live-mac-handtest` and `live-win-handtest` are always `SKIP` on this skill. Installing the app and reading a live log are human work. A Linux run must not mark them passed.

The strings below are copied from the tree at `cf89a2f` unless the item says the change must add them.

## Items

- id: readme-ja-primary
  spec: README_ja.md が日常の使い方の主である。README.md は同じ手順を英語で持つ。
  pass: README_ja.md contains `このファイルが日常の使い方の主である。` README.md contains `README_ja.md is the primary guide for daily use.` and the existing link `[日本語](README_ja.md)`.

- id: readme-daily-use
  spec: Mac と Windows の常用を書く。文字起こしは Superwhisper を前提にする。
  pass: README_ja.md has a heading `## 日常の使い方` and the sentences `常用の文字起こしは Superwhisper を前提にする。` and `Mac はメニューバー、Windows はトレイで、待ち受けたまま使う。` README.md has a heading `## Daily use` and the sentences `Daily dictation assumes Superwhisper.` and `On a Mac it is the menu-bar app. On Windows it is the tray app. Leave it running.`

- id: readme-config-paths
  spec: 設定ファイルの場所を OS ごとに書く。コードの解決順と一致させる。
  pass: Both READMEs contain `~/.config/voice-switch/config.json` and `VOICE_SWITCH_CONFIG`. README_ja.md contains `Windows は、exe と同じフォルダに config.json があるときはそれを使う。無いときは %USERPROFILE%\.config\voice-switch\config.json を使う。` README.md contains `On Windows, voice-switch.exe uses config.json beside the exe when that file exists. Otherwise it uses %USERPROFILE%\.config\voice-switch\config.json.` Those two sentences match `WindowsPaths.DefaultConfigPath` and `TrayOptions.Parse` (`--config`, then `VOICE_SWITCH_CONFIG`, then the default).

- id: readme-perm-mac
  spec: Mac の許可は、ソースが実際に要求するものだけを書く。
  pass: Both READMEs contain `NSMicrophoneUsageDescription`, `NSSpeechRecognitionUsageDescription`, `マイクへのアクセスが許可されていません`, `hotkey interception disabled: Accessibility is not granted`, and `hotkey finish unavailable: grant Accessibility`. README_ja.md contains `iTerm と System Events へのオートメーションは、ソースが osascript も Apple Event も送らないので、求めない。` README.md contains `The app does not request Automation permission for iTerm or System Events. The source sends neither osascript nor Apple Events.` Neither README contains `hotkey: Accessibility not granted yet`. Info.plist still has both usage-description keys. MacApp.swift still has `AVCaptureDevice.requestAccess` and both Accessibility log lines. The app source has no `System Events` and no `iTerm` string.

- id: readme-perm-windows
  spec: Windows 側の許可を、コードがダイアログを出さない事実と UIPI の制限で書く。
  pass: README_ja.md contains `Windows はマイクのプライバシー設定を使う。このコードはアクセシビリティやオートメーションの許可ダイアログを出さない。` and `昇格したウィンドウへのキーは UIPI でフックを通らない。` README.md contains `Windows uses the microphone privacy setting. This code shows no Accessibility or Automation consent dialog.` and `UIPI`.

- id: readme-handtest-logs
  spec: 手試験で読むログ行は、Swift と C# にある文字列だけを書く。
  pass: Both READMEs contain each of these exact strings, and the named source file still contains it. `tmux:` in `Sources/voice-switch/PaneRoute.swift` and `dotnet/VoiceSwitch.Windows/TmuxPaneRouter.cs`. `jev=` in `Sources/voice-switch/PaneRoute.swift` and `dotnet/VoiceSwitch.Windows.Core/PaneRoute.cs`. `jev:` and `jev body:` in `Sources/voice-switch/PaneRoute.swift`. `heard:` in `Sources/voice-switch/MacApp.swift`. `dictation session:` and `dictation phase:` in `dotnet/VoiceSwitch.Windows/DictationRuntime.cs`. `superwhisper handoff:` and `hotkey interception disabled: Accessibility is not granted` and `hotkey finish unavailable: grant Accessibility` in `Sources/voice-switch/MacApp.swift`. `phase:` in `Sources/voice-switch/main.swift`. `fatal:` in `Sources/voice-switch/main.swift`. `downloading speech model for` in `Sources/voice-switch/MacApp.swift`.

- id: readme-log-locations
  spec: ログの場所と、Mac と Windows で手試験をどう始めるかを書く。
  pass: README_ja.md has a heading `## 手試験` and the sentences `Mac の手試験は、アプリを起動してウェイクワードを話し、make logs で ~/Library/Logs/voice-switch.log を読む。` and `Windows の手試験は、トレイを起動してウェイクワードを話し、%LOCALAPPDATA%\voice-switch\voice-switch.log を読む。` README.md has a heading `## Hand tests` and the sentences `A Mac hand test starts the app, speaks a wake word, and reads ~/Library/Logs/voice-switch.log with make logs.` and `A Windows hand test starts the tray, speaks a wake word, and reads %LOCALAPPDATA%\voice-switch\voice-switch.log.` The Makefile `logs` target still tails `voice-switch.log`. `Platform.logURL` still ends at `Library/Logs/voice-switch.log`. `WindowsPaths.DefaultLogPath` still ends at `voice-switch.log`.

- id: readme-human-owned
  spec: インストールと手試験は人が行う。ボットは行わない。
  pass: README_ja.md contains `インストールと、Mac および Windows の手試験は、人が行う。ボットは行わない。` README.md contains `The human installs the app and runs the Mac and Windows hand tests. The bot does not.`

- id: win-stop-alias
  spec: `--tray-command stop` は quit の別名である。パイプのコマンドは増やさない。
  pass: `TrayOptions.TryParseCommand` maps both `stop` and `quit` to `TrayCommand.Quit`. The error text lists `stop`. `TrayCommand` in `TrayContracts.cs` has no `Restart` member. `TrayIpc` still accepts only `ShowStatus`, `Start`, `Pause`, `Reload`, and `Quit` on the pipe.

- id: win-restart-process
  spec: `--tray-command restart` は動いているトレイに quit を送り、プロセスが終わってから同じ exe を起動し直す。reload は使わない。
  pass: `TrayOptions` exposes a restart flag that is true only for the word `restart`, and the IPC command for that word is null. `TrayHost` sends `TrayCommand.Quit` on the existing pipe, then starts this exe with `ProcessStartInfo` and no `--tray-command` argument. `ReloadAsync` is still the config reload. The word `restart` does not appear as a `TrayCommand` wire name.

- id: win-help-semantics
  spec: start と pause は待ち受けのオンとオフである。stop と restart はプロセスの停止と再起動である。ヘルプと README の両方に書く。
  pass: `Program.PrintHelp` contains `stop is an alias of quit`, `restart sends quit to the running tray, then launches this exe again`, `start and pause turn listening on and off`, and `reload re-reads config in the same process`. README_ja.md contains `` `--tray-command start` と `pause` は待ち受けのオンとオフである。プロセスの起動と終了ではない。 `` and `` `--tray-command stop` は `quit` の別名である。 `` and `` `--tray-command restart` は動いているトレイに quit を送り、終わってから同じ exe を起動し直す。reload は同じプロセスで設定を読み直す。プロセスの再起動ではない。 `` README.md contains `` `--tray-command start` and `pause` turn listening on and off. They do not start or stop the process. `` and `` `--tray-command stop` is an alias of `quit`. `` and `` `--tray-command restart` sends quit to the running tray, then launches the same exe after that process exits. reload re-reads config in the same process. It is not a process restart. ``

- id: win-no-second-ipc
  spec: 二本目の IPC を足さない。
  pass: The tree has one `class TrayIpcServer`. No file under `dotnet/VoiceSwitch.Windows` declares a second pipe server class. The `"voice-switch-tray-"` pipe prefix is still assigned only in `TraySingleInstance.cs`. A test temp directory that happens to contain the same letters does not count.

- id: win-make-lifecycle
  spec: make win-start、既存の win-stop、make win-restart を足す。win-start はプロセスの起動であり、`--tray-command start` ではない。
  pass: Makefile has `win-start` launching `$(RELEASE_DIR)/voice-switch.exe` through `explorer.exe`, the same launch `win-publish` uses, and that recipe does not contain `--tray-command`. `win-stop` still runs `Stop-Process`. `win-restart` depends on `win-stop` and `win-start`. All three targets are `.PHONY` and have `##` help text. README_ja.md contains `` `make win-stop` は動いている voice-switch.exe を止める。 `` and `` `make win-start` は RELEASE_DIR の voice-switch.exe を起動する。`--tray-command start` ではない。 `` and `` `make win-restart` は win-stop のあと win-start する。 `` README.md contains `` `make win-stop` stops the running voice-switch.exe. `` and `` `make win-start` launches voice-switch.exe from RELEASE_DIR. It does not pass `--tray-command start`. `` and `` `make win-restart` runs win-stop, then win-start. ``

- id: win-parse-tests
  spec: stop と restart の引数解釈を、make win-test が実行するテストに足す。
  pass: `dotnet/VoiceSwitch.Windows.Tests` registers two tests whose names contain `stop aliases quit` and `restart is not reload`. The stop test parses `--tray-command` `stop` and `--tray-command` `quit` and requires both IPC commands to be `TrayCommand.Quit` with restart false. The restart test parses `--tray-command` `restart`, requires the restart flag, requires a null IPC command, and requires `--tray-command` `reload` to stay `TrayCommand.Reload`. The test project already compiles `TrayOptions.cs`. A test file that `VoiceSwitch.Windows.Tests.csproj` does not compile does not pass.

- id: mac-make-lifecycle
  spec: make start、make stop、make restart を足す。stop は install と同じ osascript。start はインストール済みアプリを open する。restart は stop のあと短く待つ。
  pass: `stop` runs `osascript` with `quit app id "$(ID)"`. `start` runs `open` on `$(DEST)/$(APP)`. `restart` depends on `stop`, waits with `pgrep` and `sleep` the way `install` does, then opens the app. The three targets are `.PHONY` and have `##` help text. No recipe calls `swift build`.

- id: mac-readme-lifecycle
  spec: Mac の start、stop、restart を README_ja.md と README.md に書く。
  pass: README_ja.md contains `` `make stop` は `osascript` で `quit app id "local.voice-switch"` する。 `` and `` `make start` はインストール済みの VoiceSwitch.app を open する。 `` and `` `make restart` は stop のあと、プロセスが終わるまで短く待って start する。 `` README.md contains `` `make stop` runs osascript `quit app id "local.voice-switch"`. `` and `` `make start` opens the installed VoiceSwitch.app. `` and `` `make restart` stops, waits until the process exits, then starts. ``

- id: mac-cli-optional
  spec: Sources にライフサイクル用の CLI フラグを足すのは任意である。足した場合だけ README に書く。
  pass: If `Sources/voice-switch/main.swift` contains `--restart`, both READMEs contain `--restart` and name it as quit-then-open. If the flag is absent, the item passes. Do not add the flag only to satisfy this item.

- id: scope-pane-proof
  spec: scripts/pane_route_proof.py は今回の差分に含めない。
  pass: `git diff --quiet origin/main -- scripts/pane_route_proof.py` exits 0.

- id: scope-no-sw0
  spec: .claude/sw0.md と .claude/sw0b.md はコミットしない。
  pass: `git ls-files` lists neither path.

- id: live-mac-handtest
  spec: Mac 実機でのインストールと手試験。
  pass: A human on a Mac runs `make install` or `make start`, speaks a wake word with Superwhisper, and reads `make logs`. This skill prints `SKIP live-mac-handtest`.

- id: live-win-handtest
  spec: Windows 実機でのインストールと手試験。
  pass: A human on Windows launches the tray, speaks a wake word with Superwhisper, and reads `%LOCALAPPDATA%\voice-switch\voice-switch.log`. This skill prints `SKIP live-win-handtest`.
