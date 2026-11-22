#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
exec python3 tests/verification_coverage.py gate
