# 02 — Safety

Derived from Ambysto Optimer's `02-cleanup-safety.md`. **Measure first, change safely, always reversible.**

## Deletion mode

v1 has one mode: **Move to Trash**. There is no permanent delete. Every deletion is recoverable from the Trash and through the journal's Put back. Lean never empties the Trash.

## Allowed roots

An item may be deleted only if it falls in one of these:

1. **Home item** — inside the user's home folder, at least 2 path components below it, and **not inside `~/Library`**, except `~/Library/Caches/*` and `~/Library/Logs/*`.
2. **Application bundle** — a folder named `*.app` directly inside `/Applications` or `~/Applications`, or one subfolder below them; it has `Contents/Info.plist`; the user can write both the bundle and its parent folder; it is not a symbolic link, not under the system volume, not protected by the OS.
3. **Leftover** — exactly an item the Uninstall module's matcher produced (see 04), inside the leftover folders.
4. **iPhone backup / iOS update (`iphoneBackup`)** — a direct device backup folder inside `~/Library/Application Support/MobileSync/Backup` or an `.ipsw` package inside `~/Library/iTunes/iPhone Software Updates` / `iPad Software Updates` selected explicitly in the iPhone module (kept protected against accidental Map / Large & Old / Duplicate deletion).

Never an item on another volume.

Windows note (owner decisions 2026-10-16): the item may be moved only on the volume that holds the profile, when (a) inside `%USERPROFILE%`, at least 2 path components below it, and not inside `AppData` except `%LOCALAPPDATA%\Temp\*` and `%LOCALAPPDATA%\CrashDumps\*` (by these literal paths); or (b) inside a folder at the root of that volume that the current user owns, and every folder down to the item too (a folder owned by `Administrators` does not count; the item itself, when it is a folder, too; a file item only has to be readable: owner decision 2026-10-17), none of them a link (junction, symbolic link or mount point; the item itself neither), at least one component inside that top folder, never a system location or `Users`; or (c) exactly an item a module produced in its module-bound root: a leftover of an uninstalled app (04) or a broken shortcut in the user's own `Start Menu\Programs`, not `Startup` (the tidy module, ASP-6). Application bundles do not exist; Lean never runs an uninstaller (04). A put-back destination is judged from its path (05). The never-touch list and the gate are the Ambysto Kit's (`docs/spec/safety.md`, Windows sections), which also refuses every move while elevated or outside an interactive session.

## Never touch

Refused at selection **and** at delete time, including everything beneath, and refused if the item **contains** one of them:

- `~/.claude/projects`, `~/.ssh`, `~/.gnupg`
- `~/Pictures`, any photo library (`*.photoslibrary`)
- `~/Library/Keychains`, any keychain file
- `~/Library/Mail`, `~/Library/Messages`
- `~/Library/Mobile Documents` (iCloud Drive), `~/Library/CloudStorage` (other cloud drives)
- `~/Library/Application Support/MobileSync/Backup` (device backups), Time Machine data (`Backups.backupdb`, `*.backupbundle`)
- `~/Library/Application Support/com.apple.TCC` (privacy database), `~/Library/Application Support/AddressBook`, `~/Library/Calendars`, `~/Library/Accounts`
- `~/Library/Application Support/Ambysto Space` (the journal)
- any child of `Containers`, `Group Containers`, `Application Support` whose name starts with `com.apple.` or `group.com.apple.`
- main browser profiles: Safari, Chrome, Arc, Firefox, Brave, Edge
- `~/.Trash` (reading its size is allowed)
- `/System`, `/Library`, `/private`, `/usr`, `/opt`, `/cores`, anything the OS marks restricted

Windows note: the list is the Ambysto Kit's (`docs/spec/safety.md`, Windows column), which already holds every cloud-sync root and everything inside it, local files included (owner decision 2026-10-16: never touched in v1). Lean adds its own journal folder, `%LOCALAPPDATA%\Ambysto\Space` (05), as the Kit lets an app add its data folders. **The user's protected folders** (Windows, owner decision 2026-10-18): in Settings the user adds folders that Lean must never touch, and can remove the ones they added. They join the list the same way, so an item inside or holding one is refused at selection (display-only, "Protected by you") and at move time, and a put-back into one is refused. They only add protection: nothing removes or weakens the list above. The list is kept in Lean's settings file, `%LOCALAPPDATA%\Ambysto\Space\settings.jsonl` (append-only lines, the last line that parses wins, as the journal), never in the registry. It is a free feature (06).

Comparison is done on canonical paths **and** on file identity (device + inode) of the item and each of its ancestors, so an alias spelling (firmlink, case) cannot slip past.

## Order per item (immediately before each move)

