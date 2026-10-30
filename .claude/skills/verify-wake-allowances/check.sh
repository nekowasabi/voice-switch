#!/bin/sh
# Bounded-exhaustive check of the wake relaxations against the real Core code. Usage: check.sh
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
dotnet=${DOTNET:-dotnet}
command -v "$dotnet" >/dev/null 2>&1 || dotnet="$HOME/.dotnet/dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec "$dotnet" run --project "$dir/check/WakeAllowances.csproj" -c Release
