# 01 — Measure and map

Platform-neutral. macOS specifics are in "macOS note" lines; Windows equivalents in "Windows note" lines.

## Volume card (shown at launch, before any scan, needs no extra permission)

| Label (exact copy) | Meaning | Source |
|---|---|---|
| **Used by this volume** | Bytes this volume uses | macOS note: `getattrlist(ATTR_VOL_SPACEUSED)`; equals `df` "Used". Windows note: total − free of the volume. |
| **Free right now** | Bytes free without macOS deleting anything | macOS note: `statfs` `f_bavail × f_bsize` (same as `df`). Windows note: `GetDiskFreeSpaceEx` free bytes available to caller. |
| **Available (as Finder shows)** | Free + space the system can reclaim on its own | macOS note: volume "available capacity for important usage". Windows note: same as Free. |
| **macOS can free on its own** | Available − Free (purgeable: caches, local snapshots, iCloud copies) | derived; Windows note: not shown. |
| **Other macOS volumes** | Container total − free − used (System, Preboot, VM share the free pool) | macOS only; Windows note: not shown. |
| **In the Trash** | Size of the user's Trash | measured with the walker; button **Open Trash** opens it and never empties it. Windows note: this user's Recycle Bin on the profile's volume (`SHQueryRecycleBin`, logical sizes), labelled "In the Recycle Bin" with **Open Recycle Bin**. |

Windows note: the card shows the volume that holds the user's profile. "Available (as Finder shows)", "macOS can free on its own" and "Other macOS volumes" are not shown.

Copy rules: the purgeable line always carries "macOS frees this itself when space is needed". It is never added to anything the app calls reclaimable.

## Walk rules

1. Stay on one volume. Never cross a mount point.
2. Never follow symbolic links. A link costs its own size only.
3. Size = **allocated** bytes on disk (blocks × block size), not the logical length.
4. **Hard links**: a file with more than one link is counted once. Its size belongs to the path that sorts first (byte-wise) among the links found; every other link shows size 0 and is flagged "hard link (counted elsewhere)". Windows note: "byte-wise" compares full paths ordinally (UTF-16 code units).
5. **Packages** (folders the OS presents as one item, such as apps and photo libraries) are walked for size and shown as one tile; Duplicates never looks inside them.
6. **Cloud placeholders** (files whose content is not on this disk) are never downloaded, never entered and never compared. They are counted and shown as "N cloud files not downloaded". macOS note: the scanning thread disables materialization of dataless files; entries flagged dataless are skipped. Windows note: see (6) below.
7. **Unreadable entries** (permission denied, protected by the OS) are counted. They are never treated as empty and never silently skipped.
8. Dates are stored as signed minutes since 1970-01-01 UTC; files dated 1970 or before 2001 exist and must not break anything.
9. **Finder aliases.** The walk records which regular files carry Finder's alias flag. It does not resolve them. A symbolic link is never recorded as an alias. See `08`.

Windows notes on the walk rules: (1) the volume is the profile's; a mount point or junction is never entered. (2) "Link" means a name-surrogate reparse point: symbolic link, junction, mount point. Other reparse points are files, not links: compressed (WOF) and deduplicated files are measured like any file; cloud files follow (6). (3) Allocated size is what the file system reports per file (compressed, sparse and WOF files count what they use). Measured 2026-10-17: a folder listing reports 0 allocated bytes for compressed (WOF) files, and a standard user does not see their reparse tag. So a file listed with 0 allocated bytes but too large to live inside its file record (more than 1 KiB) has its allocated size asked for once, by path and for attributes only, never for a cloud file. An all-sparse file then still counts 0. (4) Hard links are recognised by file id: a file id seen twice is counted once. Each name has its own folder entry and NTFS updates only the entry of the name the file was written through, so when the names of one file report different sizes or last-write times, that one file is opened once, for attributes only (never a cloud file), and its own answer is used; names that agree are trusted as listed (owner decision 2026-10-17: a file written through a name outside the scanned tree may show a stale size, and the move-time stamp check then refuses it as changed). (5) Windows has no packages. (6) A file with the recall-on-data-access or recall-on-open attribute is counted as a cloud file and never opened. (7) Alternate data streams are not measured and go to "Not measured". Entries are read in batches per folder (`FileIdExtdDirectoryInfo`), never by opening each file.

