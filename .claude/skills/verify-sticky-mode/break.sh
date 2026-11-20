#!/bin/sh
# The broken model must fail. Exit 0 only when TLC rejects it.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
out=$(mktemp)
if sh "$dir/check.sh" "$dir/StickyMode.tla" "$dir/Broken.cfg" >"$out" 2>&1; then
  echo "break: broken model passed; the invariants do not catch a bad paste" >&2
  rm -f "$out"
  exit 1
fi
if grep -E -q 'Invariant (NeverWrongPaste|NeverSecurePaste|EnterAfterPaste) is violated' "$out"; then
  echo "break: rejected a paste into the wrong place or a secure field"
  rm -f "$out"
  exit 0
fi
echo "break: TLC failed for a different reason" >&2
tail -n 40 "$out" >&2
rm -f "$out"
exit 1
