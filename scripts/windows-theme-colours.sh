#!/usr/bin/env bash
# ASP-12 (intent/2026-10-18-asp-12-brand-theme.md): no view or code file names a colour. Only the top-level Theme.xaml (the frame
# colours) and BrandPalette.cs (the BrandPalette class: the map's tones) may. In every other .cs and .xaml under the source folder
# this fails on: a hex colour literal (#rgb, #argb, #rrggbb, #aarrggbb); Color.FromArgb or ColorHelper.FromArgb; Colors.<Name>;
# a Color { ... } or new Color initializer; a XAML named colour (Background, Foreground, BorderBrush, Fill, Stroke or Color set to a
# plain word such as Red or Transparent). Comments (// to the end of the line, /* */ and <!-- -->) are stripped first, so a colour
# outside a comment on the same line still counts. A tripwire, not a proof.
# Usage: scripts/windows-theme-colours.sh [source-dir]   (default: windows/src/Ambysto.Lean.App)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="${1:-$ROOT/windows/src/Ambysto.Lean.App}"
[ -d "$SRC" ] || { echo "FAIL: no source folder $SRC"; exit 1; }
SRC="$(cd "$SRC" && pwd)"
python3 -I - "$SRC" <<'PY'
import os
import re
import sys

src = sys.argv[1]
exempt = {os.path.join(src, "Theme.xaml"), os.path.join(src, "BrandPalette.cs")}
patterns = [
    ("hex colour", re.compile(r"(?<![&\w])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})(?!\w)")),
    ("Color.FromArgb", re.compile(r"\b(?:Color|ColorHelper)\s*\.\s*FromArgb\b")),
    ("Colors.<Name>", re.compile(r"\bColors\s*\.\s*[A-Za-z]+")),
    ("Color initializer", re.compile(r"\bnew\s+Color\b|\bColor\s*\{")),
    ("named colour", re.compile(r"\b(?:Background|Foreground|BorderBrush|Fill|Stroke|Color)\s*=\s*\"[A-Za-z]+\"")),
]
block = re.compile(r"/\*.*?\*/|<!--.*?-->", re.S)
line_comment = re.compile(r"(?:(?<=\s)|^)//.*$")


def blank(match):
    return "\n" * match.group(0).count("\n")


hits = []
for folder, dirs, files in os.walk(src):
    dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
    for name in sorted(files):
        if os.path.splitext(name)[1].lower() not in (".cs", ".xaml"):
            continue
        path = os.path.join(folder, name)
        if path in exempt:
            continue
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            text = f.read()
        text = block.sub(blank, text)
        for number, line in enumerate(text.split("\n"), 1):
            code = line_comment.sub("", line)
            for label, pattern in patterns:
                if pattern.search(code):
                    hits.append(f"{path}:{number}: {label}: {line.strip()}")
                    break

if hits:
    print("\n".join(hits))
    print("FAIL: colour named outside Theme.xaml and BrandPalette (ASP-12)")
    sys.exit(1)
print("PASS: no colour named outside Theme.xaml and BrandPalette")
PY
