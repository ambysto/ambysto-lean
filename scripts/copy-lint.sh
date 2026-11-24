#!/usr/bin/env bash
# Proof 3: user-facing strings never use the words docs/spec/07-copy.md bans.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PATTERN='"[^"]*\b(junk|free up|freed|cleaned|clean up your|boost|speed up|optimi[sz]e|garbage)\b[^"]*"'
# macOS: the Swift sources. Windows (ASP-7): the C# and XAML sources, when windows/src exists.
DIRS=("$ROOT/macos/Sources")
[[ -d "$ROOT/windows/src" ]] && DIRS+=("$ROOT/windows/src")
HITS="$(grep -rniE "$PATTERN" "${DIRS[@]}" --include='*.swift' --include='*.cs' --include='*.xaml' | grep -vE '^[^:]+:[0-9]+:[[:space:]]*(//|<!--)' || true)"
if [[ -n "$HITS" ]]; then
  echo "$HITS"
  echo "FAIL: banned words in user-facing strings (docs/spec/07-copy.md)"
  exit 1
fi
echo "PASS: no banned words in user-facing strings"
