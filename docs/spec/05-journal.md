# 05 — Journal and Put back

## Entry

One entry per item per outcome, appended as one JSON object per line:

| Field | Meaning |
|---|---|
| `id` | unique id |
| `sessionId` | the deletion session (one confirmation) |
| `time` | ISO 8601 UTC |
| `module` | `map`, `largeOld`, `duplicates`, `uninstall`, `emptyFolders`, `brokenShortcuts`, `iphoneBackup`; Windows note: also `temp` (Temporary files, 03) |
| `originalPath` | canonical path at delete time |
| `originalParentRealPath` | real path of the parent folder at delete time |
| `approvedRealPath` | the path the gate approved |
| `bytes` | size measured immediately before the move |
| `mode` | `trash` (the only v1 value) |
| `trashPath` | where the system put it (Windows note: the `$R` item in `$Recycle.Bin\<user SID>`) |
| `trashedIdentity` | device, inode and size of the item in the Trash (Windows note: volume serial, 128-bit file id, logical size; the size is `-1` when the item reached the Recycle Bin with its identity but its size there could not be measured, and Put back then checks the identity only: owner decision 2026-10-18) |
| `result` | `trashed`, `skipped` (+ reason), `restored`, `restoreFailed` (+ reason); Windows note: also `lost` — the move was made and the item is not in the Recycle Bin; its reason, shown in the history and by Put back, is "Windows deleted this permanently instead of moving it to the Recycle Bin", never "emptied or moved"; an item that reached the Recycle Bin with the approved identity but another size is `trashed` with the reason "The moved item did not match the approved item — check the Recycle Bin" (02), not `lost` |
| `refersTo` | for `restored` and `restoreFailed`: the `id` of the `trashed` entry it refers to |
| `appVersion` | app version |

- Location: macOS note: `~/Library/Application Support/Ambysto Space/journal.jsonl`. Windows note: `%LOCALAPPDATA%\Ambysto\Space\journal.jsonl`. The folder keeps the app's first name, Ambysto Space, after the rename to Ambysto Lean, so an existing journal and its Put back stay available; a move of this folder needs its own decision.
- Written append-only and flushed to disk after each entry. A torn last line is ignored when reading.
- Never uploaded. Readable without the app.
- Everything is stored. Free shows the last 30 days; Pro shows everything and can export.

Windows note (2026-10-18): the app also appends `crash.log` to the journal's folder when it ends on an unhandled failure. The log is best effort: a failure inside the UI framework that ends the process at once (a stowed exception, 0xc000027b) may leave no line. It is never read by the app and holds no file contents, only the failure's type, code, message and stack.

## Put back

Available per entry and **per session** ("Put back all from this session"). Each entry is restored only if all hold; otherwise the reason is shown and journaled:

| Check | Failure reason |
|---|---|
| The Trash item exists | "No longer in the Trash (emptied or moved)" |
| Its identity and size equal `trashedIdentity` | "The item in the Trash is a different file" |
| Its path is inside a Trash folder | "Not in a Trash folder" |
| The destination passes the allowed-root and never-touch rules (02) | "Cannot restore to a protected location" |
| Nothing exists at the destination | "Something already exists at the original location" |
| The parent folder's real path equals `originalParentRealPath` | "The original folder no longer exists or moved" |

The allowed root for the destination is judged from the destination path itself, never from the journal line. Nothing is put back unless the journal can be written first.

Threat model: Put back does not authenticate journal lines. Changing the journal requires the user's own write access, which can already write anywhere Put back could restore to, so a forged line gains nothing. Put back's checks protect against mistakes: an emptied Trash, a different file, an occupied or protected destination.

Closing the window while Put back runs (Windows, ASP-24) is held until it ends, so every restore has its entry; the status line says so (07) and the window then closes by itself. A successful restore appends a `restored` entry. Windows note (owner decision 2026-10-16): Put back never uses the shell's restore (it shows a blocking dialog when the destination is taken and leaves its index record behind). The Kit's environment check runs first (elevated, SYSTEM or not an interactive session: "Moving is off while running as administrator." or the Kit's matching reason, before any check above); then the checks above; then it makes one move of the `$R` item to the original path, with no copy fallback; the Recycle Bin is per volume, so the move never crosses volumes, and any failure is `restoreFailed` with its reason. The `$I` record that stays in the Recycle Bin is left alone: removing it would be a permanent delete. Allowed roots for a destination include, by the destination path alone (owner decision 2026-10-16), the leftover folders of 04 and the user's `Start Menu\Programs` (not `Startup`), so "Put back all from this session" restores an app's leftovers.

An older build skips a journal line whose `module` it does not know. After a downgrade, `emptyFolders` and `brokenShortcuts` entries leave Lean's Put back list; the items stay in the Trash, and Finder's own Put Back still works.

## Command log (Windows, ASP-20)

Apps and caches (06, Pro) can run a tool's own documented clean command (`npm cache clean --force`, `pip cache purge`, `uv cache clean`, `dotnet nuget locals http-cache --clear`, `dotnet nuget locals plugins-cache --clear`). That is not a move to the Recycle Bin: nothing can be put back, and the confirmation says so before the run. Each run that starts is recorded in a separate file beside the journal, `commands.jsonl`, one JSON object per line: `id`, `time` (ISO 8601 UTC), `entryId`, `program` (full path), `arguments`, `location` (the cache folder the tool named), `bytesBefore`, `bytesAfter` (measured by Lean, never the tool's own report; null when the folder could not be measured), `exitCode` (null when Lean stopped waiting), `timedOut`, `result` (`cleaned`, `failed`, `timedOut`), `reason`, `appVersion`. The journal's own format is unchanged, so Put back never sees a command run. No command starts, not even the query that names the cache folder, while Lean runs as administrator, as SYSTEM or outside the user's desktop session, or when that cannot be checked (02). A run is refused, and nothing is recorded, in that case and when the row was not ready, the tool is running (checked again just before the run), the program or the cache folder changed since the user approved it, the log cannot be written, or Windows did not start the program. A program still running after 5 minutes is left running: Lean stops waiting and records it as `timedOut`. Closing the window during a run is held until the run ends (a confirmation still open is closed as Cancel, and nothing runs), so every run that starts is recorded. A log line whose arguments are missing or hold a null, or whose id, program, entry id or location is empty, is skipped when the log is read. History (06) shows each recorded run as its own entry, beside the journal's sessions and under the same 30-day limit in Free, with the command, the sizes before and after and no Put back.
