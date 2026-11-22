#!/bin/sh
set -eu
if [ "$#" -ne 1 ]; then
  echo "usage: $0 <base-ref>" >&2
  exit 2
fi
cd "$(dirname "$0")/.."
exec python3 tests/verification_coverage.py affected "$1"
