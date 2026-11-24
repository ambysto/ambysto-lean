# 03 — Large and old files, duplicates

## Large and old files

- Source: the scanned tree.
- Defaults: size ≥ 100 MB, last used more than 12 months ago. Both adjustable. Size is the allocated size (01, walk rule 3), the number the map shows.
- **Last used**: the system's "last used" date when the file is indexed (macOS note: Spotlight `kMDItemLastUsedDate`). Otherwise the modification date. Each row says which one it shows ("last opened" vs "last modified").
- Type groups: video, disk image, archive, document, application, virtual machine image (`.vmdk`, `.qcow2`, `.utm`, `.vdi`, `Docker.raw`), other.
- Each row: name, path, size, last used + source, type, and **Open by <process>** when a process has it open (from one snapshot when the list is shown; refreshable).
- Rows outside the allowed roots (02) are display-only with the where-to-manage hint (01). They are hidden by default and shown with "Show system items".
- Nothing is pre-selected.

Windows notes: "last used" is always the modification date, never the last-access time (last-access times change without a user action, measured 2026-10-16), and every row says "last modified"; virtual machine images also include `.vhdx`, `.avhdx`, `.vhd` and `.ova`; `.dll` is "other", not an application.

## Temporary files (Windows, owner decision 2026-10-18)

- Lists the direct children of `%LOCALAPPDATA%\Temp` and `%LOCALAPPDATA%\CrashDumps` whose newest last-write time, over the child's whole tree, is older than the chosen age: 1 week (default), 1 month or 1 quarter (3 months).
- Each row: name, "about <size>" (allocated size, from the walk), last changed. The total reads "About <size> could be moved to the Recycle Bin (estimate)"; nothing ever says space is freed.
- **Nothing is selected.** A row holding a link, a cloud file or something unreadable is display-only: "Holds a link, a cloud file or something Lean cannot read: Lean does not move it"; a row inside one of the user's protected folders is display-only: "Protected by you"; `C:\Windows\Temp` is one display-only row, "System-wide: needs an administrator; Lean does not change it".
- Moves go through 02's order like every other move (module `temp`); both folders are already allowed by root (a)'s literal exceptions. A file a program holds open is skipped at move time with its holders (02 step 4).

## Duplicates

**Identity rule: byte-for-byte equal.** A hash or a size is never proof on its own. A read error means "not identical".

Candidates: regular files in the home-item root (02), at least 1 MB (adjustable), excluding:
- anything inside `~/Library`;
- hidden folders and their contents (names starting with `.`, e.g. `.git`), and `node_modules`; Windows note: also folders with the hidden attribute, and anything inside `AppData`;
- the inside of packages (Windows note: Windows has no packages, so a folder named like one, such as `Tool.app`, is searched);
- cloud placeholders;
- hard links counted elsewhere (two links to one file are not duplicates).

Steps:
1. group by logical length;
2. within a group, compare the first 4 KB and the last 4 KB;
3. within a matching set, compare every byte in 1 MB chunks.

