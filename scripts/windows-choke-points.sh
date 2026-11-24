#!/usr/bin/env bash
# Lean's tripwire for Windows (intent "Repository"; CLAUDE.md Windows rules). Every deletion goes through the Trasher
# (phase B), Put back is the only other move, the login item is the only registry write, the launcher module is the
# only place that opens Settings or File Explorer, and the clean runner is the only place that starts a process (ASP-20).
# This fails when windows/src names a forbidden API anywhere, or a module-bound API outside its module. A tripwire, not
# a proof: whole-line comments (// or <!--) are not checked; a trailing comment is (fail closed).
# Usage: scripts/windows-choke-points.sh [source-dir]   (default: windows/src; paths below are relative to it)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="${1:-$ROOT/windows/src}"
[ -d "$SRC" ] || { echo "FAIL: no source folder $SRC"; exit 1; }
# Every file MSBuild could compile or load as XAML, whatever the case of its extension.
INCLUDES=(--include='*.[cC][sS]' --include='*.[xX][aA][mM][lL]')

# Nowhere in windows/src.
FORBIDDEN=(
  # deleting
  '\b(File|Directory|FileSystem)\.Delete[A-Za-z]*\b'
  '\.Delete\s*\('
  'DeleteFile[A-Za-z]*\b|\bRemoveDirectory[A-Za-z]*\b'
  '\bSHFileOperation[A-Za-z]*\b|\bSHEmptyRecycleBin[A-Za-z]*\b|\bFOFX_RECYCLEONDELETE\b'
  '\bFileDispositionInfo[A-Za-z]*\b|\bFILE_DISPOSITION_[A-Z_]+\b|\bDeleteOnClose\b|\bFILE_FLAG_DELETE_ON_CLOSE\b'
  # FileInfo/DirectoryInfo carry Delete, MoveTo, Replace and CopyTo: Lean reads through handles and File/Directory reads
  '\b(FileInfo|DirectoryInfo|FileSystemInfo)\b'
  '\bFile\.Replace\b|\bReplaceFile[A-Za-z]*\b|\bMoveTo\s*\('
  # WinRT storage and the shell's scripting object, which delete, move, recycle and restore outside the Trasher
  '\b(Delete|Move|Rename|MoveAndReplace|Copy|CopyAndReplace)Async\b|\bStorageDeleteOption\b'
  'Shell\.Application|\bInvokeVerb[A-Za-z]*\b|\bMoveHere\b|\bCopyHere\b|\bShell32\.(Shell|Folder)[A-Za-z]*\b'
  # copying instead of moving, renaming by handle
  '\bMOVEFILE_COPY_ALLOWED\b|\bCopyFile[A-Za-z]*\b|\bFile\.Copy\b'
  '\bSetFileInformationByHandle\b|\b(Nt|Zw)SetInformationFile\b|\bFileRenameInfo[A-Za-z]*\b'
  # processes (starting one is bound to the clean runner, below; ending one is forbidden everywhere)
  '\.Kill\s*\(|\bTerminateProcess\b|\bCreateProcess[A-Za-z]*\b|\bShellExecute[A-Za-z]*\b|\bWinExec\b'
  '"[Rr][Uu][Nn][Aa][Ss]"|\bRm(Shutdown|Restart)\b'
  # shortcuts: never rewritten
  '\bSLR_UPDATE\b|\.Save\s*\([^,()]*,'
  # cloud placeholders: never changed
  '\bCf(HydratePlaceholder|DehydratePlaceholder|SetPinState|SetInSyncState|ConvertToPlaceholder|UpdatePlaceholder|RevertPlaceholder)\b'
  # Settings or a page opened without the launcher module
  '\bNavigateUri\b'
  # names hidden behind using static or an alias
  '\busing\s+static\s+(System\.IO\.(File|Directory|FileInfo|DirectoryInfo)|System\.Diagnostics\.Process|Microsoft\.VisualBasic|Windows\.System\.Launcher|Windows\.Storage)\b'
  '\busing\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*(global::)?(System\.IO\.(File|Directory|FileInfo|DirectoryInfo)|System\.Diagnostics\.Process|Microsoft\.VisualBasic|Windows\.System\.Launcher|Windows\.Storage)\b'
)

