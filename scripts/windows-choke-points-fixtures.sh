#!/usr/bin/env bash
# Proves Lean's Windows tripwire: the pass tree (every module-bound API in its own module, look-alikes, comments) passes,
# every fail case (one forbidden or misplaced use each) fails, and windows/src itself passes.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; S="$ROOT/scripts/windows-choke-points.sh"; F="$ROOT/scripts/fixtures/windows-choke-points"
bad=0
"$S" "$F/pass" >/dev/null || { echo "WRONG: the pass tree failed"; "$S" "$F/pass"; bad=1; }
# ASP-33: the private module's pass tree runs in module mode (what scripts/windows-choke-points-pro.sh sets).
LEAN_TRIPWIRE_MODULE=1 "$S" "$F/pass-module" >/dev/null || { echo "WRONG: the module pass tree failed"; LEAN_TRIPWIRE_MODULE=1 "$S" "$F/pass-module"; bad=1; }
n=0
for d in "$F"/fail/*/; do
  n=$((n + 1))
  # Each fail case has one line of code, line 6 of its one file: the case must fail, and on that line only.
  mode=0; [ -e "${d}module-mode" ] && mode=1   # a case with a module-mode file runs as the module's tripwire does
  if out=$(LEAN_TRIPWIRE_MODULE=$mode "$S" "${d%/}"); then echo "WRONG: $(basename "$d") passed"; bad=1
  elif echo "$out" | grep -E '^/' | grep -vqE ':6:'; then echo "WRONG: $(basename "$d") failed on another line"; echo "$out"; bad=1; fi
done
"$S" >/dev/null || { echo "WRONG: windows/src fails"; "$S"; bad=1; }
[ $bad = 0 ] && echo "PASS: the pass tree passes, $n of $n fail cases fail, windows/src passes" || { echo "FAIL"; exit 1; }