Each group shows every copy with path, size and last used.
- **Suggested keep**: newest modification; ties → shortest path; then alphabetical. Shown as a badge; **nothing is pre-selected**.
- The user can never tick every copy of a group: the last unticked copy cannot be ticked.
- The confirmation lists "Keeping: <path>" per group.
- Files that may share storage with another file (copy-on-write clones) are labelled "May share storage; moving it may free less". macOS note: extended flag "may share blocks". Windows note: every file on a volume that reports block reference counting (`FILE_SUPPORTS_BLOCK_REFCOUNTING`; ReFS and Dev Drives report it, per Microsoft's documentation, not measured here); a volume that does not report it, or that cannot be asked, has no shared blocks (measured 2026-10-16: the NTFS, FAT32 and exFAT test volumes do not report it).
- Reclaimable total is an estimate and is labelled so: the allocated bytes of every copy other than the suggested keep, as scanned. Windows note (2026-10-18): a copy Lean may not move (for example inside one of the user's protected folders: "Protected by you", 02) stays in its group, display-only with its hint, cannot be ticked, and is not counted in the estimate.

Windows notes, helpers (ASP-15, owner decisions 2026-10-18 and 2026-10-19):
- **Type tabs**: above the list, "All files" and then the seven type groups of 01, each with its number of groups; one is current, "All files" at the start. A group belongs to the tab of its files' type group: the type group of its suggested copy; when that is "other", of the first copy (in the list's order) whose type is not "other"; else "other". The tabs only filter what is shown: Move takes every ticked copy in every tab, and its confirmation lists every ticked copy with its group's kept copy.
- **Select by rule**: a drop-down beside Move; nothing is ticked until the user picks an entry. The rule sets the Keep choice of every group of the current tab and ticks the other copies; a protected (display-only) copy is never ticked, and may be the kept copy. The rules: newest modified; oldest modified; shortest path; inside a folder the user picks (the newest copy inside it; a group with no copy inside is left as it is). Ties and the last-access time: the Core's `DuplicateRules`. A rule that would tick more than 100 copies first asks, saying how many copies in how many groups and how many of them are program files (`.dll`, `.exe`, `.sys`, `.ocx`, `.drv`, `.cpl`, `.pyd`, `.so`, `.node`, `.jar`, `.winmd`; that part is left out when there are none), with **Tick** and **Cancel**; the count is the copies the rule would tick (no hint, not the kept one), worked out before anything is ticked, and Cancel changes no tick. A line reports what was ticked. **Clear selection** unticks every group of the current tab and leaves each Keep choice.
- **Preview**: an eye button on the row of a picture (`.jpg`, `.jpeg`, `.png`, `.gif`, `.bmp`, `.webp`, `.tif`, `.tiff`) shows the picture (at most 320 px wide), its size in pixels and its path in a flyout. The file is read by the Core's guard (`PicturePreview`): an online-only file is never opened and says so; a file that cannot be decoded says so. The shell's thumbnail provider is never used.
- **Rows**: every row is one line high; a row shows "Show in File Explorer" (a magnifier) on hover and focus, and on a picture row the eye beside it. Alternate groups sit on the panel and the ground colours so that they read apart.
- **Select all** (the largest files, Large and old, Temporary files): a three-state tick box in the Move header that ticks every row of the list as shown that can be moved, never a display-only row, and unticks them all when every such row is ticked. Nothing is ticked before it is used, it is off while a scan or a move runs, and Move still confirms the whole list. Duplicates has none: Select by rule does that job.

## Suggest (Windows, ASP-17, owner decisions 2026-10-19 and 2026-10-20; Pro, 06)

A **Suggest** button sits in the row of the Move button, after it, in Temporary files, Large and old and Duplicates (in Duplicates, after Select by rule). It **ticks what Lean suggests and says why**; it never moves anything, nothing is ticked until it is pressed, and Move still confirms every ticked item. The Core's rules read only (`Suggest`), judge at a given "now", and never suggest: a folder (Large and old suggests files only), a link, an unreadable entry, a cloud file that is not on this disk, a hard link counted at another path, a row that is display-only or has a hint (protected by you, the system, other users, sync roots and the like).
- **Temporary files**: Suggest first unticks every row, then ticks the rows (a folder row too) whose newest write is before one month ago, whatever age the view shows (the view keeps its 1-week default). A **Why** column (240 wide) reads "Unchanged since <date>" on those rows and is empty on the others. While the reasons show, every row is one line: each text cell is trimmed with an ellipsis and its full text is its tooltip; the columns are Move, Name (the rest), Size 90, Last changed 110, Note 200 and Why 240. The status line reads "Suggest ticked <n> items", and goes back to what it read before when the ticks go (a new reading of the list, a change of the age, a new scan; ASP-22).
- **Large and old**: Suggest replaces the list with **its own list**: every suggested file of the whole scan, ticked, with the Why column; largest first, then by path. A file is suggested when it is (1) a setup file or disk image (`.exe .msi .msix .msixbundle .appx .appxbundle .iso .img .zip .7z .rar`, any case) strictly inside the Downloads known folder and last written more than 90 days ago: "Old download, unchanged since <date>"; or (2) 1 GB (1 GiB) or more allocated and last written more than 365 days ago: "1 GB or more, unchanged since <date>". A file that is both is listed once, as the first. The list has the columns Move, Name 190, Folder 224, Size 90, Last modified 110 and Why (no Type: the date and the kind are in Why), every row one line with the full text of a cell as its tooltip. ASP-22: each column but Why is as wide as its cell needs: a name of up to 22 characters, the folder shortened in the middle to 26 characters (the full path is the tooltip), a size, a date (JetBrains Mono, 14 DIP, 8.4 px a character). Why alone takes what is left and alone gives way: down to 40 px, trimmed, its tooltip the full text, when the window is narrow, so the date and the size are never cut (at a window 1280 DIP wide every other column is shown in full; all sizes here are DIP). The size, age and "Show system items" filters are off while this list shows; **Clear suggestion** brings back the filtered list with nothing ticked. The status line reads "Suggested <n> files, <size>"; Clear suggestion puts the status line back to what it read before Suggest (ASP-22; the same line is put back when a new scan or a move ends the suggestion, unless another action has written the line since). With nothing to suggest the status line reads "Nothing to suggest in this scan" and the list stays as it was. The list stays while the user looks at another view and comes back, and rows that were moved leave it.
- **Duplicates**: Suggest is the **Newest modified** rule on the current tab, exactly as Select by rule does it (same Keep choices, same display-only copies, same question over 100). Each copy it ticked reads "Older copy; the newest is kept" in the Why column (280 wide; while it shows, the Suggested badge column gives way to it and the Folder column is narrower; rows stay one line). The status line reads "Suggest ticked <n> items".
- **Over 100**: when Suggest would tick more than 100 items in a view it asks first, "Suggest ticks <n> items, <p> of them program files (.dll, .exe and similar). Tick them?" (without the program-file part when there are none; Duplicates keeps the rule's own question), with **Tick** and **Cancel**. The counts are worked out before anything is ticked, a program file is counted by the row's extension (ASP-15's list; a folder never is), and Cancel changes nothing: no tick, no list, no column, no status line.
- **When the Why column goes**: a new scan, a new search, a change of the age or of a filter, Clear selection (Duplicates), Clear suggestion, a rule picked in Select by rule, and a new reading of the Temporary files list. In Duplicates a reason shows only while its copy stays ticked, and goes with the group when the Keep choice is changed.
- **Busy**: Suggest is off whenever Select all is: while a scan, a duplicates search, an export, the root check or a move runs, and while a move is being confirmed.
