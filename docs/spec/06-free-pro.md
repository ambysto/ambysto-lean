# 06 — Free and Pro, permissions onboarding

Model: like WinRAR. Free is fully usable forever. Product id: `space`.

## Feature split

| Feature | Free | Pro |
|---|---|---|
| Volume card, map, large and old, duplicates, uninstall | ✓ | ✓ |
| Empty folders and broken shortcuts (Tidy) | ✓ | ✓ |
| Move to Trash with every safety check | ✓ | ✓ |
| Windows: protected folders (02), temporary files (03) | ✓ | ✓ |
| Journal and Put back | last 30 days shown | everything |
| Export report and journal (Markdown, JSON) | | ✓ |
| Low-disk alert | | ✓ |
| Suggest (Windows, ASP-17; 03) | | ✓ |
| Apps and caches (Windows, ASP-20): the rows, their sizes and reasons (owner decision 2026-10-22) | ✓ | ✓ |
| Apps and caches: clearing known caches with the tool's own command (05, Command log), and Suggest there | | ✓ |
| No purchase reminder | | ✓ |

## Purchase reminder (Free only)

- Non-blocking banner; at most once per app launch; never while a scan or a deletion runs; dismissed with one click.
- **Shown only when a purchase page exists.** Launch phase (2026-10-10): there is none, so the reminder never appears.
- Pro-only controls in Free show one quiet "Pro" label; clicking does nothing else (no modal, no popup). Pro users never see the word "Pro" outside About and Settings.

Windows note, Suggest (ASP-17): the Suggest button of Temporary files, Large and old and Duplicates is a Pro-only control. In Free it is disabled and a quiet "Pro" label sits beside it; a click does nothing (no modal, no popup, no tick). A key entered in About (ASP-11) turns it on at once, and the label goes. Pro users never see the label.

## License

- Pro is unlocked only by a license key that verifies offline and whose `products` list contains `space`. Format and verification live in the Ambysto Kit spec (license format, AO-5).
- Release builds have no other way to unlock Pro. Windows note (ASP-17): Debug builds also take the QA argument `--pro`, which makes the app Pro so that the Pro side of the UI can be tested before the production key exists; it is compiled into Debug builds only, and the gates prove a Release build holds no such string.
- "Registered to <licensee>" in About and Settings (Windows, ASP-11: About holds the licence entry and status; Settings holds the feature preferences).
- Launch phase: keys are gifted to early users **unconditionally** — never in exchange for a review.

## Low-disk alert (Pro)

- Checks every 5 minutes while the app runs (the window may be closed). Fires when free space < threshold (default 10 GB). 30-minute cooldown. Informs only; never deletes.
- Notification permission is requested the first time the user turns the alert on, never at launch. If refused, a badge on the app icon is used.
- Optional "Open at login".

Windows notes: the key is pasted in About (ASP-11; no file association in v1); leading and trailing white space (a newline copied with it) is removed before the check. About says "Free to use. We only charge when you are happy to pay." The alert is a Windows notification through the app identity the installer registers; when notifications are off, an overlay on the taskbar button. "Open at login" is one value under the user's `Run` key, added and removed only by its own module; it is the only registry value the app writes.

## Manual update check (Windows, ASP-11)

About shows the app name, installed version, site link and "Check for updates". Only pressing that button makes one request to the site's small Windows version manifest. No startup, navigation, scan, licence or periodic check; no disk contents, key or machine identifier is sent. A newer version is reported and its HTTPS download page opened through the default browser; Lean never downloads or installs it. Current/older versions, network/manifest failures and a browser failure have inline messages; a busy check cannot be started again and closing cancels it. Donate stays hidden in v1. The manifest and bounded-reader/test contracts are in `intent/2026-10-23-asp-11-about-updates.md`.

## Full Disk Access onboarding

Required before the first scan. The volume card stays visible behind the step.

Copy:
1. "Ambysto Lean needs Full Disk Access to measure your whole disk."
2. Trust line: "Lean reads your disk on this Mac and never uploads anything. Deletions go to the Trash."
3. Button **Open Privacy Settings**.
4. "If Ambysto Lean is not in the list, click + and choose it from Applications, or drag it into the list."
5. "After an update, if the switch is on but Lean still asks: remove Ambysto Lean from the list with −, then add it again."
6. The step closes by itself when access is detected (checked every second).

Windows note: no equivalent permission and no onboarding step; folders that need elevation are counted as unreadable. Started elevated, the app says "Moving is off while running as administrator." and does not relaunch itself.

### Windows: developer diagnosis and app versions (ASP-30)

Developer space and Installers and versions are readable in Free. A verified old-version leaf can use the existing confirmed Recycle Bin move and Put back in Free, following the same entitlement as other file moves. Native tool cache commands remain Pro with their existing permanent-deletion warning and command history. Diagnosis of toolchains, models, account/project stores or pending updates does not grant cleaning authority. Unknown measurements remain explicitly unknown and explained, rather than becoming zero or reclaimable bytes.
