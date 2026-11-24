#!/usr/bin/env bash
# Lean's package check for Windows (intent Constraints; plan group 2). Usage: scripts/windows-packages.sh [windows-dir]
exec python3 -I "$(cd "$(dirname "$0")" && pwd)/windows-packages.py" "$@"
