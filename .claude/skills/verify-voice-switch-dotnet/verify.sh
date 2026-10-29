#!/usr/bin/env bash
# Drive VoiceSwitch.Windows.Program.Run on this Linux .NET 8 host.
# Evidence stays under ${VS_VERIFY_ROOT}/evidence. Cleanup removes the driver build only.
set -euo pipefail

SKILL_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd "$SKILL_DIR/../../.." && pwd)
ROOT=${VS_VERIFY_ROOT:-/tmp/voice-switch-dotnet-verify}
DRIVER="$ROOT/driver"
EVIDENCE_ROOT="$ROOT/evidence"
DLL="$DRIVER/bin/Release/net8.0-windows/voice-switch-verify-cli.dll"

export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

usage() {
  echo "usage: $0 doctor | build | cli -- [args...] | cleanup" >&2
  exit 2
}

need_dotnet() {
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not on PATH (expected \$HOME/.dotnet)" >&2
    exit 1
  fi
  local version
  version=$(dotnet --version)
  case "$version" in
    8.*) ;;
    *)
      echo "dotnet SDK $version is not 8.x" >&2
      exit 1
      ;;
  esac
  printf '%s\n' "$version"
}

generate_driver() {
  mkdir -p "$DRIVER"
  python3 - "$REPO" "$DRIVER" <<'PY'
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

repo = Path(sys.argv[1])
driver = Path(sys.argv[2])
tests_csproj = repo / "dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj"
root = ET.parse(tests_csproj).getroot()
includes = []
for node in root.iter("Compile"):
    inc = node.attrib.get("Include")
    if not inc:
        continue
    includes.append((tests_csproj.parent / Path(inc)).resolve())
if not includes:
    raise SystemExit("test csproj has no Compile Include entries")
names = {path.name for path in includes}
stub = (tests_csproj.parent / "WindowFocusStub.cs").resolve()
if "WindowFocus.cs" not in names and stub.name not in names:
    includes.append(stub)
main = driver / "drive-main.cs"
main.write_text("return VoiceSwitch.Windows.Program.Run(args);\n", encoding="utf-8")
core = (repo / "dotnet/VoiceSwitch.Windows.Core/VoiceSwitch.Windows.Core.csproj").resolve()
lines = [
    '<Project Sdk="Microsoft.NET.Sdk">',
    "  <PropertyGroup>",
    "    <OutputType>Exe</OutputType>",
    "    <TargetFramework>net8.0-windows</TargetFramework>",
    "    <EnableWindowsTargeting>true</EnableWindowsTargeting>",
    "    <ImplicitUsings>enable</ImplicitUsings>",
    "    <Nullable>enable</Nullable>",
    "    <LangVersion>latest</LangVersion>",
    "    <RollForward>LatestMajor</RollForward>",
    "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
    "    <UseAppHost>false</UseAppHost>",
    "    <AssemblyName>voice-switch-verify-cli</AssemblyName>",
    "  </PropertyGroup>",
    "  <ItemGroup>",
    f'    <Compile Include="{main.as_posix()}" />',
]
for path in includes:
    lines.append(f'    <Compile Include="{path.as_posix()}" />')
lines += [
    "  </ItemGroup>",
    "  <ItemGroup>",
    f'    <ProjectReference Include="{core.as_posix()}" />',
    "  </ItemGroup>",
    "</Project>",
    "",
]
(driver / "Drive.csproj").write_text("\n".join(lines), encoding="utf-8")
print(f"driver sources: {len(includes) + 1}")
PY
}

ensure_driver() {
  generate_driver
  mkdir -p "$ROOT"
  if ! dotnet build "$DRIVER/Drive.csproj" -c Release --ignore-failed-sources --disable-parallel >"$ROOT/driver-build.log" 2>&1; then
    echo "driver build failed; log: $ROOT/driver-build.log" >&2
    tail -n 50 "$ROOT/driver-build.log" >&2
    exit 1
  fi
  if [ ! -f "$DLL" ]; then
    echo "driver build did not produce $DLL" >&2
    exit 1
  fi
}

cmd_doctor() {
  local version dirty head
  version=$(need_dotnet)
  [ -f "$REPO/dotnet/VoiceSwitch.Windows/Program.cs" ] || { echo "missing Program.cs" >&2; exit 1; }
  [ -f "$REPO/dotnet/VoiceSwitch.Windows.Core/CliOptions.cs" ] || { echo "missing CliOptions.cs" >&2; exit 1; }
  [ -f "$REPO/dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj" ] || { echo "missing test csproj" >&2; exit 1; }
  [ -f "$REPO/dotnet/VoiceSwitch.Windows.Tests/WindowFocusStub.cs" ] || { echo "missing WindowFocusStub.cs" >&2; exit 1; }
  command -v python3 >/dev/null 2>&1 || { echo "python3 missing" >&2; exit 1; }
  head=$(git -C "$REPO" rev-parse --short HEAD)
  dirty=$(git -C "$REPO" status --porcelain | wc -l | tr -d ' ')
  echo "repo: $REPO"
  echo "head: $head"
  echo "dirty files: $dirty"
  echo "dotnet: $version"
  echo "python3: $(python3 --version)"
  echo "driver root: $ROOT"
  echo "doctor: ok"
}

