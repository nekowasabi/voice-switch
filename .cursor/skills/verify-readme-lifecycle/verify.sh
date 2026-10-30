#!/usr/bin/env bash
# Read-only checks for the README and lifecycle acceptance items. See SKILL.md.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SKILL_DIR="$(cd "$(dirname "$0")" && pwd)"
BASE="${README_LIFECYCLE_VERIFY_BASE:-/tmp/voice-switch-readme-lifecycle-verify}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
if [[ -x "$DOTNET_ROOT/dotnet" ]]; then
  export PATH="$DOTNET_ROOT:$PATH"
fi

die() { printf 'verify: fail: %s\n' "$1" >&2; exit 1; }

item_ids() {
  sed -n 's/^- id: \([a-z0-9-]*\)$/\1/p' "$SKILL_DIR/items.md"
}

feature_ids() {
  local feature="$1"
  local file="$SKILL_DIR/features/${feature}.md"
  [[ -f "$file" ]] || die "unknown feature ${feature}"
  awk '/^## Sub-features$/{f=1;next} /^## /{f=0} f' "$file" \
    | sed -n 's/^- `\([a-z0-9-]*\)`.*/\1/p'
}

check_item_map() {
  local items="$SKILL_DIR/items.md"
  local id file base
  local -A known=() listed=()
  while IFS= read -r id; do
    [[ -n "$id" ]] || continue
    known["$id"]=1
  done < <(item_ids)
  [[ ${#known[@]} -gt 0 ]] || die "items.md listed no ids"
  local expected actual
  expected=$'## Sub-features\n## How to get to it (user POV)\n## Driving it with verify-readme-lifecycle\n## Gotchas'
  for file in "$SKILL_DIR"/features/*.md; do
    base="$(basename "$file" .md)"
    [[ "$base" == "README" ]] && continue
    actual="$(grep '^## ' "$file")"
    [[ "$actual" == "$expected" ]] || die "feature ${base}.md H2 headings drifted"
    while IFS= read -r id; do
      [[ -n "$id" ]] || continue
      [[ -n "${known[$id]+x}" ]] || die "feature ${base} lists ${id} which is not in items.md"
      listed["$id"]=1
    done < <(feature_ids "$base")
  done
  for id in "${!known[@]}"; do
    [[ -n "${listed[$id]+x}" ]] || die "items.md id ${id} is not in any feature file"
  done
}

doctor() {
  check_item_map
  command -v dotnet >/dev/null 2>&1 || die "dotnet not on PATH and not at $DOTNET_ROOT"
  dotnet --list-sdks | grep -E '^8\.' >/dev/null || die "no .NET 8 SDK (dotnet --list-sdks)"
  local need
  for need in \
    README_ja.md \
    README.md \
    Makefile \
    Info.plist \
    dotnet/VoiceSwitch.Windows/Tray/TrayOptions.cs \
    dotnet/VoiceSwitch.Windows/Tray/TrayIpc.cs \
    dotnet/VoiceSwitch.Windows/Tray/TrayContracts.cs \
    Sources/voice-switch/MacApp.swift \
    Sources/voice-switch/PaneRoute.swift
  do
    [[ -f "$ROOT/$need" ]] || die "missing $need"
  done
  [[ -x "$SKILL_DIR/verify.sh" ]] || die "verify.sh is not executable"
  printf 'doctor: ready\n'
  printf 'doctor: root %s\n' "$ROOT"
  printf 'doctor: dotnet %s\n' "$(dotnet --version)"
  printf 'doctor: host %s\n' "$(uname -s)"
}

has() {
  grep -F -q -- "$2" "$1"
}

recipe_of() {
  local target="$1"
  awk -v t="$target" '
    $0 ~ "^" t ":" { on = 1; next }
    on && /^[^ \t]/ { exit }
    on { print }
  ' "$ROOT/Makefile"
}

target_line() {
  grep -E "^${1}:" "$ROOT/Makefile" || true
}

MISS=""
note_miss() { MISS+="${MISS:+$'\n'}  $1"; }

require_file() {
  local file="$1" text="$2"
  if ! has "$file" "$text"; then
    note_miss "missing in ${file#"$ROOT"/}: ${text}"
  fi
}

require_both() {
  require_file "$ROOT/README_ja.md" "$1"
  require_file "$ROOT/README.md" "$1"
}

forbid_file() {
  local file="$1" text="$2"
  if has "$file" "$text"; then
    note_miss "forbidden in ${file#"$ROOT"/}: ${text}"
  fi
}

finish_item() {
  local id="$1"
  if [[ -z "$MISS" ]]; then
    printf 'PASS %s\n' "$id"
    return 0
  fi
  printf 'FAIL %s\n%s\n' "$id" "$MISS"
  return 1
}

check_readme_ja_primary() {
  MISS=""
  require_file "$ROOT/README_ja.md" "このファイルが日常の使い方の主である。"
  require_file "$ROOT/README.md" "README_ja.md is the primary guide for daily use."
  require_file "$ROOT/README.md" "[日本語](README_ja.md)"
  finish_item readme-ja-primary
}

check_readme_daily_use() {
  MISS=""
  require_file "$ROOT/README_ja.md" "## 日常の使い方"
  require_file "$ROOT/README_ja.md" "常用の文字起こしは Superwhisper を前提にする。"
  require_file "$ROOT/README_ja.md" "Mac はメニューバー、Windows はトレイで、待ち受けたまま使う。"
  require_file "$ROOT/README.md" "## Daily use"
  require_file "$ROOT/README.md" "Daily dictation assumes Superwhisper."
  require_file "$ROOT/README.md" "On a Mac it is the menu-bar app. On Windows it is the tray app. Leave it running."
  finish_item readme-daily-use
}

check_readme_config_paths() {
  MISS=""
  require_both "~/.config/voice-switch/config.json"
  require_both "VOICE_SWITCH_CONFIG"
  require_file "$ROOT/README_ja.md" 'Windows は、exe と同じフォルダに config.json があるときはそれを使う。無いときは %USERPROFILE%\.config\voice-switch\config.json を使う。'
  require_file "$ROOT/README.md" 'On Windows, voice-switch.exe uses config.json beside the exe when that file exists. Otherwise it uses %USERPROFILE%\.config\voice-switch\config.json.'
  require_file "$ROOT/dotnet/VoiceSwitch.Windows.Core/WindowsPaths.cs" 'config.json'
  require_file "$ROOT/dotnet/VoiceSwitch.Windows.Core/WindowsPaths.cs" '.config\voice-switch\config.json'
  finish_item readme-config-paths
}

check_readme_perm_mac() {
  MISS=""
  require_both "NSMicrophoneUsageDescription"
  require_both "NSSpeechRecognitionUsageDescription"
  require_both "マイクへのアクセスが許可されていません"
  require_both "hotkey interception disabled: Accessibility is not granted"
  require_both "hotkey finish unavailable: grant Accessibility"
  require_file "$ROOT/README_ja.md" "iTerm と System Events へのオートメーションは、ソースが osascript も Apple Event も送らないので、求めない。"
  require_file "$ROOT/README.md" "The app does not request Automation permission for iTerm or System Events. The source sends neither osascript nor Apple Events."
  forbid_file "$ROOT/README_ja.md" "hotkey: Accessibility not granted yet"
  forbid_file "$ROOT/README.md" "hotkey: Accessibility not granted yet"
  require_file "$ROOT/Info.plist" "NSMicrophoneUsageDescription"
  require_file "$ROOT/Info.plist" "NSSpeechRecognitionUsageDescription"
  require_file "$ROOT/Sources/voice-switch/MacApp.swift" "AVCaptureDevice.requestAccess"
  require_file "$ROOT/Sources/voice-switch/MacApp.swift" "hotkey interception disabled: Accessibility is not granted"
  require_file "$ROOT/Sources/voice-switch/MacApp.swift" "hotkey finish unavailable: grant Accessibility"
  if grep -R -F -q -e 'System Events' -e 'iTerm' "$ROOT/Sources" "$ROOT/dotnet" --include='*.swift' --include='*.cs'; then
    note_miss "Sources or dotnet now contains System Events or iTerm. The README claim would be false."
  fi
  finish_item readme-perm-mac
}

check_readme_perm_windows() {
  MISS=""
  require_file "$ROOT/README_ja.md" "Windows はマイクのプライバシー設定を使う。このコードはアクセシビリティやオートメーションの許可ダイアログを出さない。"
  require_file "$ROOT/README_ja.md" "昇格したウィンドウへのキーは UIPI でフックを通らない。"
  require_file "$ROOT/README.md" "Windows uses the microphone privacy setting. This code shows no Accessibility or Automation consent dialog."
  require_file "$ROOT/README.md" "UIPI"
  finish_item readme-perm-windows
}

check_readme_handtest_logs() {
  MISS=""
  local pair
  local -a pairs=(
    "tmux:|Sources/voice-switch/PaneRoute.swift"
    "tmux:|dotnet/VoiceSwitch.Windows/TmuxPaneRouter.cs"
    "jev=|Sources/voice-switch/PaneRoute.swift"
    "jev=|dotnet/VoiceSwitch.Windows.Core/PaneRoute.cs"
    "jev:|Sources/voice-switch/PaneRoute.swift"
    "jev body:|Sources/voice-switch/PaneRoute.swift"
    "heard:|Sources/voice-switch/MacApp.swift"
    "dictation session:|dotnet/VoiceSwitch.Windows/DictationRuntime.cs"
    "dictation phase:|dotnet/VoiceSwitch.Windows/DictationRuntime.cs"
    "superwhisper handoff:|Sources/voice-switch/MacApp.swift"
    "hotkey interception disabled: Accessibility is not granted|Sources/voice-switch/MacApp.swift"
    "hotkey finish unavailable: grant Accessibility|Sources/voice-switch/MacApp.swift"
    "downloading speech model for|Sources/voice-switch/MacApp.swift"
    'log("phase:|Sources/voice-switch/main.swift'
    'log("fatal:|Sources/voice-switch/main.swift'
  )
  for pair in "${pairs[@]}"; do
    local text="${pair%%|*}" file="${pair#*|}"
    require_file "$ROOT/$file" "$text"
  done
  local token
  for token in \
    'tmux:' 'jev=' '`jev:`' '`jev body:`' 'heard:' 'dictation session:' '`phase:`' '`dictation phase:`' \
    'superwhisper handoff:' 'hotkey interception disabled: Accessibility is not granted' \
    'hotkey finish unavailable: grant Accessibility' 'fatal:' 'downloading speech model for'
  do
    require_both "$token"
  done
  finish_item readme-handtest-logs
}

check_readme_log_locations() {
  MISS=""
  require_file "$ROOT/README_ja.md" "## 手試験"
  require_file "$ROOT/README_ja.md" "Mac の手試験は、アプリを起動してウェイクワードを話し、make logs で ~/Library/Logs/voice-switch.log を読む。"
  require_file "$ROOT/README_ja.md" 'Windows の手試験は、トレイを起動してウェイクワードを話し、%LOCALAPPDATA%\voice-switch\voice-switch.log を読む。'
  require_file "$ROOT/README.md" "## Hand tests"
  require_file "$ROOT/README.md" "A Mac hand test starts the app, speaks a wake word, and reads ~/Library/Logs/voice-switch.log with make logs."
  require_file "$ROOT/README.md" 'A Windows hand test starts the tray, speaks a wake word, and reads %LOCALAPPDATA%\voice-switch\voice-switch.log.'
  local logs
  logs="$(recipe_of logs)"
  if ! grep -F -q 'voice-switch.log' <<<"$logs"; then
    note_miss "Makefile logs recipe does not mention voice-switch.log"
  fi
  require_file "$ROOT/Sources/voice-switch/Platform.swift" "Library/Logs/voice-switch.log"
  require_file "$ROOT/dotnet/VoiceSwitch.Windows.Core/WindowsPaths.cs" 'voice-switch\voice-switch.log'
  finish_item readme-log-locations
}

check_readme_human_owned() {
  MISS=""
  require_file "$ROOT/README_ja.md" "インストールと、Mac および Windows の手試験は、人が行う。ボットは行わない。"
  require_file "$ROOT/README.md" "The human installs the app and runs the Mac and Windows hand tests. The bot does not."
  finish_item readme-human-owned
}

check_win_stop_alias() {
  MISS=""
  local opts="$ROOT/dotnet/VoiceSwitch.Windows/Tray/TrayOptions.cs"
  local contracts="$ROOT/dotnet/VoiceSwitch.Windows/Tray/TrayContracts.cs"
  local ipc="$ROOT/dotnet/VoiceSwitch.Windows/Tray/TrayIpc.cs"
  require_file "$opts" '"quit" or "stop" => TrayCommand.Quit'
  require_file "$opts" "--tray-command must be one of: status, start, pause, reload, quit, stop, restart."
  local enum
  enum="$(awk '/public enum TrayCommand/,/^}/' "$contracts")"
  if grep -q 'Restart' <<<"$enum"; then
    note_miss "TrayCommand has a Restart member. stop/restart must not grow the pipe enum."
  fi
  if ! grep -q 'Quit' <<<"$enum"; then
    note_miss "TrayCommand is missing Quit"
  fi
  if grep -q 'TrayCommand.Restart' "$ipc"; then
    note_miss "TrayIpc references TrayCommand.Restart"
  fi
  require_file "$ipc" "nameof(TrayCommand.Quit)"
  finish_item win-stop-alias
}

check_win_restart_process() {
  MISS=""
  local opts="$ROOT/dotnet/VoiceSwitch.Windows/Tray/TrayOptions.cs"
  local host="$ROOT/dotnet/VoiceSwitch.Windows/Tray/TrayHost.cs"
  local supervisor="$ROOT/dotnet/VoiceSwitch.Windows/Tray/TrayRuntimeSupervisor.cs"
  require_file "$opts" "bool Restart"
  local restart_block
  restart_block='if (normalized == "restart")
        {
            restart = true;
            command = null;
            return true;
        }'
  if ! python3 -c 'import pathlib,sys; sys.exit(0 if sys.argv[1] in pathlib.Path(sys.argv[2]).read_text() else 1)' "$restart_block" "$opts"; then
    note_miss "TrayOptions.cs is missing the restart parse block that sets restart and leaves command null"
  fi
  require_file "$host" "RestartRunningTray"
  require_file "$host" "TrayCommand.Quit"
  require_file "$host" "ProcessStartInfo"
  if grep -q 'TrayCommand.Restart' "$host" "$opts"; then
    note_miss "restart is represented as TrayCommand.Restart"
  fi
  if grep -q -- '--tray-command' "$host"; then
    note_miss "TrayHost passes --tray-command. The relaunch must start the tray with no tray command."
  fi
  require_file "$supervisor" "ReloadAsync"
  finish_item win-restart-process
}

check_win_help_semantics() {
  MISS=""
  local program="$ROOT/dotnet/VoiceSwitch.Windows/Program.cs"
  require_file "$program" "stop is an alias of quit"
  require_file "$program" "restart sends quit to the running tray, then launches this exe again"
  require_file "$program" "start and pause turn listening on and off"
  require_file "$program" "reload re-reads config in the same process"
  require_file "$ROOT/README_ja.md" '`--tray-command start` と `pause` は待ち受けのオンとオフである。プロセスの起動と終了ではない。'
  require_file "$ROOT/README_ja.md" '`--tray-command stop` は `quit` の別名である。'
  require_file "$ROOT/README_ja.md" '`--tray-command restart` は動いているトレイに quit を送り、終わってから同じ exe を起動し直す。reload は同じプロセスで設定を読み直す。プロセスの再起動ではない。'
  require_file "$ROOT/README.md" '`--tray-command start` and `pause` turn listening on and off. They do not start or stop the process.'
  require_file "$ROOT/README.md" '`--tray-command stop` is an alias of `quit`.'
  require_file "$ROOT/README.md" '`--tray-command restart` sends quit to the running tray, then launches the same exe after that process exits. reload re-reads config in the same process. It is not a process restart.'
  finish_item win-help-semantics
}

check_win_no_second_ipc() {
  MISS=""
  local count
  count="$(grep -R -l 'class TrayIpcServer' "$ROOT/dotnet/VoiceSwitch.Windows" --include='*.cs' | wc -l | tr -d ' ')"
  if [[ "$count" != "1" ]]; then
    note_miss "expected one class TrayIpcServer under dotnet/VoiceSwitch.Windows, found ${count}"
  fi
  local pipes
  pipes="$(grep -R -l -F '"voice-switch-tray-"' "$ROOT/dotnet/VoiceSwitch.Windows" --include='*.cs' || true)"
  if [[ "$pipes" != "$ROOT/dotnet/VoiceSwitch.Windows/Tray/TraySingleInstance.cs" ]]; then
    note_miss "the voice-switch-tray- pipe prefix is not assigned only in TraySingleInstance.cs: ${pipes}"
  fi
  finish_item win-no-second-ipc
}

check_win_make_lifecycle() {
  MISS=""
  local line recipe
  line="$(target_line win-start)"
  [[ -n "$line" ]] || note_miss "Makefile has no win-start target"
  if ! grep -q '##' <<<"$line"; then
    note_miss "win-start is missing ## help text"
  fi
  recipe="$(recipe_of win-start)"
  if ! grep -F -q 'explorer.exe' <<<"$recipe"; then
    note_miss "win-start recipe does not launch through explorer.exe"
  fi
  if ! grep -F -q 'voice-switch.exe' <<<"$recipe"; then
    note_miss "win-start recipe does not name voice-switch.exe"
  fi
  if ! grep -F -q 'RELEASE_DIR' <<<"$recipe"; then
    note_miss "win-start recipe does not use RELEASE_DIR"
  fi
  if grep -F -q -- '--tray-command' <<<"$recipe"; then
    note_miss "win-start recipe passes --tray-command. That target is process start, not listening start."
  fi
  recipe="$(recipe_of win-stop)"
  if ! grep -F -q 'Stop-Process' <<<"$recipe"; then
    note_miss "win-stop no longer runs Stop-Process"
  fi
  line="$(target_line win-restart)"
  [[ -n "$line" ]] || note_miss "Makefile has no win-restart target"
  if ! grep -q 'win-stop' <<<"$line" || ! grep -q 'win-start' <<<"$line"; then
    note_miss "win-restart must depend on win-stop and win-start: ${line}"
  fi
  if ! grep -q '##' <<<"$line"; then
    note_miss "win-restart is missing ## help text"
  fi
  if ! grep -E '^win-stop:.*##' "$ROOT/Makefile" >/dev/null; then
    note_miss "win-stop is missing ## help text"
  fi
  local phony
  phony="$(grep -E '^\.PHONY:' "$ROOT/Makefile" | tr ' ' '\n')"
  for name in win-start win-stop win-restart; do
    if ! grep -q "^${name}$" <<<"$phony"; then
      note_miss ".PHONY is missing ${name}"
    fi
  done
  require_file "$ROOT/README_ja.md" '`make win-stop` は動いている voice-switch.exe を止める。'
  require_file "$ROOT/README_ja.md" '`make win-start` は RELEASE_DIR の voice-switch.exe を起動する。`--tray-command start` ではない。'
  require_file "$ROOT/README_ja.md" '`make win-restart` は win-stop のあと win-start する。'
  require_file "$ROOT/README.md" '`make win-stop` stops the running voice-switch.exe.'
  require_file "$ROOT/README.md" '`make win-start` launches voice-switch.exe from RELEASE_DIR. It does not pass `--tray-command start`.'
  require_file "$ROOT/README.md" '`make win-restart` runs win-stop, then win-start.'
  finish_item win-make-lifecycle
}

check_win_parse_tests() {
  MISS=""
  local tests="$ROOT/dotnet/VoiceSwitch.Windows.Tests/Program.cs"
  local csproj="$ROOT/dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj"
  [[ -f "$tests" ]] || note_miss "missing VoiceSwitch.Windows.Tests/Program.cs"
  require_file "$csproj" "TrayOptions.cs"
  local text
  for text in \
    "stop aliases quit" \
    "restart is not reload" \
    '"--tray-command", "stop"' \
    '"--tray-command", "quit"' \
    '"--tray-command", "restart"' \
    '"--tray-command", "reload"' \
    "TrayCommand.Quit" \
    "TrayCommand.Reload" \
    ".Restart" \
    "IpcCommand is null"
  do
    require_file "$tests" "$text"
  done
  finish_item win-parse-tests
}

check_mac_make_lifecycle() {
  MISS=""
  local line recipe name
  for name in start stop restart; do
    line="$(target_line "$name")"
    [[ -n "$line" ]] || note_miss "Makefile has no ${name} target"
    if ! grep -q '##' <<<"${line:-}"; then
      note_miss "${name} is missing ## help text"
    fi
  done
  recipe="$(recipe_of stop)"
  if ! grep -F -q 'osascript' <<<"$recipe"; then
    note_miss "stop recipe does not run osascript"
  fi
  if ! grep -F -q 'quit app id' <<<"$recipe"; then
    note_miss "stop recipe does not quit app id"
  fi
  if ! grep -F -q '$(ID)' <<<"$recipe"; then
    note_miss "stop recipe does not use \$(ID)"
  fi
  recipe="$(recipe_of start)"
  if ! grep -F -q 'open' <<<"$recipe"; then
    note_miss "start recipe does not open the app"
  fi
  if ! grep -F -q '$(DEST)' <<<"$recipe" || ! grep -F -q '$(APP)' <<<"$recipe"; then
    note_miss "start recipe does not open \$(DEST)/\$(APP)"
  fi
  if grep -F -q 'swift build' <<<"$recipe"; then
    note_miss "start recipe runs swift build"
  fi
  line="$(target_line restart)"
  if ! grep -Eq '^restart:([^#]*[[:space:]])?stop([[:space:]]|$)' <<<"${line:-}"; then
    note_miss "restart must depend on stop: ${line:-missing}"
  fi
  recipe="$(recipe_of restart)"
  if ! grep -F -q 'pgrep' <<<"$recipe" || ! grep -F -q 'sleep' <<<"$recipe"; then
    note_miss "restart recipe does not wait with pgrep and sleep"
  fi
  if ! grep -F -q 'open' <<<"$recipe"; then
    note_miss "restart recipe does not open the app"
  fi
  if grep -F -q 'swift build' <<<"$recipe"; then
    note_miss "restart recipe runs swift build"
  fi
  local phony
  phony="$(grep -E '^\.PHONY:' "$ROOT/Makefile" | tr ' ' '\n')"
  for name in start stop restart; do
    if ! grep -q "^${name}$" <<<"$phony"; then
      note_miss ".PHONY is missing ${name}"
    fi
  done
  finish_item mac-make-lifecycle
}

check_mac_readme_lifecycle() {
  MISS=""
  require_file "$ROOT/README_ja.md" '`make stop` は `osascript` で `quit app id "local.voice-switch"` する。'
  require_file "$ROOT/README_ja.md" '`make start` はインストール済みの VoiceSwitch.app を open する。'
  require_file "$ROOT/README_ja.md" '`make restart` は stop のあと、プロセスが終わるまで短く待って start する。'
  require_file "$ROOT/README.md" '`make stop` runs osascript `quit app id "local.voice-switch"`.'
  require_file "$ROOT/README.md" '`make start` opens the installed VoiceSwitch.app.'
  require_file "$ROOT/README.md" '`make restart` stops, waits until the process exits, then starts.'
  finish_item mac-readme-lifecycle
}

check_mac_cli_optional() {
  MISS=""
  if has "$ROOT/Sources/voice-switch/main.swift" "--restart"; then
    require_both "--restart"
    require_both "quit-then-open"
  fi
  finish_item mac-cli-optional
}

check_scope_pane_proof() {
  MISS=""
  if ! git -C "$ROOT" rev-parse --verify --quiet origin/main >/dev/null; then
    note_miss "origin/main is not available"
  elif ! git -C "$ROOT" diff --quiet origin/main -- scripts/pane_route_proof.py; then
    note_miss "scripts/pane_route_proof.py differs from origin/main"
  fi
  finish_item scope-pane-proof
}

check_scope_no_sw0() {
  MISS=""
  local tracked
  tracked="$(git -C "$ROOT" ls-files -- .claude/sw0.md .claude/sw0b.md)"
  if [[ -n "$tracked" ]]; then
    note_miss "tracked scratch docs: ${tracked}"
  fi
  finish_item scope-no-sw0
}

check_live_mac_handtest() {
  printf 'SKIP live-mac-handtest human installs and runs the Mac hand test\n'
}

check_live_win_handtest() {
  printf 'SKIP live-win-handtest human installs and runs the Windows hand test\n'
}

run_id() {
  local id="$1"
  case "$id" in
    readme-ja-primary) check_readme_ja_primary ;;
    readme-daily-use) check_readme_daily_use ;;
    readme-config-paths) check_readme_config_paths ;;
    readme-perm-mac) check_readme_perm_mac ;;
    readme-perm-windows) check_readme_perm_windows ;;
    readme-handtest-logs) check_readme_handtest_logs ;;
    readme-log-locations) check_readme_log_locations ;;
    readme-human-owned) check_readme_human_owned ;;
    win-stop-alias) check_win_stop_alias ;;
    win-restart-process) check_win_restart_process ;;
    win-help-semantics) check_win_help_semantics ;;
    win-no-second-ipc) check_win_no_second_ipc ;;
    win-make-lifecycle) check_win_make_lifecycle ;;
    win-parse-tests) check_win_parse_tests ;;
    mac-make-lifecycle) check_mac_make_lifecycle ;;
    mac-readme-lifecycle) check_mac_readme_lifecycle ;;
    mac-cli-optional) check_mac_cli_optional ;;
    scope-pane-proof) check_scope_pane_proof ;;
    scope-no-sw0) check_scope_no_sw0 ;;
    live-mac-handtest) check_live_mac_handtest ;;
    live-win-handtest) check_live_win_handtest ;;
    *) die "no check for id ${id}" ;;
  esac
}

drive() {
  local feature="${1:-}"
  check_item_map
  local -a ids=()
  if [[ -n "$feature" ]]; then
    while IFS= read -r id; do
      [[ -n "$id" ]] || continue
      ids+=("$id")
    done < <(feature_ids "$feature")
    [[ ${#ids[@]} -gt 0 ]] || die "feature ${feature} listed no ids"
  else
    while IFS= read -r id; do
      [[ -n "$id" ]] || continue
      ids+=("$id")
    done < <(item_ids)
  fi
  local stamp evidence bad=0 id status
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  evidence="$BASE/evidence/$stamp"
  mkdir -p "$evidence" "$BASE/scratch"
  if [[ -n "$feature" ]]; then
    printf '%s\n' "$feature" >"$evidence/items.txt"
  else
    printf 'all\n' >"$evidence/items.txt"
  fi
  printf '%q ' "$SKILL_DIR/verify.sh" drive ${feature:+"$feature"} >"$evidence/command.txt"
  printf '\n' >>"$evidence/command.txt"
  : >"$evidence/stdout.txt"
  : >"$evidence/stderr.txt"
  local first
  for id in "${ids[@]}"; do
    set +e
    status="$(run_id "$id")"
    local code=$?
    set -e
    printf '%s\n' "$status" | tee -a "$evidence/stdout.txt"
    first="${status%%$'\n'*}"
    if [[ "$code" -ne 0 ]]; then
      bad=$((bad + 1))
    fi
    case "$first" in
      PASS\ *|SKIP\ *|FAIL\ *) ;;
      *)
        printf 'FAIL %s check produced no status line\n' "$id" | tee -a "$evidence/stdout.txt"
        bad=$((bad + 1))
        ;;
    esac
  done
  local pass skip fail
  pass="$(grep -c '^PASS ' "$evidence/stdout.txt" || true)"
  skip="$(grep -c '^SKIP ' "$evidence/stdout.txt" || true)"
  fail="$(grep -c '^FAIL ' "$evidence/stdout.txt" || true)"
  printf 'summary: pass=%s fail=%s skip=%s\n' "$pass" "$fail" "$skip" | tee -a "$evidence/stdout.txt"
  if [[ "$bad" -ne 0 || "$fail" -ne 0 ]]; then
    printf '1\n' >"$evidence/exitcode.txt"
    printf 'evidence: %s\n' "$evidence"
    exit 1
  fi
  printf '0\n' >"$evidence/exitcode.txt"
  printf 'evidence: %s\n' "$evidence"
}

cleanup() {
  rm -rf "$BASE/scratch"
  printf 'cleanup: removed %s\n' "$BASE/scratch"
  printf 'cleanup: evidence kept at %s\n' "$BASE/evidence"
}

cmd="${1:-doctor}"
case "$cmd" in
  doctor) doctor ;;
  drive) drive "${2:-}" ;;
  cleanup) cleanup ;;
  *) die "usage: verify.sh doctor|drive [feature]|cleanup" ;;
esac
