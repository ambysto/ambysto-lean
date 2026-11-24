# 08 — Empty folders and broken shortcuts

Two lists from the existing scan. They reclaim almost no disk space; the value is a home folder you can read. Every move uses the same path as any other list: confirm → session → Trasher → journal → Put back (`02`, `05`). The gate, never-touch list and Put back do not change.

Copy: **"Empty folders"** and **"Broken shortcuts"**. Rows never claim disk space. After a move: "Moved to Trash: \<size\> (measured just now)." Estimates on the list are labelled "about".

## Empty folders

A folder is **empty** when its whole subtree holds nothing but folders and `.DS_Store` files: no other file, no symbolic link, no cloud placeholder, no mount point and **no unreadable entry** (01, rule 7). The folder itself must be readable, not a cloud placeholder and not a mount point. An `Icon\r` or `.localized` file is an "other file", so that folder is never empty.

Windows note: `Thumbs.db` and `desktop.ini` count like `.DS_Store`.

Lean shows only the **topmost** such folder inside the home-item root as one row. A folder directly in home is too close to the home folder for the gate (`02`), so its empty children are the rows. Nested empty folders under a listed row are counted on that row ("holds 4 empty folders"), not listed separately.

**Emptiness is checked again at move time**, after the ordinary re-measure and before the gate. Trasher refuses an empty-folder row when that re-measure finds anything that breaks the definition, including an unreadable entry: "No longer empty: scan again". The short window between re-measure and the move is the same window every v1 row has.

### Candidates and exclusions

Folders in the home-item allowed root (`02`), excluding:

- anything inside `~/Library`, including `Caches` and `Logs`;
- hidden folders and their contents, `node_modules`, and the inside of packages and bundles (same as duplicates, `03`);
- anything inside a **version-controlled working tree** (an ancestor holds a child named `.git`, `.hg` or `.svn`, folder or file), and any folder that holds such a working tree at any depth: it is never a row, while its other empty children can be;
- folders the user tagged in Finder, or that have a custom icon;
- folders inside a **cloud-sync root** (moving one would sync the removal):
  - a folder directly in home named `Dropbox`, `Google Drive`, `My Drive`, `OneDrive`, `Box`, `Box Sync` or `pCloud Drive`, or whose name starts with `Dropbox (` or `OneDrive - `;
  - any folder in home that holds a `.dropbox` file or a `.dropbox.cache` folder;
  - `~/Library/CloudStorage` and `~/Library/Mobile Documents` are already never-touch and inside `~/Library`;
  - iCloud Desktop & Documents: `~/Desktop` and `~/Documents` count as cloud-sync roots when `~/Library/Mobile Documents/com~apple~CloudDocs/Desktop` (or `…/Documents`) exists;
- folders created or modified in the last **30 days** (the later of birth time and modification time), adjustable;
- `~/Public` and everything inside it.

Windows note: whether `%APPDATA%\Microsoft\Windows\Start Menu` belongs in Windows' allowed roots is decided in Lean's Windows intent.

Each row: path, number of empty folders inside, last modified, and the size the scan measured, labelled "about". Nothing is pre-selected. The UI passes ids, never paths.

## Broken shortcuts

A **shortcut** on Mac is a Finder alias file. The walk records which regular files carry Finder's alias flag (`01`); it does not resolve them during the walk.

It is **broken** when resolving it says the target is not found **and** the target's volume is mounted right now: the volume UUID the bookmark records equals the UUID of a volume mounted now. A bookmark that records no volume UUID is shown as "Cannot tell where it pointed", display-only, never broken.

**Resolution never mounts and never asks.** It does not show UI and does not search.

Windows note: a `.lnk` is the shortcut there (`IShellLink` resolve with no UI, no search and no link tracking; the `.lnk` is never rewritten). A `.lnk` found on a Mac is not a shortcut there and is not listed.

| Outcome | Row |
|---|---|
| Target missing, volume mounted | selectable |
| Target missing, volume not mounted | display-only "On a disk that is not connected: \<volume name\>" |
| No volume UUID | display-only "Cannot tell where it pointed" |
| Target in the Trash | selectable, "The original is in the Trash" |
| Target exists | not listed |

Candidates: alias files the scan finds in the home-item root. The empty-folder exclusions apply, except the 30-day rule and the rule that a folder holding a working tree is never a row (an alias beside a working tree is still listed; one inside it is not).

Each selectable row: the shortcut's path, where it pointed (last known path), last modified.

### Shown, never moved

- **Broken symbolic links.** The gate refuses symbolic links. Listed display-only with "Remove it in Finder".
- **Dock items whose app is gone.** Source: the Dock's `persistent-apps` list, read only. Only an entry whose path is inside home or `/Applications` is checked, with `lstat` on each component from the top down, stopping with "Not checked" at the first symbolic link. Any other path is "Not checked: on another disk". Hint: "Remove it from the Dock: Control-click it, then Options → Remove from Dock". Tune owns preference changes.
- **Login items whose program is gone.** Source: property lists in `~/Library/LaunchAgents` whose program path no longer exists, same path rule. Hint: Ambysto Tune when installed, otherwise System Settings → General → Login Items. The system's Background Task Management list is not read (needs a privileged tool; Lean runs none).

Windows note: Start menu entries for removed apps belong to the Windows build.

## Journal

Two `module` values: `emptyFolders` and `brokenShortcuts`. Put back is unchanged. An older build skips journal lines with a `module` it does not know; those entries leave Lean's Put back list after a downgrade (the items stay in the Trash; Finder's Put Back still works). See `05`.

## Free and Pro

Both lists, the move to the Trash, and Put back are Free. Pro includes them in the existing export (`06`).
