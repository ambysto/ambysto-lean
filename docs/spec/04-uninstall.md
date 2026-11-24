# 04 — Uninstall with leftovers

## Inventory

Apps in `/Applications` and `~/Applications`, plus one level of subfolders. For each: name, identifier (bundle id), version, size, last used, and a class:

| Class | When | Shown as |
|---|---|---|
| removable | the user can write the bundle and its parent | selectable |
| needs admin | owned by another user (e.g. installed by a system installer) or not writable | "Needs admin: remove in Finder", with size; never selectable; Lean never asks for a password |
| system | a link into the system volume, or protected by the OS | under "Apple system apps"; never selectable |
| unsupported | iPhone/iPad apps installed on a Mac (no `Contents/Info.plist`) | "Remove in Finder" |

Windows note: the inventory is the list of registered uninstallers (`Uninstall` keys in both hives and both registry views); entries marked as system components, and entries without a display name (Windows does not list them either), are hidden; per-machine entries are "needs admin" (Windows Installer lists a product installed for the current user only under the machine hive too; it is per-user, by its per-user installer record); Store (MSIX) apps are listed display-only. **Lean never runs an uninstaller** (owner decision 2026-10-16): "Uninstall…" opens Settings > Apps and names the entry to remove. An app counts as **gone** when its entry has been absent from both hives and both views, and no Store package and no other entry (hidden ones included: system components and entries without a display name) names the same install location or display name, for 10 s (checked every 2 s), and no process runs from its install location. An empty install location matches nothing; locations compare as paths (case-insensitive, a process anywhere below the location counts). Only then are its leftovers offered, nothing pre-selected.

Windows note (ASP-16): the inventory is a table, one line per app, with the columns Name, Publisher, Version, Size, Installed on and Last updated (each sorts from its header; a missing value is an empty cell and sorts after every real value in both directions). One row can be selected (a click or the arrow keys); "Uninstall…" works on the selected row. The class note ("Needs admin: remove in Settings > Apps", "Store app: remove in Settings > Apps") is not in the row, so the whole name shows: it is the row's tooltip (with the name) and part of its automation name, and it is shown next to the "Uninstall…" button for the selected app (alone for a Store app, which has no button; empty when nothing is selected or the app has no note). Every text cell trims with an ellipsis and carries its full text as its tooltip. Where each value comes from:
- Size is the entry's `EstimatedSize` DWORD (read as unsigned, in KB) × 1024; empty when the value is missing, not a DWORD or 0. A Store app has no size: Lean does not measure package folders.
- Installed on is the entry's `InstallDate`, exactly 8 digits `yyyyMMdd`; empty when it is anything else, not a real date, before 1980 or more than 1 day after today (an installer's wrong clock). For a Store app it is `Package.InstalledDate`, as a date in local time; the same bounds apply (between 1980-01-01 and tomorrow), otherwise it is empty.
- Last updated is the time the uninstall key was last written (`RegQueryInfoKey` on the opened key); empty when it cannot be read. A Store app has none.
- A value that cannot be read leaves only its cell empty, never the app out of the list.
- A magnifier (Show in File Explorer) on a row's hover and focus opens the install folder itself, showing its contents. It is there only when `InstallLocation` is a fully qualified local path to a folder that exists, checked when the row is built: never a drive-relative path (`C:foo`, `\foo`), never a UNC path in any spelling (`\\server\share`, `\\?\UNC\…`, `\\.\UNC\…`), never a drive letter mapped to a network share, and never a Store app. A folder that is itself a symlink or junction counts by its target, read from the link without following it. A network location is never probed, so no network traffic happens and nothing stalls when the view opens. The existence checks run off the UI thread, at most 8 at a time and within 1.5 seconds for the whole list: a check not started by then is never started, and a folder that has not answered gets no magnifier. If File Explorer cannot open the folder, the status line says so.
- The leftovers list has a Select all that ticks only the leftovers that can be selected; it is off while a move or a scan runs or a move is being confirmed. Nothing is ticked to start.

## Leftover locations (macOS)

Direct children of these folders under `~/Library` only:
`Application Support`, `Caches`, `Preferences`, `Preferences/ByHost`, `Containers`, `Group Containers`, `Saved Application State`, `Logs`, `HTTPStorages`, `WebKit`, `Cookies`, `LaunchAgents`, `Application Scripts`.

Windows note: in `%APPDATA%`, `%LOCALAPPDATA%` and `%LOCALAPPDATA%\Programs` only: the app's exact install location when it is at least one component inside one of these folders (never one of these folders itself, and never `%USERPROFILE%` itself; an install location anywhere else, such as `Program Files`, is never a leftover; so an app installed directly at `%USERPROFILE%\<App>` is never moved, since that folder is not two components below the profile either), `<Publisher>\<DisplayName>` and `<DisplayName>` exactly. A display name that is a generic folder name (`Temp`, `Programs`, `Packages`, `Microsoft`, …) is never matched; a vendor folder another installed app may use is never offered; a candidate two installed or just-removed entries could own belongs to nobody (rule 4). The registry is never touched.

## Match rules (exact, never substring)

1. A child whose name, without its extension, **equals** the bundle id, or **starts with the bundle id followed by a dot** (so `com.foo.bar` never matches `com.foo.barista`).
2. In `Application Support`, `Caches` and `Logs` only: a child named **exactly** the app's name.
3. In `Group Containers` only: a child named exactly one of the **app-group ids listed in the app's signed entitlements**. A group id that another installed app also lists is not offered. **Never match by team-id prefix.**
4. A candidate belongs to the installed app that matches it **most specifically** (exact bundle id > longer bundle-id prefix > app name). A tie (two copies of one app, an app group several apps list) means nobody owns it, and it is never offered.
5. Never-touch (02) always wins.
6. System-wide leftovers (`/Library/…`) are listed as "needs admin", never selectable.

7. Apple system apps get no leftover search at all.
8. Leftovers of an app that stays installed (needs admin, unsupported) are shown but not selectable: "Remove the app in Finder first". Removing them would only reset the app.
9. An app group whose id does not contain the app's bundle id carries the note "App group: other apps from this developer may use it".

## Plan and run

- The app bundle is pre-selected; leftovers are not.
- While any process runs from inside the bundle, the whole uninstall is blocked: "Quit <App> first". A **Quit** button sends the normal quit request after confirmation; there is no force quit.
- A login agent file that is moved carries the note "Stops at next login".
- Everything goes through the safety order in 02 and is journaled as one session, so **Put back all from this session** restores the app and its leftovers together.

Windows note: there is no app to move, so nothing is pre-selected and there is no Quit button; the app's own uninstaller (started from Settings) removes the app, and "app is gone" waits until no process runs from its install location. A per-machine ("needs admin") entry is uninstalled the same way; once it is gone, its leftovers in the per-user folders above are offered like any other. **Put back all from this session** restores the leftovers; the app comes back only by running its installer again.

### Windows: results after removing another application (ASP-28)

After the existing Settings handoff and ten-second app-gone confirmation, show one informational result naming the removed app. Distinguish no leftovers, locations found for review, and a failed search; discovering locations does not move them. Details exposes the original unticked list. After approved leftover movement, report actual approved, verified-and-journaled successful, attempted unsuccessful and unattempted counts. Preserve warnings and lost/un-journaled outcomes in the existing report. Review Put back navigates to its existing session action and never restores the removed application itself. Selection/view changes and the initial close request invalidate stale notices without cancelling already approved work. The exact count partition and keyboard contract are in `intent/2026-10-24-asp-28-uninstall-results.md`.