# Allowed only in the named file (exact path) or folder (a prefix ending in /), relative to the source folder; the text
# before the first | is the place, the rest the pattern. Nowhere else.
BOUND=(
  'Ambysto.Lean.Core/Trash/ShellRecycler.cs|\bIFileOperation[A-Za-z]*\b|\bCLSID_FileOperation\b|\bFOF_[A-Z_]+\b|3[aA][dD]05575|947[aA][aA][bB]5[fF]|\bIFileOperationProgressSink\b'
  'Ambysto.Lean.Core/Journal/PutBack.cs|\b(File|Directory)\.Move\b|\bMoveFile[A-Za-z]*\b|\bFileSystem\.Move[A-Za-z]*\b'
  'Ambysto.Lean.App/SystemLauncher.cs|\bLauncher\b|\bLaunch(Uri|Folder|File)[A-Za-z]*\b|\b[Ee]xplorer\.exe\b'
  'Ambysto.Lean.Core/Tidy/ShortcutReader.cs|\bIPersistFile\b|\bIShellLink[A-Za-z]*\b'
  # ASP-20 decision 12: the documented clean commands, with fixed arguments, are the only process Lean starts.
  'Ambysto.Lean.Core/Catalog/ProcessCommandRunner.cs|\bProcess\.Start\b|\bProcessStartInfo\b|\.StartInfo\b|\bnew\s+Process\s*[({]|\bProcess\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*new\s*\('
)

# ASP-33: the one registry write (the user's Run value) belongs to the private Pro module. The public run (windows/src) forbids it
# everywhere; scripts/windows-choke-points-pro.sh sets LEAN_TRIPWIRE_MODULE=1 and then allows it at Ambysto.Lean.Pro/LoginItem.cs only.
REGISTRY_WRITE='\.(SetValue|CreateSubKey|DeleteValue|DeleteSubKey[A-Za-z]*|SetAccessControl)\s*\(|OpenSubKey\([^)]*(writable:\s*true|,\s*true\s*\))|\bRegistry\.SetValue\b|\bReg(Set|Create|Delete|Save|Restore|Replace|Load|Unload|Rename|Copy)[A-Za-z]*\b|\bRegistryKeyPermissionCheck\.ReadWrite'
if [ "${LEAN_TRIPWIRE_MODULE:-0}" = 1 ]; then BOUND+=("Ambysto.Lean.Pro/LoginItem.cs|$REGISTRY_WRITE"); else FORBIDDEN+=("$REGISTRY_WRITE"); fi

hits=0
report() { echo "$1"; hits=$((hits + $(echo "$1" | wc -l))); }
uncommented() { grep -vE '^[^:]+:[0-9]+:\s*(//|<!--)'; }
for p in "${FORBIDDEN[@]}"; do
  if out=$(grep -rnE "${INCLUDES[@]}" "$p" "$SRC" | uncommented); then report "$out"; fi
done
for rule in "${BOUND[@]}"; do
  place="${rule%%|*}"; p="${rule#*|}"
  if out=$(grep -rnE "${INCLUDES[@]}" "$p" "$SRC" | uncommented | while IFS= read -r line; do
             rel="${line#"$SRC"/}"; file="${rel%%:*}"
             if [[ "$place" == */ ]]; then [[ "$file" == "$place"* ]] && continue; else [[ "$file" == "$place" ]] && continue; fi
             echo "$line"
           done | grep .); then
    report "$out"
  fi
done
if [ "$hits" -gt 0 ]; then echo "FAIL: $hits forbidden or misplaced use(s) in $SRC"; exit 1; fi
echo "PASS: no forbidden or misplaced use in $SRC"
