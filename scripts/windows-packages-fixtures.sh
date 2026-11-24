#!/usr/bin/env bash
# Proves Lean's package check: the pass tree passes (bin/ and obj/ skipped), the KIT-LOCAL tree passes marked NOT
# MERGEABLE, every fail case fails, windows/ passes.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; S="$ROOT/scripts/windows-packages.sh"; F="$ROOT/scripts/fixtures/windows-packages"
bad=0
"$S" "$F/pass" >/dev/null || { echo "WRONG: the pass tree failed"; "$S" "$F/pass"; bad=1; }
out=$("$S" "$F/pass-local") && echo "$out" | grep -q "NOT MERGEABLE" || { echo "WRONG: the KIT-LOCAL tree did not pass with NOT MERGEABLE"; echo "$out"; bad=1; }
n=0
for d in "$F"/fail/*/; do
  n=$((n + 1))
  if "$S" "${d%/}" >/dev/null; then echo "WRONG: $(basename "$d") passed"; bad=1; fi
done
"$S" >/dev/null || { echo "WRONG: windows/ fails"; "$S"; bad=1; }
[ $bad = 0 ] && echo "PASS: the pass tree passes, $n of $n fail cases fail, windows/ passes" || { echo "FAIL"; exit 1; }