1. For a duplicate: the copy that is kept must still exist **unchanged** (same identity, size, modification time). Otherwise skip: "The copy you are keeping changed or is gone".
2. **Re-measure** the item (sizes from the scan are never reused). A symbolic link is never followed. A folder with a protected item anywhere inside (photo library, keychain, backup) is refused.
2a. **Empty-folder rows only** (`08`): if the re-measured tree is no longer empty (anything but folders and `.DS_Store`, or an unreadable entry), skip "No longer empty: scan again". This runs after re-measure and before the gate.
3. **Gate**: canonicalize; must not be a symbolic link; inside an allowed root; outside and not containing any never-touch root; identity (device, inode, size, modification time) equal to the scan record, otherwise skip "Changed since the scan".
   Windows note (measured 2026-10-18, the test-machine notes): the scan reads a child's identity from its parent folder's listing, and the gate reads the item itself. NTFS updates a folder's entry in its parent's listing a few seconds after the folder's own record. So a folder whose contents changed within a few seconds before the scan read it can be refused as "Changed since the scan" though nothing changed after the scan. This refusal is safe, and a new scan clears it. The tests wait for the listing to settle (`RealGateRefusalTests`).
4. **In use**:
   - a readable process has its executable, working directory or a command-line argument inside the item (relative arguments resolved against its working directory);
   - a readable process has a file inside the item open;
   - a readable process has a file inside the item mapped into memory (one snapshot per session, refreshed when older than 10 s).
   Matches are by identity, not path text. In use → skip with the process names and ids. Processes that cannot be read are reported as information ("could not check N system processes") and do not block.
5. **Journal check**: the journal must accept a write right now; otherwise nothing more is moved in this session.
6. **Move to Trash**, using the exact real path the gate approved.
7. **Post-verify**: the item now in the Trash has the approved identity and size. A mismatch is journaled as "The moved item did not match the approved item — check the Trash" and stops the session.
8. **Journal** the outcome (moved or skipped with reason). Every item produces exactly one entry. If that write fails after the move, the session stops and the report names where the item went.

A file with more than one hard link is noted "Frees nothing while other links exist".

Closing the window (Windows, ASP-24): a close never ends Lean between an item's move and its journal entry. While a session runs, a close is held until the session ends (owner decision 2026-10-23): every approved item goes through steps 1 to 8 as it would have, each with its entry (a session that stops by itself, or Cancel pressed meanwhile, ends it as usual); the status line says so (07) and the window closes by itself when the session has ended, without asking again. A close while only the confirmation is open (or being prepared) ends it as Cancel: nothing moves, and the window closes. Ending the process from outside (Task Manager, a sign-out or shutdown, a crash, a power loss) is not covered by this hold.

Keyboard focus after the confirmation (Windows, ASP-22): the confirmation's default button is Cancel, so Enter moves nothing. When it closes without moving (Cancel, Esc, or a close of the window that is held), the keyboard focus returns to the control that was focused when Move was chosen; when that control is gone (a row menu that has closed) or off, to the Move button of the view the move was started from. The focus is never left on nothing.

Windows note on steps 6–7: one shell move per item, which is aborted whenever any pre-delete callback (the item's or a descendant's) does not say "recycle if possible". Post-verify has four outcomes: the item the shell reports in the Recycle Bin has the approved volume serial, file id and logical size → `trashed`; it has the approved volume serial and file id but another logical size → `trashed` with step 7's reason, "The moved item did not match the approved item — check the Recycle Bin", journaled where it is with the size it has there (so Put back returns it as it is), and the session stops (owner decision 2026-10-17: the item is in the Recycle Bin, so it is not called deleted); the original is still in place with the approved identity → `skipped` with its cause (in use, or changed since the scan); anything else → `lost` (05), shown in the report, and the session stops.

## Selection rules

- Nothing destructive is ever pre-selected, except the app bundle itself in an uninstall the user started.
- Only items the scanner or a module produced can be selected; the UI passes ids, never paths.
- No update ever enables a destructive setting.

## Permissions

- Full Disk Access is required before scanning (see 06 for the onboarding copy).
- Lean never runs with administrator privileges, never asks for a password, never uses a privileged helper, never kills a process. Quitting an app is the same as the user choosing Quit, after confirmation.
- Windows (ASP-20): the only program Lean starts is a tool's own documented cache-clean command, or its read-only query for the cache folder (05, Command log). Never as administrator, as SYSTEM or outside the user's desktop session, never through a shell, and never ended by Lean.

### Windows: verified app versions (ASP-30)

The Installers module adds only exact numeric CapCut version leaves under the user's LocalAppData/CapCut/Apps, produced by its version recognizer. Its allowed-root decision is conclusive: it never falls through to Map's general roots. Before each item the gate re-reads the exact per-user registration, bounded ProductInfo/Configure metadata, all retained version references and process inventory. Current, newest and rollback aliases, ambiguous or unreadable evidence, running apps, redirected/cloud ancestry and protected data refuse the action. Existing whole-tree identity/stamp, in-use, journal, Recycle Bin and Put back rules still decide the operation. The display never grants an AppData parent or a guide-only updater/Codex/Lark row authority. The exact evidence schema and tests are frozen in `intent/2026-10-24-asp-30-developer-space.md`.