cmd_build() {
  need_dotnet >/dev/null
  mkdir -p "$ROOT/win-x64"
  if ! dotnet build "$REPO/dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj" -c Release -r win-x64 --no-restore -o "$ROOT/win-x64" >"$ROOT/win-build.log" 2>&1; then
    echo "win-x64 build failed; log: $ROOT/win-build.log" >&2
    tail -n 40 "$ROOT/win-build.log" >&2
    exit 1
  fi
  if [ ! -f "$ROOT/win-x64/voice-switch.dll" ]; then
    echo "win-x64 build did not produce voice-switch.dll" >&2
    exit 1
  fi
  if ! grep -q "0 Error(s)" "$ROOT/win-build.log" || ! grep -q "0 Warning(s)" "$ROOT/win-build.log"; then
    echo "win-x64 build log is missing 0 Warning(s) and 0 Error(s)" >&2
    exit 1
  fi
  mkdir -p "$EVIDENCE_ROOT"
  cp "$ROOT/win-build.log" "$EVIDENCE_ROOT/win-x64-build.log"
  echo "build: $ROOT/win-x64/voice-switch.dll"
  echo "build log: $EVIDENCE_ROOT/win-x64-build.log"
}

cmd_cli() {
  if [ "${1:-}" = "--" ]; then
    shift
  fi
  need_dotnet >/dev/null
  ensure_driver
  local run_id dest pid code
  run_id=${VS_RUN_ID:-$(date +%Y%m%dT%H%M%S)}
  dest="$EVIDENCE_ROOT/$run_id"
  mkdir -p "$dest"
  git -C "$REPO" rev-parse HEAD >"$dest/git-head.txt"
  {
    printf '%s\n' "dotnet" "exec" "$DLL"
    printf '%s\n' "$@"
  } >"$dest/command.txt"
  # dotnet exec forwards every argument, including those that start with a dash.
  # elapsed-ms.txt is the app process only. Driver build time stays out of it.
  local start_ms end_ms
  start_ms=$(date +%s%3N)
  dotnet exec "$DLL" "$@" >"$dest/stdout.txt" 2>"$dest/stderr.txt" &
  pid=$!
  printf '%s\n' "$pid" >"$dest/pid.txt"
  printf '%s\n' "$pid" >"$ROOT/active.pid"
  set +e
  wait "$pid"
  code=$?
  set -e
  end_ms=$(date +%s%3N)
  printf '%s\n' "$code" >"$dest/exitcode.txt"
  printf '%s\n' "$((end_ms - start_ms))" >"$dest/elapsed-ms.txt"
  rm -f "$ROOT/active.pid"
  echo "exit: $code"
  echo "evidence: $dest"
  return "$code"
}

cmdline_of() {
  local pid=$1
  if [ -r "/proc/$pid/cmdline" ]; then
    tr '\0' ' ' <"/proc/$pid/cmdline"
  fi
}

cmd_cleanup() {
  local pid cmd
  if [ -f "$ROOT/active.pid" ]; then
    pid=$(tr -d '[:space:]' <"$ROOT/active.pid")
    cmd=$(cmdline_of "$pid" || true)
    case "$cmd" in
      *voice-switch-verify-cli*)
        kill "$pid" 2>/dev/null || true
        sleep 0.5
        if [ -d "/proc/$pid" ]; then
          kill -9 "$pid" 2>/dev/null || true
        fi
        echo "killed driver pid $pid"
        ;;
      "")
        echo "active pid $pid is not running"
        ;;
      *)
        echo "active pid $pid is not the verify driver; left it alone" >&2
        ;;
    esac
    rm -f "$ROOT/active.pid"
  fi
  if [ -d "$DRIVER" ]; then
    rm -rf "$DRIVER"
    echo "removed driver: $DRIVER"
  fi
  rm -f "$ROOT/driver-build.log"
  if [ -d "$ROOT/win-x64" ]; then
    rm -rf "$ROOT/win-x64"
    echo "removed win-x64 build: $ROOT/win-x64"
  fi
  rm -f "$ROOT/win-build.log"
  echo "evidence kept: $EVIDENCE_ROOT"
}

[ $# -ge 1 ] || usage
case "$1" in
  doctor) cmd_doctor ;;
  build) cmd_build ;;
  cli) shift; cmd_cli "$@" ;;
  cleanup) cmd_cleanup ;;
  *) usage ;;
esac