macOS note: the whole volume is walked at `/System/Volumes/Data`. Paths are converted to their firmlinked form (`/Users/…`, `/Applications/…`) using the system firmlink table before display or any action.

## Coverage

```
measured + not measured = used by this volume      (exactly)
not measured = used − measured                      (never negative; if negative, show "measured more than used" with both numbers)
```

"Not measured" lists: unreadable entries (count), cloud files not downloaded (count), and the remainder labelled "snapshots, system and hidden data". Windows note: the remainder is labelled "restore points, NTFS metadata, alternate data streams and other users' folders" (the page, swap and hibernation files are listed and measured like any file, measured 2026-10-17) (alternate data streams are not counted: counting them would mean opening each file).

## Progress and cancel

- Results appear progressively: top-level folder totals refresh at most 4 times per second from the first second.
- The map is drawn during the scan from the top-level zone totals (the root's direct folders), at most 4 times per second. A zone is filled with its type-group colour when the walk has read its last subfolder; a zone still being read is drawn hatched, with the bytes measured so far; a zone not reached yet has no tile. The map is display-only until the scan ends: a click does nothing and no move is offered. The final map is drawn from the final tree.
- Cancel stops every worker within 1 second, including inside a folder with hundreds of thousands of entries.
- After cancel the map shows what was measured so far, labelled "Scan cancelled — partial".
- Windows note (owner decision 2026-10-18, ASP-14): the first screen is a start screen with a Scan button and a card for every fixed drive (not removable, network, CD or RAM drives), read with `GetDiskFreeSpaceEx` off the UI thread (a drive that does not answer within 2 s is left out). Only the drive that holds the user's profile can be scanned in v1; every other card is disabled and says that scanning it comes after v1. A folder on that drive can be scanned with the same rules (**Scan a folder**); a folder on another drive is refused with the same words and no scan starts. A folder scan has no **Not measured** tile (it is given at volume level only).
- Windows note (ASP-22, as Steady): the main window has a least size of **560 x 560 DIP**, multiplied by the window's DPI scale (pixels = DIP x DPI / 96), so it is the same physical size at 100 %, 125 % and 150 % and follows the window to another monitor; it is the outer size of the window, and the sidebar (220) and one whole drive card (250) fit at it. The least size never exceeds the work area of the monitor the window is on (the screen less the taskbar), so a small screen at a high scale does not push the window under the taskbar. The default size is unchanged. Every width in these notes is DIP.
- Windows note (owner decision 2026-10-20, ASP-18): each card is one tick box whose content is the card (one tab stop; Space or a click ticks or unticks it; its name is the drive name; the tick is drawn in the card's top-right corner in the card's own style, with system colours in High Contrast). A click or Space on a card only ticks or unticks it and never starts a scan; the **Scan** circle scans the ticked drives, and **Scan again** keeps its ASP-14 behaviour (it is not tied to the ticks). The profile drive's card is ticked at every launch (the tick is not saved); every other card is disabled and unticked. With no drive ticked **Scan** is disabled and the line under it reads the whole line "Tick a drive to scan, or scan a folder" ("scan a folder" still the link); **Scan a folder** works whatever is ticked. The tick box follows the same busy rules as the card did: off while a scan, a move or the root check runs. The top band of the volume card ("Used by this volume", "Free right now", "In the Recycle Bin" and the Open Recycle Bin button) has a 1 px divider, about the text's height, centred in the gap between its items, in the hairline colour (the system's disabled text colour in High Contrast); where the band wraps, an item that starts a line has no divider before it, so none is at the start or the end of a line; the dividers are decoration and not part of the accessibility tree. The cards sit side by side while they fit and **wrap onto a new line** when the window is too narrow for them (ASP-22: 250 px wide, 12 px apart, centred); a card is never cut at the right edge down to the window's least width (below), and if the start screen is still too high for the window it scrolls.
- Windows note (owner decision 2026-10-18, ASP-14): while the scan runs a terminal readout sits above the map: the command being run, the folder being read, a 40-cell meter lit up to the percent measured of what the drive uses (a folder scan has no total, so a block of six lit cells moves along it), and the last six zones, each "done" or "reading". The progress line for screen readers is a polite live region, updated at most once a second. Any glow, sweep or scanline effect is off unless the build switch is on, and is never shown with High Contrast or with Windows' animation effects off.
- Windows note (owner decision 2026-10-18, ASP-14): once a scan is done the first screen is a results view: a column of type groups (All files and the seven groups, each with the bytes the scan measured for it, and the volume's free space), the map with the tiles of the other groups dimmed when a group is chosen, and under it the 500 largest files of the chosen group with a tick box per row (nothing ticked) and Move to Recycle Bin…. A row shows two icon buttons while the pointer is over it or it has focus: Show in File Explorer and Move to Recycle Bin… for that row alone; both are also in the row's menu. Large and old stays a view of its own.
- Windows note (owner decision 2026-10-18): when a scan ends or is cancelled, the status line reports what it read: "Scanned <files> files in <folders> folders in <time>; <n> could not be read" (the last part only when n > 0). Nothing is stored.

## Map

- Squarified treemap. Area is proportional to allocated size.
- Shows the current folder, up to 3 levels deep. Children too small to draw merge into one "smaller items" tile.
- At volume level, one extra tile: **Not measured** (hatched), sized by `used − measured`. Its readout names what it holds: snapshots, system and hidden data, and the count of items that could not be read (those have no known size, so they cannot have an area of their own).
- Colour by file-type group (video, disk image, archive, document, application, virtual machine image, other). A folder drawn as a single tile takes the colour of the file reached by following its largest child. Folders drawn with their children inside are a light neutral tint. No red for ordinary files. A legend sits under the map.
- Hover shows path, size and percentage of parent. Click drills down; breadcrumbs go back.
- Actions on a tile: **Reveal in Finder** (Windows: Explorer) and **Move to Trash…** The latter is disabled for display-only tiles, with the reason and where to manage them:

| Location | Hint |
|---|---|
| iPhone/iPad backups | "Manage in Finder → your device → Manage Backups" |
| System-wide locations (`/Library`, `/private`, …) | "System-wide: needs an administrator; Lean does not change it" |
| Docker disk image | "Manage in Docker Desktop → Settings → Resources" |
| Developer caches | "Use Ambysto Optimer for developer caches" |
| Anything else outside the allowed roots | "Outside the folders Lean may change" |

Windows notes on hints: device backups "Manage in Apple Devices or iTunes"; `Program Files`, `ProgramData`, other users' folders "System-wide: needs an administrator; Lean does not change it"; Docker and WSL disks (`*.vhdx` inside `AppData`) "Manage in Docker Desktop or with wsl.exe"; a cloud-synced folder "Synced by <provider>: manage it there". **Reveal in File Explorer** replaces Reveal in Finder; **Move to Recycle Bin…** replaces Move to Trash….

Windows note on colour (Map): the map is drawn on the dark frame. A folder drawn with its children inside is a dark neutral (the frame's panel colour), not a light tint. The seven groups use the brand's tones, without the app's own tone. When Windows High Contrast is on, every tile is drawn in the system's window colour with the window text colour as border and label; the group is still named in the tile's name and in the legend.

- After a deletion session, trashed items are removed from the tree and ancestors re-totalled, so no stale tile remains.
