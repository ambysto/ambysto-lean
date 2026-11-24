using System.Globalization;

namespace Ambysto.Lean.Core.Copy;

/// <summary>Every user-facing string of phase A, in one place (docs/spec/07 and the wording the other specs give, with
/// their Windows notes). <c>scripts/copy-lint.sh</c> checks this file for the banned words.</summary>
public static class Text
{
    /// <summary>docs/spec/01, Progress and cancel.</summary>
    public const string ScanCancelled = "Scan cancelled — partial";

    /// <summary>docs/spec/01, walk rule 6.</summary>
    public static string CloudFilesNotDownloaded(long count) => string.Format(CultureInfo.CurrentCulture, "{0} cloud files not downloaded", count);

    /// <summary>docs/spec/01, Volume card.</summary>
    public const string UsedByThisVolume = "Used by this volume";

    /// <summary>docs/spec/01, Volume card.</summary>
    public const string FreeRightNow = "Free right now";

    /// <summary>docs/spec/01, Volume card (Windows note).</summary>
    public const string InTheRecycleBin = "In the Recycle Bin";

    /// <summary>docs/spec/01, Volume card (Windows note): opens the Recycle Bin, never empties it.</summary>
    public const string OpenRecycleBin = "Open Recycle Bin";

    /// <summary>A size that could not be read (plan group 5 UI contract: the Recycle Bin line when the shell cannot read it).</summary>
    public const string Unknown = "unknown";

    /// <summary>The button that starts a scan (plan group 5 UI contract).</summary>
    public const string Scan = "Scan";

    /// <summary>The Scan button once a map is shown (ASP-14, the scanned state of Overview).</summary>
    public const string ScanAgain = "Scan again";

    /// <summary>The link under the start screen's Scan button (ASP-14): the end of <see cref="ScanDriveOrFolder"/>.</summary>
    public const string ScanFolderLink = "scan a folder";

    /// <summary>The ghost button beside Scan again (ASP-14): opens the system folder picker.</summary>
    public const string ScanFolder = "Scan a folder";

    /// <summary>The line under the start screen's Scan button (ASP-14): the drive the button scans, and the link to scan a folder.</summary>
    public static string ScanDriveOrFolder(string drive) => string.Format(CultureInfo.CurrentCulture, "Scan {0} or {1}", drive, ScanFolderLink);

    /// <summary>The line under the start screen's Scan button when no drive is ticked (ASP-18): the whole line, ending in <see cref="ScanFolderLink"/>.</summary>
    public const string TickADriveOrScanAFolder = "Tick a drive to scan, or " + ScanFolderLink;

    /// <summary>The sidebar item that opens the start screen or the map (ASP-14).</summary>
    public const string Overview = "Overview";

    /// <summary>The first word of the sidebar's app name (ASP-12 accent, ASP-14): "Ambysto" in the mute colour.</summary>
    public const string BrandName = "Ambysto";

    /// <summary>The second word of the sidebar's app name (ASP-12 accent, ASP-14): "Lean" in the tone, bold.</summary>
    public const string ProductName = "Lean";

    /// <summary>The name of a drive that has no label (ASP-14, drive cards).</summary>
    public const string LocalDisk = "Local Disk";

    /// <summary>A drive card's name (ASP-14): the drive and its label.</summary>
    public static string DriveName(string drive, string label) => string.Format(CultureInfo.CurrentCulture, "{0} {1}", drive, label);

    /// <summary>A drive card's numbers (ASP-14): free and total space.</summary>
    public static string DriveFreeOfTotal(string free, string total) => string.Format(CultureInfo.CurrentCulture, "{0} free of {1}", free, total);

    /// <summary>The note on the drive card of the profile's drive (ASP-14; owner decision 2026-10-18: scanning other drives comes after v1).</summary>
    public const string ThisDriveIsScanned = "Lean scans this drive";

    /// <summary>The note on every other drive card (ASP-14).</summary>
    public const string OtherDrivesAfterV1 = "Scanning this drive comes after v1";

    /// <summary>The progress line when the chosen folder is on another drive (ASP-14): the scan does not start.</summary>
    public static string FolderNotOnProfileDrive(string drive) => string.Format(CultureInfo.CurrentCulture, "Scanning other drives comes after v1. Choose a folder on {0}", drive);

    /// <summary>The progress line of a folder scan, which has no total to compare with (ASP-14, docs/spec/01 Progress and cancel).</summary>
    public static string ScanningLine(string where, long files, string measured) => string.Format(CultureInfo.CurrentCulture, "Scanning {0} — {1:N0} files, {2}", where, files, measured);

    /// <summary>The progress line of a whole-drive scan (ASP-14): what is measured, against what the drive uses, as a percent that
    /// reads "about" and never passes 99 until the scan ends.</summary>
    public static string ScanningLine(string where, long files, string measured, string used, int percent) =>
        string.Format(CultureInfo.CurrentCulture, "Scanning {0} — {1:N0} files, {2} of about {3} ({4} %)", where, files, measured, used, percent);

    /// <summary>The prompt line of the scan readout (ASP-14, the terminal readout): the command that is running, on the drive or folder.</summary>
    public static string ScanPrompt(string where) => string.Format(CultureInfo.CurrentCulture, "$ lean scan {0}", where);

    /// <summary>The reading line of the scan readout (ASP-14): the folder a worker is reading now.</summary>
    public static string ScanReading(string path) => string.Format(CultureInfo.CurrentCulture, "reading {0}", path);

    /// <summary>A line of the scan readout's log (ASP-14): a zone the walk has finished.</summary>
    public static string ScanLogDone(string name, string size) => string.Format(CultureInfo.CurrentCulture, "done  {0}  {1}", name, size);

    /// <summary>A line of the scan readout's log (ASP-14): a zone the walk is still reading.</summary>
    public static string ScanLogReading(string name) => string.Format(CultureInfo.CurrentCulture, "....  {0}  reading", name);

    /// <summary>A percent, as the scan readout's meter ends with (ASP-14).</summary>
    public static string Percent(int percent) => string.Format(CultureInfo.CurrentCulture, "{0} %", percent);

    /// <summary>The name of the scan readout's meter when the scan has no total to compare with (a folder scan, ASP-14).</summary>
    public const string ScanMeterRunning = "scanning";

    /// <summary>The first row of the type column of the results view (ASP-14): every file.</summary>
    public const string AllFiles = "All files";

    /// <summary>The last row of the type column (ASP-14): the volume's free space, text only.</summary>
    public const string FreeSpace = "Free space";

    /// <summary>The column of a results row that names its folder (ASP-14).</summary>
    public const string ColumnLocation = "Location";

    /// <summary>Under the largest files (ASP-14): ticking is the only thing that selects a file for moving.</summary>
    public const string LargestFilesNote = "Tick only files you no longer need. They go to the Recycle Bin, and Put back returns them.";

    /// <summary>A row's icon button (ASP-14): opens File Explorer with the file selected.</summary>
    public const string ShowInFileExplorer = "Show in File Explorer";

    /// <summary>The button that cancels a running scan (docs/spec/01, Progress and cancel).</summary>
    public const string Cancel = "Cancel";

    /// <summary>docs/spec/03: shows the display-only rows, hidden by default.</summary>
    public const string ShowSystemItems = "Show system items";

    /// <summary>docs/spec/03, Duplicates: the reclaimable total is labelled an estimate (docs/spec/07).</summary>
    public const string ReclaimableEstimate = "Reclaimable (estimate)";

    /// <summary>docs/spec/04 (Windows note): a per-machine app.</summary>
    public const string NeedsAdminApp = "Needs admin: remove in Settings > Apps";

    /// <summary>docs/spec/04, leftovers 6: a system-wide leftover.</summary>
    public const string NeedsAdmin = "Needs admin";

    /// <summary>docs/spec/04, leftovers 8 (Windows note): leftovers of an app that is still installed.</summary>
    public const string RemoveAppFirst = "Remove the app in Settings first";

    /// <summary>docs/spec/04 (Windows note): the Settings hand-off, naming the entry as Settings lists it.</summary>
    public static string UninstallInSettings(string displayName) => string.Format(CultureInfo.CurrentCulture,
        "In Settings > Apps > Installed apps, find {0} and choose Uninstall. Lean offers its leftovers once it is gone.", displayName);

    /// <summary>docs/spec/01, Coverage (Windows note): what the remainder of "Not measured" holds.</summary>
    public const string NotMeasuredRemainder = "restore points, NTFS metadata, alternate data streams and other users' folders";

    /// <summary>docs/spec/01, Coverage: shown with both numbers.</summary>
    public const string MeasuredMoreThanUsed = "measured more than used";

    /// <summary>docs/spec/01, Map.</summary>
    public const string NotMeasured = "Not measured";

    /// <summary>docs/spec/01, Map.</summary>
    public const string SmallerItems = "smaller items";

    /// <summary>docs/spec/01, Map (Windows note).</summary>
    public const string RevealInExplorer = "Reveal in File Explorer";

    /// <summary>docs/spec/01, Map (Windows note).</summary>
    public const string MoveToRecycleBin = "Move to Recycle Bin…";

    /// <summary>docs/spec/01 hints (Windows notes).</summary>
    public const string HintDeviceBackups = "Manage in Apple Devices or iTunes";

    /// <summary>docs/spec/01 hints.</summary>
    public const string HintSystemWide = "System-wide: needs an administrator; Lean does not change it";

    /// <summary>docs/spec/01 hints (Windows notes).</summary>
    public const string HintDockerWsl = "Manage in Docker Desktop or with wsl.exe";

    /// <summary>docs/spec/01 hints (Windows notes).</summary>
    public static string HintSynced(string provider) => string.Format(CultureInfo.CurrentCulture, "Synced by {0}: manage it there", provider);

    /// <summary>docs/spec/01 hints.</summary>
    public const string HintDeveloperCaches = "Use Ambysto Optimer for developer caches";

    /// <summary>docs/spec/01 hints.</summary>
    public const string HintOutside = "Outside the folders Lean may change";

    /// <summary>docs/spec/03 (Windows notes).</summary>
    public const string LastModified = "last modified";

    /// <summary>docs/spec/03, Duplicates.</summary>
    public const string MayShareStorage = "May share storage; moving it may free less";

    /// <summary>docs/spec/03, Duplicates.</summary>
    public static string Keeping(string path) => string.Format(CultureInfo.CurrentCulture, "Keeping: {0}", path);

    /// <summary>docs/spec/06 (Windows note).</summary>
    public const string MovingOffElevated = "Moving is off while running as administrator.";

    /// <summary>docs/spec/06, License.</summary>
    public static string RegisteredTo(string licensee) => string.Format(CultureInfo.CurrentCulture, "Registered to {0}", licensee);

    /// <summary>docs/spec/06, License: a verified key whose products do not include Lean.</summary>
    public const string KeyNotForThisApp = "This key does not include Ambysto Lean.";

    /// <summary>docs/spec/06, License: a key the check could not verify and gave no reason for.</summary>
    public const string KeyNotVerified = "This key could not be verified.";

    /// <summary>docs/spec/06, Purchase reminder: the quiet label on Pro-only controls in Free.</summary>
    public const string ProLabel = "Pro";

    /// <summary>The window title and the name of the app (plan group 5 UI contract).</summary>
    public const string AppName = "Ambysto Lean";

    /// <summary>The status before the first scan.</summary>
    public const string ReadyToScan = "Ready to scan";

    /// <summary>The status while a scan runs.</summary>
    public static string Scanning(string path) => string.Format(CultureInfo.CurrentCulture, "Scanning {0}", path);

    /// <summary>The status when a scan could not run.</summary>
    public static string ScanFailed(string path, string reason) => string.Format(CultureInfo.CurrentCulture, "Could not scan {0}: {1}", path, reason);

    /// <summary>docs/spec/01, Progress and cancel: the line that refreshes while a scan runs.</summary>
    public static string ScanProgressLine(long files, string measured) => string.Format(CultureInfo.CurrentCulture, "{0:N0} files seen, {1} measured so far", files, measured);

    /// <summary>docs/spec/01, Progress and cancel: one top-level folder total.</summary>
    public static string FolderTotal(string name, string size) => string.Format(CultureInfo.CurrentCulture, "{0}: {1}", name, size);

    /// <summary>A label with its value: the name of a volume card text and of a tile.</summary>
    public static string LabelValue(string label, string value) => string.Format(CultureInfo.CurrentCulture, "{0}: {1}", label, value);

    /// <summary>docs/spec/01, Map: the name of a tile, read out by a screen reader.</summary>
    public static string NameAndSize(string name, string size) => string.Format(CultureInfo.CurrentCulture, "{0}, {1}", name, size);

    /// <summary>docs/spec/01, Coverage: measured against the volume's used bytes.</summary>
    public static string MeasuredOfUsed(string measured, string used) => string.Format(CultureInfo.CurrentCulture, "Measured {0} of {1} used by this volume", measured, used);

    /// <summary>docs/spec/01, Coverage: "measured more than used" with both numbers.</summary>
    public static string MeasuredMoreThanUsedBoth(string measured, string used) => string.Format(CultureInfo.CurrentCulture, "{0}: {1} measured, {2} used", MeasuredMoreThanUsed, measured, used);

    /// <summary>docs/spec/01, Coverage: entries that could not be read are counted.</summary>
    public static string UnreadableItems(long count) => string.Format(CultureInfo.CurrentCulture, count == 1 ? "{0} item could not be read" : "{0} items could not be read", count);

    /// <summary>docs/spec/01, Map: hover shows the size and the percentage of the parent.</summary>
    public static string PercentOfParent(double percent) => string.Format(CultureInfo.CurrentCulture, "{0:0.#}% of parent", percent);

    /// <summary>docs/spec/01, Map: the legend, one entry per file-type group.</summary>
    public const string TypeVideo = "Video";

    /// <summary>docs/spec/01, Map: legend.</summary>
    public const string TypeDiskImage = "Disk image";

    /// <summary>docs/spec/01, Map: legend.</summary>
    public const string TypeArchive = "Archive";

    /// <summary>docs/spec/01, Map: legend.</summary>
    public const string TypeDocument = "Document";

    /// <summary>docs/spec/01, Map: legend.</summary>
    public const string TypeApplication = "Application";

    /// <summary>docs/spec/01, Map: legend.</summary>
    public const string TypeVirtualMachineImage = "Virtual machine image";

    /// <summary>docs/spec/01, Map: legend.</summary>
    public const string TypeOther = "Other";

    /// <summary>docs/spec/01, Map: legend, a folder drawn with its children inside.</summary>
    public const string TypeFolder = "Folder";

    /// <summary>A launcher call failed: File Explorer could not show the item.</summary>
    public static string RevealFailed(string path) => string.Format(CultureInfo.CurrentCulture, "Could not show {0} in File Explorer", path);

    /// <summary>A launcher call failed: the Recycle Bin did not open.</summary>
    public const string OpenRecycleBinFailed = "Could not open the Recycle Bin";

    /// <summary>docs/spec/01, Map: the separator between breadcrumbs.</summary>
    public const string BreadcrumbSeparator = ">";

    // Report export (docs/spec/06, Export): the headings and fixed words of the Markdown report.

    /// <summary>docs/spec/06, Export: the report's title after the app name.</summary>
    public const string ReportTitle = "Scan report";

    /// <summary>docs/spec/06, Export: heading of the volume card section.</summary>
    public const string ReportVolume = "Volume";

    /// <summary>docs/spec/06, Export: heading of the coverage section.</summary>
    public const string ReportCoverage = "Coverage";

    /// <summary>docs/spec/06, Export: heading of the top folders section.</summary>
    public const string ReportTopFolders = "Top folders";

    /// <summary>docs/spec/06, Export: heading of the large and old files section.</summary>
    public const string ReportLargeOld = "Large and old files";

    /// <summary>docs/spec/06, Export: heading of the duplicates section.</summary>
    public const string ReportDuplicates = "Duplicates";

    /// <summary>docs/spec/06, Export: a section with no rows.</summary>
    public const string ReportNothingFound = "Nothing found.";

    /// <summary>docs/spec/03, Duplicates: the badge on the copy Lean suggests keeping (never pre-selected).</summary>
    public const string SuggestedKeep = "suggested keep";

    /// <summary>docs/spec/06 export: a report that holds a name which is not valid text (a lone surrogate) is not
    /// exported; the App shows this instead.</summary>
    public const string ExportInvalidName = "The report holds a file name that is not valid text, so it was not exported.";

    /// <summary>Plan group 8 UI contract: the button that checks a pasted key.</summary>
    public const string ActivateKey = "Activate";

    /// <summary>Plan group 8 UI contract: the license status with no key (docs/spec/06, Free).</summary>
    public const string Free = "Free";

    /// <summary>Plan group 8 UI contract (docs/spec/06): the Open at login toggle.</summary>
    public const string OpenAtLogin = "Open at login";

    /// <summary>Plan group 8 UI contract (docs/spec/06): the low-disk alert toggle.</summary>
    public const string LowDiskAlert = "Low-disk alert";

    /// <summary>Plan group 8 UI contract (docs/spec/06): the export button.</summary>
    public const string ExportReport = "Export report…";

    /// <summary>Plan group 8 UI contract: the main window's Settings button and the title of its view.</summary>
    public const string Settings = "Settings";

    /// <summary>Plan group 8 UI contract: the main window's About button and the title of its view.</summary>
    public const string About = "About";

    /// <summary>The developer attribution in About.</summary>
    public const string DevelopedByAmbysto = "Developed by Ambysto";

    /// <summary>The sidebar action below About that opens Buy Me a Coffee.</summary>
    public const string BuyMeACoffee = "Buy me a coffee";

    /// <summary>ASP-11: the voluntary payment message in About.</summary>
    public const string AboutPayment = "Free to use. We only charge when you are happy to pay.";
    /// <summary>ASP-11: the manual update action.</summary>
    public const string CheckForUpdates = "Check for updates";
    /// <summary>ASP-11: a manual check is in progress.</summary>
    public const string CheckingForUpdates = "Checking for updates…";
    /// <summary>ASP-11: the installed version is current.</summary>
    public const string UpdatesCurrent = "You have the latest version.";
    /// <summary>ASP-11: a failed manual check.</summary>
    public const string UpdatesFailed = "Could not check for updates. Try again later.";
    /// <summary>ASP-11: a newer version's page could not be opened.</summary>
    public const string UpdatePageFailed = "Could not open the download page. Try again later.";
    /// <summary>ASP-11: the product website action.</summary>
    public const string Website = "Website";
    /// <summary>ASP-11: a failed website launch.</summary>
    public const string WebsiteFailed = "Could not open the website. Try again later.";
    /// <summary>The About action that opens the project's GitHub page, where a user can star it.</summary>
    public const string StarOnGitHub = "Star us on GitHub";
    /// <summary>A failed GitHub page launch.</summary>
    public const string GitHubFailed = "Could not open GitHub. Try again later.";
    /// <summary>ASP-11: the validated newer version.</summary>
    public static string UpdateAvailable(string version) => string.Format(CultureInfo.CurrentCulture, "Version {0} is available.", version);

    /// <summary>The text of the move report's Close button (CloseReport); the six views go back with <see cref="BackToMap"/>.</summary>
    public const string CloseView = "Close";

    /// <summary>ASP-9: the back button at the top left of the six views; it returns to the map.</summary>
    public const string BackToMap = "Back to map";

    /// <summary>The label of the license key text box (docs/spec/06, License).</summary>
    public const string LicenseKeyLabel = "License key";

    /// <summary>The version line of the About view.</summary>
    public static string Version(string version) => string.Format(CultureInfo.CurrentCulture, "Version {0}", version);

    /// <summary>Export (docs/spec/06) before any scan has run: there is nothing to report yet.</summary>
    public const string ExportNeedsScan = "Scan first, then export the report.";

    /// <summary>Export (docs/spec/06): the report was written to <paramref name="path"/>.</summary>
    public static string ExportDone(string path) => string.Format(CultureInfo.CurrentCulture, "Report saved to {0}", path);

    /// <summary>Export (docs/spec/06): the file could not be written.</summary>
    public static string ExportFailed(string path) => string.Format(CultureInfo.CurrentCulture, "Could not save the report to {0}", path);

    /// <summary>Export: the name of the Markdown choice in the save dialog.</summary>
    public const string ExportMarkdownChoice = "Markdown";

    /// <summary>Export: the name of the JSON choice in the save dialog.</summary>
    public const string ExportJsonChoice = "JSON";

    /// <summary>Open at login (docs/spec/06 Windows note): the Run value could not be changed.</summary>
    public const string OpenAtLoginFailed = "Could not change Open at login.";

    /// <summary>Low-disk alert (docs/spec/06): informs only; the free space and the threshold, in the status line.</summary>
    public static string LowDiskAlertFired(string free, string threshold) => string.Format(CultureInfo.CurrentCulture, "Low disk space: {0} free, below {1}", free, threshold);

    /// <summary>Export (docs/spec/06): the save dialog could not be opened.</summary>
    public const string ExportPickerFailed = "Could not open the save dialog.";

    /// <summary>Export (docs/spec/06): the scan could not be read to build the report.</summary>
    public const string ExportReadFailed = "Could not read the scan to build the report.";

    /// <summary>Export (docs/spec/06): the chosen location is not a usable file path.</summary>
    public static string ExportInvalidPath(string path) => string.Format(CultureInfo.CurrentCulture, "{0} is not a valid place to save the report.", path);

    /// <summary>Export (docs/spec/06): shown while the report is built (the duplicates pass reads files).</summary>
    public const string ExportPreparing = "Preparing the report…";

    /// <summary>Export (docs/spec/06): the user cancelled while the report was built.</summary>
    public const string ExportCancelled = "Export cancelled.";

    /// <summary>Plan group 8b: the Large and old view before any scan.</summary>
    public const string ScanFirst = "Scan first to list large and old files.";

    /// <summary>Plan group 8b: the main window's buttons.</summary>
    public const string LargeOld = "Large and old";

    /// <summary>Plan group 8b.</summary>
    public const string Duplicates = "Duplicates";

    /// <summary>Plan group 8b.</summary>
    public const string Uninstall = "Uninstall";

    /// <summary>Plan group 8b: starts the duplicates search.</summary>
    public const string FindDuplicates = "Find duplicates";

    /// <summary>Plan group 8b: the minimum-age choices.</summary>
    public const string SixMonths = "6 months";

    /// <summary>Plan group 8b.</summary>
    public const string TwelveMonths = "12 months";

    /// <summary>Plan group 8b.</summary>
    public const string TwoYears = "2 years";

    /// <summary>Plan group 8b: "Uninstall…" opens Settings > Apps (docs/spec/04).</summary>
    public const string UninstallInSettingsButton = "Uninstall…";

    /// <summary>Plan group 8b: a duplicates group's name.</summary>
    public static string CopiesEach(int count, string size) => string.Format(CultureInfo.CurrentCulture, "{0} copies, {1} each", count, size);

    /// <summary>Plan group 8b: a large-and-old row's name.</summary>
    public static string LargeOldRowName(string name, string size, string date, string type) =>
        string.Format(CultureInfo.CurrentCulture, "{0}, {1}, last modified {2}, {3}", name, size, date, type);

    /// <summary>Plan group 8b (docs/spec/04): the watched app is gone; its leftovers come in phase B.</summary>
    public static string AppGone(string name) => string.Format(CultureInfo.CurrentCulture, "{0} is no longer installed.", name);

    /// <summary>Plan group 8b: the label of the minimum-size choice of the Large and old view.</summary>
    public const string MinimumSizeLabel = "Minimum size";

    /// <summary>Plan group 8b: the label of the minimum-age choice of the Large and old view.</summary>
    public const string MinimumAgeLabel = "Minimum age";

    /// <summary>Plan group 8b: the Large and old view after a scan, when no file passes both thresholds.</summary>
    public const string NoLargeOldFiles = "No files match the size and age you chose.";

    /// <summary>Plan group 8b: the Duplicates view before any scan.</summary>
    public const string ScanFirstDuplicates = "Scan first to search for duplicates.";

    /// <summary>Plan group 8b: the status line while the duplicates search runs (docs/spec/03, Duplicates).</summary>
    public static string DuplicatesSearching(int seconds) => string.Format(CultureInfo.CurrentCulture, "Comparing… {0} s", seconds);

    /// <summary>Plan group 8b: the duplicates search was cancelled; no group is shown.</summary>
    public const string DuplicatesCancelled = "Duplicate search cancelled.";

    /// <summary>Plan group 8b: the search found no byte-identical files.</summary>
    public const string NoDuplicatesFound = "No duplicates found.";

    /// <summary>Plan group 8b: the search found <paramref name="groups"/> groups of byte-identical files.</summary>
    public static string DuplicateGroupsFound(int groups) => string.Format(CultureInfo.CurrentCulture, "{0:N0} groups of identical files found.", groups);

    /// <summary>Plan group 8b (docs/spec/04, 07): a Store app is display-only and says where to manage it.</summary>
    public const string StoreAppDisplayOnly = "Store app: remove in Settings > Apps";

    /// <summary>Plan group 8b: the app-gone watch failed unexpectedly.</summary>
    public const string AppGoneWatchFailed = "Could not watch whether the app is gone.";

    /// <summary>Plan group 8b: Settings could not be opened for the hand-off.</summary>
    public const string OpenSettingsFailed = "Could not open Settings.";

    /// <summary>Plan group 8b: the list of installed apps could not be read.</summary>
    public const string UninstallReadFailed = "Could not read the list of installed apps.";

    // Phase B (docs/spec/02, 05, 07 Windows notes): moves, skips, lost items and Put back.

    /// <summary>docs/spec/07 Windows note: after a move.</summary>
    public static string MovedToRecycleBin(string size) => string.Format(CultureInfo.CurrentCulture, "Moved to the Recycle Bin: {0} (measured just now).", size);

    /// <summary>docs/spec/07 Windows note.</summary>
    public const string SpaceReturnsWhenEmptied = "Disk space returns when you empty the Recycle Bin.";

    /// <summary>docs/spec/02 step 1.</summary>
    public const string KeptCopyChanged = "The copy you are keeping changed or is gone";

    /// <summary>docs/spec/02 step 5: the journal does not accept a write.</summary>
    public const string JournalUnavailable = "The journal cannot be written, so nothing more is moved.";

    /// <summary>docs/spec/02: a file with more than one hard link.</summary>
    public const string FreesNothingWhileLinked = "Frees nothing while other links exist";

    /// <summary>docs/spec/02 step 4: in use, with the holders.</summary>
    public static string InUseBy(string holders) => string.Format(CultureInfo.CurrentCulture, "In use by {0}", holders);

    /// <summary>docs/spec/07 Windows note: the deletion report for a lost item.</summary>
    public const string NotFoundIntactInRecycleBin = "Not found intact in the Recycle Bin";

    /// <summary>docs/spec/05 and 07 Windows notes: the journal reason, history and Put back of a lost item.</summary>
    public const string DeletedPermanentlyByWindows = "Windows deleted this permanently instead of moving it to the Recycle Bin";

    /// <summary>docs/spec/05 Put back (Windows wording).</summary>
    public const string NoLongerInRecycleBin = "No longer in the Recycle Bin (emptied or moved)";

    /// <summary>docs/spec/05 Put back.</summary>
    public const string DifferentFileInRecycleBin = "The item in the Recycle Bin is a different file";

    /// <summary>docs/spec/05 Put back.</summary>
    public const string NotInRecycleBinFolder = "Not in a Recycle Bin folder";

    /// <summary>docs/spec/05 Put back.</summary>
    public const string CannotRestoreProtected = "Cannot restore to a protected location";

    /// <summary>docs/spec/05 Put back.</summary>
    public const string DestinationExists = "Something already exists at the original location";

    /// <summary>docs/spec/05 Put back.</summary>
    public const string OriginalFolderGone = "The original folder no longer exists or moved";

    /// <summary>docs/spec/02: the shell move failed and the item is still in place.</summary>
    public static string MoveFailed(int errorCode) => string.Format(CultureInfo.InvariantCulture, "The move failed (0x{0:X8}); the item is still in place.", errorCode);

    /// <summary>docs/spec/02 step 3: an item the scan could not identify is never moved.</summary>
    public const string NoScanIdentity = "Could not verify: the scan has no identity for this item";

    /// <summary>docs/spec/05 Put back: the entry is not one that was moved to the Recycle Bin.</summary>
    public const string NothingToPutBack = "Nothing to put back";

    /// <summary>docs/spec/05 Put back: the entry was already put back.</summary>
    public const string AlreadyPutBack = "Already put back";

    /// <summary>docs/spec/05 Windows note: the one move back failed (never a copy).</summary>
    public static string PutBackFailed(int errorCode) => string.Format(CultureInfo.InvariantCulture, "Could not put it back (error {0}).", errorCode);

    /// <summary>docs/spec/05: the item is back but its journal line could not be written.</summary>
    public const string PutBackNotJournaled = "Put back, but the journal could not be written";

    // Group 11 (plan "UI contract for group 11", docs/spec/02, 05, 07 Windows notes): confirmation, report, history, leftovers.

    /// <summary>Plan group 11: the title of the confirmation dialog.</summary>
    public static string ConfirmMoveTitle(int count) => string.Format(CultureInfo.CurrentCulture,
        count == 1 ? "Move {0} item to the Recycle Bin?" : "Move {0} items to the Recycle Bin?", count);

    /// <summary>Plan group 11 (docs/spec/02): one item of the confirmation; the size is an estimate until re-measured.</summary>
    public static string ConfirmItem(string path, string size) => string.Format(CultureInfo.CurrentCulture, "{0}, about {1}", path, size);

    /// <summary>Plan group 11: the total of the confirmation.</summary>
    public static string AboutTotal(string size) => string.Format(CultureInfo.CurrentCulture, "About {0} in total", size);

    /// <summary>Plan group 11 (docs/spec/02): every item is measured and checked again just before its move.</summary>
    public const string CheckedAgainBeforeMove = "Each item is measured and checked again just before it is moved.";

    /// <summary>Plan group 11: the primary button of the confirmation.</summary>
    public const string MoveToRecycleBinConfirm = "Move to Recycle Bin";

    /// <summary>Plan group 11: the status line while the Trasher runs.</summary>
    public const string MovingItems = "Moving items to the Recycle Bin…";

    /// <summary>Plan group 11: the status line after a run that moved nothing.</summary>
    public const string NothingMoved = "Nothing was moved to the Recycle Bin.";

    /// <summary>Plan group 11: the run stopped before it reached every item (cancelled, or stopped by a failure).</summary>
    public const string MovingStopped = "Moving stopped before the last items.";

    /// <summary>Plan group 11: the run could not start or failed unexpectedly.</summary>
    public static string MoveRunFailed(string reason) => string.Format(CultureInfo.CurrentCulture, "Could not move the items: {0}", reason);

    /// <summary>Plan group 11: the items could not be put back because of an unexpected failure.</summary>
    public static string PutBackRunFailed(string reason) => string.Format(CultureInfo.CurrentCulture, "Could not put the items back: {0}", reason);

    /// <summary>Plan group 11 (docs/spec/05): the report's "Put back all from this session" button.</summary>
    public const string PutBackAllFromSession = "Put back all from this session";

    /// <summary>Plan group 11: one report row after Put back succeeded.</summary>
    public const string PutBackDone = "Put back";

    /// <summary>Plan group 11 (docs/spec/05): the button on one history entry.</summary>
    public const string PutBackButton = "Put back";

    /// <summary>Plan group 11 (docs/spec/05): the button on a history session.</summary>
    public const string PutBackSessionButton = "Put back all";

    /// <summary>Plan group 11: the status line after a Put back.</summary>
    public static string PutBackSummary(int restored, int total) => string.Format(CultureInfo.CurrentCulture, "Put back {0} of {1} items.", restored, total);

    /// <summary>Plan group 11: the main window's button and the title of the History view.</summary>
    public const string History = "History";

    /// <summary>Plan group 11 (docs/spec/05, 06): Free shows the last 30 days of the journal.</summary>
    public const string HistoryFreeLimit = "Free shows the last 30 days. Pro shows everything.";

    /// <summary>Plan group 11: the History view when the journal has nothing to show.</summary>
    public const string HistoryEmpty = "Nothing has been moved yet.";

    /// <summary>Plan group 11: the History view when the journal could not be read.</summary>
    public const string HistoryReadFailed = "Could not read the history.";

    /// <summary>Plan group 11: the name of a history session.</summary>
    public static string HistorySessionName(string when, string module, int count) =>
        string.Format(CultureInfo.CurrentCulture, count == 1 ? "{0}, {1}, {2} item" : "{0}, {1}, {2} items", when, module, count);

    /// <summary>Plan group 11: the name of a history entry, with the reason when there is one.</summary>
    public static string HistoryEntryName(string path, string result, string? reason) => string.IsNullOrEmpty(reason)
        ? string.Format(CultureInfo.CurrentCulture, "{0}: {1}", path, result)
        : string.Format(CultureInfo.CurrentCulture, "{0}: {1}, {2}", path, result, reason);

    /// <summary>Plan group 11 (docs/spec/05): the result of a <c>trashed</c> entry.</summary>
    public const string ResultTrashed = "Moved to the Recycle Bin";

    /// <summary>Plan group 11 (docs/spec/05): the result of a <c>skipped</c> entry.</summary>
    public const string ResultSkipped = "Not moved";

    /// <summary>Plan group 11 (docs/spec/05): the result of a <c>restored</c> entry.</summary>
    public const string ResultRestored = "Put back";

    /// <summary>Plan group 11 (docs/spec/05): the result of a <c>restoreFailed</c> entry.</summary>
    public const string ResultRestoreFailed = "Could not be put back";

    /// <summary>Plan group 11: the module name in a history session (the map).</summary>
    public const string ModuleMap = "Map";

    /// <summary>Plan group 11 (docs/spec/04): the heading of the leftovers of the app that is gone.</summary>
    public static string LeftoversOf(string name) => string.Format(CultureInfo.CurrentCulture, "Leftovers of {0}", name);

    /// <summary>Plan group 11 (docs/spec/04): the app is gone and nothing of it matched.</summary>
    public const string NoLeftoversFound = "No leftovers were found.";

    /// <summary>Plan group 11: the leftovers could not be looked up.</summary>
    public const string LeftoversReadFailed = "Could not look for leftovers.";

    /// <summary>Plan group 11: a leftover folder that could not be measured is shown, not selectable.</summary>
    public const string LeftoverNotMeasured = "Could not be measured, so it is not offered";

    /// <summary>docs/spec/02: the size post-verify compares could not be measured just before the move, so nothing moved.</summary>
    public const string CouldNotMeasureBeforeMove = "Could not verify: the size just before the move";

    /// <summary>docs/spec/02 step 8: moved, but the journal line could not be written; the report names where it went.</summary>
    public static string MovedButNotJournaled(string trashPath) => string.Format(CultureInfo.CurrentCulture, "Moved to the Recycle Bin as {0}, but the journal could not be written", trashPath);

    /// <summary>docs/spec/06 Windows note: the low-disk notification's title.</summary>
    public const string LowDiskNotificationTitle = "Low disk space";

    /// <summary>docs/spec/06 Windows note: the low-disk notification's text.</summary>
    public static string LowDiskNotificationBody(string free, string threshold) => string.Format(CultureInfo.CurrentCulture, "{0} free on this drive, below {1}.", free, threshold);

    /// <summary>docs/spec/02 step 7 (Windows wording): the approved item is in the Recycle Bin, but its size there is not
    /// the size measured just before the move (owner decision 2026-10-17).</summary>
    public const string MovedItemDidNotMatch = "The moved item did not match the approved item — check the Recycle Bin";

    // v1 additions (owner decision 2026-10-18; docs/spec/01, 02, 03, 07 Windows notes).

    /// <summary>docs/spec/02 Windows note: a row inside or holding one of the user's protected folders.</summary>
    public const string HintProtectedByYou = "Protected by you";

    /// <summary>Two sentences in one status line (for example "Scan cancelled — partial. Scanned ...").</summary>
    public static string TwoSentences(string first, string second) => first + ". " + second;

    /// <summary>A row's name followed by one note or hint (for example "C:\Temp\a.tmp, Protected by you").</summary>
    public static string WithNote(string name, string note) => name + ", " + note;

    /// <summary>docs/spec/01 Windows note: what a scan read; the last part only when something could not be read.</summary>
    public static string ScanSummaryLine(long files, long folders, long unreadable, System.TimeSpan elapsed) =>
        string.Format(CultureInfo.CurrentCulture, "Scanned {0:N0} files in {1:N0} folders in {2}", files, folders, Duration(elapsed))
        + (unreadable > 0 ? string.Format(CultureInfo.CurrentCulture, "; {0:N0} could not be read", unreadable) : string.Empty);

    /// <summary>docs/spec/07 Windows note: during a move.</summary>
    public static string MovingProgress(int index, int count, string name) =>
        string.Format(CultureInfo.CurrentCulture, "Moving {0} of {1}: {2}", index, count, name);

    /// <summary>docs/spec/03 "Temporary files": the view's title and button.</summary>
    public const string TemporaryFiles = "Temporary files";

    /// <summary>docs/spec/03 "Temporary files": the age choices.</summary>
    public const string AgeWeek = "1 week";

    /// <summary>docs/spec/03 "Temporary files".</summary>
    public const string AgeMonth = "1 month";

    /// <summary>docs/spec/03 "Temporary files".</summary>
    public const string AgeQuarter = "1 quarter (3 months)";

    /// <summary>docs/spec/03 and 07 Windows notes: the Temporary files total, an estimate.</summary>
    public static string TempEstimate(string size) =>
        string.Format(CultureInfo.CurrentCulture, "About {0} could be moved to the Recycle Bin (estimate)", size);

    /// <summary>docs/spec/02 Windows note: Settings, the protected folders.</summary>
    public const string ProtectedFolders = "Protected folders";

    /// <summary>docs/spec/02 Windows note: Settings, the explanation under the list.</summary>
    public const string ProtectedFoldersNote = "Lean never moves anything inside these folders.";

    /// <summary>Settings: add a protected folder.</summary>
    public const string AddProtectedFolder = "Add folder…";

    /// <summary>Settings: remove the selected protected folder.</summary>
    public const string RemoveProtectedFolder = "Remove";

    /// <summary>Settings: the folder was added.</summary>
    public const string ProtectedFolderAdded = "Added: Lean never moves anything inside it.";

    /// <summary>Settings: the folder was removed from the list.</summary>
    public const string ProtectedFolderRemoved = "Removed: Lean may move items inside it again.";

    /// <summary>Settings: the folder is already covered by the list.</summary>
    public const string ProtectedFolderAlreadyCovered = "Already protected by a folder in the list.";

    /// <summary>Settings: not an existing folder.</summary>
    public const string ProtectedFolderNotAFolder = "Not an existing folder.";

    /// <summary>Settings: not a usable local path.</summary>
    public const string ProtectedFolderNotUsable = "Not a folder on this PC.";

    /// <summary>Settings: the system folder picker could not be opened.</summary>
    public const string FolderPickerFailed = "Could not open the folder picker.";

    /// <summary>Settings: the settings file could not be read; the defaults are used and the file is left as it is.</summary>
    public const string SettingsUnreadable = "Lean's settings file could not be read, so no protected folders are in effect. The file was left as it is.";

    /// <summary>Settings: the settings file could not be saved.</summary>
    public const string SettingsNotSaved = "The change could not be saved.";

    /// <summary>docs/spec/03 "Temporary files": a row's name, "&lt;name&gt;, about &lt;size&gt;, last changed &lt;date&gt;".</summary>
    public static string TempRowName(string name, string size, string date) =>
        string.Format(CultureInfo.CurrentCulture, "{0}, about {1}, last changed {2}", name, size, date);

    /// <summary>docs/spec/03 "Temporary files": the label of the age choice.</summary>
    public const string TempAgeLabel = "Older than";

    /// <summary>docs/spec/03 "Temporary files": nothing is older than the chosen age.</summary>
    public const string NoTempFiles = "No temporary files are older than the age you chose.";

    /// <summary>docs/spec/03 "Temporary files": the folders are being read.</summary>
    public const string ReadingTempFolders = "Reading the temporary folders…";

    /// <summary>docs/spec/03 "Temporary files": the folders could not be read.</summary>
    public const string TempReadFailed = "The temporary folders could not be read.";

    /// <summary>docs/spec/03 "Temporary files": a row holding a link, a cloud file or something unreadable.</summary>
    public const string HintTempNotWhole = "Holds a link, a cloud file or something Lean cannot read: Lean does not move it";

    /// <summary>ASP-10 (table lists): the header of the Keep column of a duplicates group.</summary>
    public const string ColumnKeep = "Keep";

    /// <summary>ASP-10: the header of the column of tick boxes that mark items to move.</summary>
    public const string ColumnMove = "Move";

    /// <summary>ASP-10: the header of the name column.</summary>
    public const string ColumnName = "Name";

    /// <summary>ASP-10: the header of the folder column.</summary>
    public const string ColumnFolder = "Folder";

    /// <summary>ASP-16: the header of the publisher column of the Uninstall table.</summary>
    public const string ColumnPublisher = "Publisher";

    /// <summary>ASP-16: the header of the version column of the Uninstall table.</summary>
    public const string ColumnVersion = "Version";

    /// <summary>ASP-16: the header of the installed-on column of the Uninstall table.</summary>
    public const string ColumnInstalledOn = "Installed on";

    /// <summary>ASP-16: the header of the last-updated column of the Uninstall table.</summary>
    public const string ColumnLastUpdated = "Last updated";

    /// <summary>ASP-10: the header of the size column.</summary>
    public const string ColumnSize = "Size";

    /// <summary>ASP-10: the header of the modification-date column.</summary>
    public const string ColumnLastModified = "Last modified";

    /// <summary>ASP-10: the header of the column that carries the suggested-keep badge.</summary>
    public const string ColumnSuggested = "Suggested";

    /// <summary>ASP-15 (docs/spec/03, Duplicates): a type tab of the duplicates list, the type's name and its number of groups.</summary>
    public static string TypeTab(string label, int groups) => string.Format(CultureInfo.InvariantCulture, "{0} ({1})", label, groups);

    /// <summary>ASP-15: the drop-down beside Move that picks the kept copy of every group shown and ticks the others.</summary>
    public const string SelectByRule = "Select by rule";

    /// <summary>ASP-15: the rule that keeps the copy modified last.</summary>
    public const string RuleNewest = "Newest modified";

    /// <summary>ASP-15: the rule that keeps the copy modified first.</summary>
    public const string RuleOldest = "Oldest modified";

    /// <summary>ASP-15: the rule that keeps the copy with the shortest path.</summary>
    public const string RuleShortest = "Shortest path";

    /// <summary>ASP-15: the rule that keeps the newest copy inside a folder the user picks.</summary>
    public const string RuleInFolder = "Inside a folder I choose…";

    /// <summary>ASP-15: unticks every group of the tab shown; each Keep choice stays.</summary>
    public const string ClearSelection = "Clear selection";

    /// <summary>ASP-15: what a rule did, in the status line.</summary>
    public static string SelectedByRule(int copies, int groups) =>
        string.Format(CultureInfo.CurrentCulture, "Ticked {0:N0} copies in {1:N0} groups; the kept copy of each group is unticked", copies, groups);

    /// <summary>ASP-15 (owner 2026-10-19): the question a rule asks before it ticks more than 100 copies; the program-file part is left out
    /// when none of the copies is a program file.</summary>
    public static string RuleConfirm(int copies, int groups, int programFiles) => programFiles > 0
        ? string.Format(CultureInfo.CurrentCulture, "This ticks {0:N0} copies in {1:N0} groups, {2:N0} of them program files (.dll, .exe and similar). Tick them?", copies, groups, programFiles)
        : string.Format(CultureInfo.CurrentCulture, "This ticks {0:N0} copies in {1:N0} groups. Tick them?", copies, groups);

    /// <summary>ASP-15: the button of that question that applies the rule.</summary>
    public const string RuleConfirmTick = "Tick";

    /// <summary>ASP-17: the Suggest button (Pro).</summary>
    public const string Suggest = "Suggest";

    /// <summary>ASP-17: the header of the column that says why a row was suggested.</summary>
    public const string ColumnWhy = "Why";

    /// <summary>ASP-17: the button that leaves Large and old's own suggestion list for the filtered list.</summary>
    public const string ClearSuggestion = "Clear suggestion";

    /// <summary>ASP-17: Large and old's Suggest found nothing.</summary>
    public const string NothingToSuggest = "Nothing to suggest in this scan";

    /// <summary>ASP-17: the Why of a duplicate copy Suggest ticks (the Newest rule keeps the newest copy).</summary>
    public const string WhyOlderCopy = "Older copy; the newest is kept";

    /// <summary>ASP-17: the status line after Suggest ticked rows.</summary>
    public static string SuggestTicked(int count) => string.Format(CultureInfo.CurrentCulture, "Suggest ticked {0:N0} items", count);

    /// <summary>ASP-17: the status line while Large and old shows its own suggestion list.</summary>
    public static string SuggestedFiles(int count, string size) => string.Format(CultureInfo.CurrentCulture, "Suggested {0:N0} files, {1}", count, size);

    /// <summary>ASP-17: the Why of a temporary row; the date is the local day of its newest write.</summary>
    public static string WhyOldTemp(System.DateTime newestWriteUtc) => "Unchanged since " + LocalShortDate(newestWriteUtc);

    /// <summary>ASP-17: the Why of a setup file or disk image in Downloads.</summary>
    public static string WhyOldInstaller(System.DateTimeOffset lastModified) =>
        "Old download, unchanged since " + LocalShortDate(lastModified.UtcDateTime);

    /// <summary>ASP-17: the Why of a file of 1 GB or more that has not changed for a year.</summary>
    public static string WhyLargeUnused(System.DateTimeOffset lastModified) => "1 GB or more, unchanged since " + LocalShortDate(lastModified.UtcDateTime);

    /// <summary>ASP-17: the question Suggest asks before it ticks more than 100 items; the program-file part only when there are some.</summary>
    public static string SuggestConfirm(int count, int programFiles) => programFiles > 0
        ? string.Format(CultureInfo.CurrentCulture, "Suggest ticks {0:N0} items, {1:N0} of them program files (.dll, .exe and similar). Tick them?", count, programFiles)
        : string.Format(CultureInfo.CurrentCulture, "Suggest ticks {0:N0} items. Tick them?", count);

    private static string LocalShortDate(System.DateTime utc) =>
        System.DateTime.SpecifyKind(utc, System.DateTimeKind.Utc).ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    /// <summary>ASP-15: what Clear selection did, in the status line.</summary>
    public const string SelectionCleared = "Selection cleared";

    /// <summary>ASP-15: a duplicates row's eye button, which opens a picture preview.</summary>
    public const string PreviewPicture = "Preview picture";

    /// <summary>ASP-15 (owner decision 2026-10-18): the preview of an online-only file; it is never opened, so nothing is downloaded.</summary>
    public const string PreviewOnlineOnly = "Online-only: no preview, so nothing is downloaded";

    /// <summary>ASP-15: the preview of a file that could not be read or decoded.</summary>
    public const string PreviewUnavailable = "No preview available for this file";

    /// <summary>ASP-15: the size of the picture in a preview, in pixels.</summary>
    public static string PreviewSize(int width, int height) => string.Format(CultureInfo.CurrentCulture, "{0} × {1} pixels", width, height);

    /// <summary>ASP-15 (owner decision 2026-10-19): the tick box in the Move header of a table that ticks every row that can be moved.</summary>
    public const string SelectAll = "Select all";

    /// <summary>ASP-10: the header of the file-type column.</summary>
    public const string ColumnType = "Type";

    /// <summary>ASP-10: the header of the column that carries a row's note or hint.</summary>
    public const string ColumnNote = "Note";

    /// <summary>ASP-10: the header of the History result column.</summary>
    public const string ColumnResult = "Result";

    /// <summary>ASP-10: the header of the History reason column.</summary>
    public const string ColumnReason = "Reason";

    /// <summary>ASP-10: the header of the History item column.</summary>
    public const string ColumnItem = "Item";

    /// <summary>ASP-10: the header of the last-changed date column of Temporary files.</summary>
    public const string ColumnLastChanged = "Last changed";

    /// <summary>ASP-10: the status a screen reader gets on the column header a table is sorted by, ascending.</summary>
    public const string SortedAscending = "ascending";

    /// <summary>ASP-10: the status a screen reader gets on the column header a table is sorted by, descending.</summary>
    public const string SortedDescending = "descending";

    /// <summary>ASP-10: a table row's automation name, its non-empty cells joined in column order (Narrator and the UI tests read one
    /// row as one line). Two at a time, chained by the caller: the copy tests call every public string method with sample
    /// strings, so no array parameter.</summary>
    public static string RowName(string? head, string? next) =>
        string.IsNullOrEmpty(head) ? next ?? "" : string.IsNullOrEmpty(next) ? head : head + ", " + next;

    /// <summary>ASP-10: a path shortened in the middle to at most <paramref name="maxLength"/> characters, as
    /// "C:\Users\…\build\dist": the drive and the first folder stay, then as many of the last folders as fit. A path that fits
    /// is returned as it is.</summary>
    public static string MiddleShortened(string path, int maxLength)
    {
        if (path.Length <= maxLength || maxLength < 12)
        {
            return path;
        }

        var root = System.IO.Path.GetPathRoot(path) ?? string.Empty;
        var parts = path[root.Length..].Split('\\', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            var head = (maxLength - 1) / 2;
            return path[..head] + "…" + path[^(maxLength - 1 - head)..];
        }

        const string Gap = "\\…\\";
        var start = root + parts[0];
        var tail = new System.Collections.Generic.List<string>();
        var used = start.Length + Gap.Length;
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            var need = parts[i].Length + (tail.Count > 0 ? 1 : 0);
            if (used + need > maxLength)
            {
                break;
            }

            tail.Insert(0, parts[i]);
            used += need;
        }

        if (tail.Count == parts.Length - 1)
        {
            return path;
        }

        if (tail.Count == 0)
        {
            var last = parts[^1];
            var keep = System.Math.Clamp(maxLength - used - 1, 1, last.Length);
            tail.Add("…" + last[^keep..]);
        }

        return start + Gap + string.Join('\\', tail);
    }

    /// <summary>ASP-20: the Why of npm's cache row (npm's docs: the cache repairs itself; clearing it only frees space).</summary>
    public const string WhyNpmCache = "npm downloads these packages again when a project needs them";

    /// <summary>ASP-20: the Why of pip's cache row.</summary>
    public const string WhyPipCache = "pip downloads these packages again when it needs them";

    /// <summary>ASP-20: the Why of uv's cache row.</summary>
    public const string WhyUvCache = "uv downloads these packages again when it needs them";

    /// <summary>ASP-20: the Why of NuGet's http-cache row (entries older than 30 minutes are refreshed anyway).</summary>
    public const string WhyNuGetHttpCache = "NuGet's download cache; NuGet downloads again what a restore needs";

    /// <summary>ASP-20: the Why of NuGet's plugins-cache row.</summary>
    public const string WhyNuGetPluginsCache = "NuGet's plugin cache; NuGet rebuilds it when a plugin runs";

    /// <summary>ASP-20: the Why of a browser's cache row (shown only; the browser's own page clears it).</summary>
    public const string WhyBrowserCache = "Copies of web pages and their code the browser downloads again when needed";

    /// <summary>ASP-20: the Why of CapCut's old versions (shown only).</summary>
    public const string WhyCapCutVersions = "Older CapCut versions kept beside the newest one; your projects are kept elsewhere";

    /// <summary>ASP-20: the Why of an electron-updater pending folder (shown only).</summary>
    public const string WhyUpdaterPending = "An update installer the app downloaded; it downloads it again when needed";

    // ASP-20 phase C: the advice on a virtual disk (decision 4: Lean only says how; it moves nothing). Each text follows the
    // vendor's own documentation, read 2026-10-22.

    /// <summary>ASP-20: the advice on a WSL distribution's disk (owner, 2026-10-22: `--move`). Source: WSL's own help
    /// (`wsl --help`, WSL 3.0.3: "--manage &lt;Distro&gt; --move &lt;Location&gt;: Move the distribution to a new location"). Probed
    /// 2026-10-22 on a test machine (WSL 3.0.3, Ubuntu on E:): a running distribution refuses (WSL_E_DISTRO_NOT_STOPPED), and after
    /// `wsl --terminate` the disk is still held (ERROR_SHARING_VIOLATION); after `wsl --shutdown` the move succeeds, the
    /// distribution's location follows and its files are kept. Not the FAQ's export, unregister, import steps:
    /// `wsl --unregister` deletes the distribution.</summary>
    public const string AdviceWsl = "To move it to another drive: wsl --shutdown, then wsl --manage <name> --move <folder on the other drive>";

    /// <summary>ASP-20: the advice on Docker Desktop's disk image. Source: Docker Docs, "Change your Docker Desktop settings",
    /// Resources > Advanced, "Disk image location" (https://docs.docker.com/desktop/settings-and-maintenance/settings/); the
    /// release notes (https://docs.docker.com/desktop/release-notes/, 4.92.0) show the setting moves the WSL 2 disk on Windows.
    /// The same release notes say that before 4.91 an interrupted move could delete the image and that before 4.92 a move to
    /// another drive failed, so the advice starts with the update (owner decision 16, 2026-10-22).</summary>
    public const string AdviceDockerDesktop = "Update Docker Desktop to 4.92 or later first, then open Settings > Resources > Advanced and change Disk image location";

    /// <summary>ASP-20: the name of the row for Docker Desktop's disk image (docker_data.vhdx).</summary>
    public const string DockerDesktopRowName = "Docker Desktop";

    /// <summary>ASP-20: the name of the row for Docker Desktop's data disk (ext4.vhdx of the data distribution).</summary>
    public const string DockerDesktopDataRowName = "Docker Desktop (data)";

    /// <summary>ASP-20: the advice on a VirtualBox machine's disks. Source: Oracle VirtualBox User Manual, chapter 5 "Virtual
    /// Storage", the Virtual Media Manager (https://www.virtualbox.org/manual/ch05.html): Media on the Tools menu, select the
    /// disk, Move; VirtualBox then updates its configuration files.</summary>
    public const string AdviceVirtualBox = "In VirtualBox Manager, open Tools > Media, select the disk and choose Move";

    /// <summary>ASP-20: a clean refused because the row was not ready when it was approved.</summary>
    public const string CleanNotReady = "Not cleared: this cache could not be checked";

    /// <summary>ASP-20: a clean refused because the tool runs now (decision 6).</summary>
    public static string CleanToolRunning(string appName) => "Not cleared: close " + appName + " first";

    /// <summary>ASP-20: a NuGet clean refused because a .NET process runs (a build server can stay for minutes after a build).</summary>
    public const string CleanDotnetRunning = "Not cleared: .NET is running. Close Visual Studio, or run dotnet build-server shutdown";

    /// <summary>ASP-20 (hard review round 1): no command starts while Lean runs as administrator.</summary>
    public const string CleanNotAsAdministrator = "Not cleared: Lean is running as administrator";

    /// <summary>ASP-20 (hard review round 1): no command starts outside the user's desktop session.</summary>
    public const string CleanNotOutsideDesktop = "Not cleared: Lean is running outside your desktop session";

    /// <summary>ASP-20 (hard review round 2): the environment could not be checked, so no command starts.</summary>
    public const string CleanEnvironmentUnknown = "Not cleared: Lean could not check how it is running";

    /// <summary>ASP-20 (hard review round 1): Windows did not start the program (blocked, removed or not a program).</summary>
    public const string CleanCouldNotStart = "Not cleared: Windows did not start the program";

    /// <summary>ASP-20: a clean refused because the program found now is not the one shown when it was approved.</summary>
    public const string CleanProgramChanged = "Not cleared: the program changed since you approved it";

    /// <summary>ASP-20: a clean refused because the cache folder named now is not the one shown when it was approved.</summary>
    public const string CleanLocationChanged = "Not cleared: the cache folder changed since you approved it";

    /// <summary>ASP-20: a clean refused because its record cannot be written.</summary>
    public const string CleanLogUnavailable = "Not cleared: Lean cannot write its history";

    /// <summary>ASP-20: the clean command ended with an error code.</summary>
    public static string CleanFailed(int exitCode) => string.Format(CultureInfo.CurrentCulture, "The command ended with error {0}", exitCode);

    /// <summary>ASP-20: Lean stopped waiting for the clean command (it is left running).</summary>
    public const string CleanTimedOut = "Still running after 5 minutes: Lean stopped waiting";

    /// <summary>ASP-20 (hard review round 3): added to a failure or timeout whose record could not be written.</summary>
    public const string CleanNotRecorded = "Lean could not write this to its history";

    /// <summary>ASP-20: the clean ran but its record could not be written.</summary>
    public const string CleanLogWriteFailed = "Cleared, but Lean could not write it to its history";

    /// <summary>ASP-20 phase B: the sidebar item and the title of the Apps and caches view.</summary>
    public const string AppsAndCaches = "Apps and caches";

    /// <summary>ASP-20 phase B: the header of the column of tick boxes in Apps and caches.</summary>
    public const string ColumnClear = "Clear";

    /// <summary>ASP-20 phase B: the header of the column that says where the advice comes from.</summary>
    public const string ColumnSource = "Source";

    /// <summary>ASP-20 phase B: the header of the time column of a command entry in History.</summary>
    public const string ColumnWhen = "When";

    /// <summary>ASP-20 phase B: the header of the column that names what happened in a command entry in History.</summary>
    public const string ColumnWhat = "What";

    /// <summary>ASP-20 phase B: the header of the column that shows the command in a command entry in History.</summary>
    public const string ColumnCommand = "Command";

    /// <summary>ASP-20 phase B: the header of the size-before column of a command entry in History.</summary>
    public const string ColumnBefore = "Before";

    /// <summary>ASP-20 phase B: the header of the size-after column of a command entry in History.</summary>
    public const string ColumnAfter = "After";

    /// <summary>ASP-20 phase B: the Source of a row backed by the vendor's documentation (a strong grade).</summary>
    public const string SourceVendorDocs = "Vendor's documentation";

    /// <summary>ASP-20 phase B: the Source of a row backed by community reports or the tool's source code (a weak grade).</summary>
    public const string SourceCommunity = "Community reports";

    /// <summary>ASP-20 phase B: the message while Apps and caches reads its rows.</summary>
    public const string ReadingAppsAndCaches = "Reading apps and caches…";

    /// <summary>ASP-20 phase B: the message when Apps and caches lists nothing.</summary>
    public const string NothingToClear = "Nothing found to clear";

    /// <summary>ASP-20 phase B: the message when Apps and caches could not be read.</summary>
    public const string AppsReadFailed = "Apps and caches could not be read.";

    /// <summary>ASP-20 phase C2: the choice in Apps and caches that shows the cache rows (the phase B table).</summary>
    public const string SectionCaches = "Caches";

    /// <summary>ASP-20 phase C2: the choice in Apps and caches that shows the space each installed app takes.</summary>
    public const string SectionSpaceByApp = "Space by app";

    /// <summary>ASP-20 phase C2: the choice in Apps and caches that shows the large virtual disks.</summary>
    public const string SectionVirtualDisks = "Virtual disks";

    /// <summary>ASP-20 phase C2: the header of the install folder's size in Space by app.</summary>
    public const string ColumnInstallFolder = "Install folder size";

    /// <summary>ASP-20 phase C2: the header of the caches' size in Space by app.</summary>
    public const string ColumnCaches = "Caches";

    /// <summary>ASP-20 phase C2: the header of an app's total in Space by app.</summary>
    public const string ColumnTotal = "Total";

    /// <summary>ASP-20 phase C2: the header of the kind of a virtual disk.</summary>
    public const string ColumnKind = "Kind";

    /// <summary>ASP-20 phase C2: the header of the advice on a virtual disk.</summary>
    public const string ColumnAdvice = "Advice";

    /// <summary>ASP-20 phase C2: the word beside an install folder size that is the uninstall entry's estimate, not measured.</summary>
    public const string Estimated = "estimated";

    /// <summary>ASP-20 phase C2: the message while Space by app is read.</summary>
    public const string ReadingSpaceByApp = "Reading space by app…";

    /// <summary>ASP-20 phase C2: the message when Space by app lists nothing.</summary>
    public const string NoAppsFound = "No installed apps found";

    /// <summary>ASP-20 phase C2: the message when Space by app could not be read.</summary>
    public const string SpaceByAppReadFailed = "Space by app could not be read.";

    /// <summary>ASP-20 phase C2: the message while the virtual disks are read.</summary>
    public const string ReadingVirtualDisks = "Reading virtual disks…";

    /// <summary>ASP-20 phase C2: the message when no virtual disk is found.</summary>
    public const string NoVirtualDisks = "No virtual disks found";

    /// <summary>ASP-20 phase C2: the message when the virtual disks could not be read.</summary>
    public const string VirtualDisksReadFailed = "Virtual disks could not be read.";

    /// <summary>ASP-20 phase C2: the kind of a WSL distribution's disk.</summary>
    public const string KindWsl = "WSL";

    /// <summary>ASP-20 phase C2: the kind of Docker Desktop's disk.</summary>
    public const string KindDockerDesktop = "Docker Desktop";

    /// <summary>ASP-20 phase C2: the kind of a VirtualBox machine's disk.</summary>
    public const string KindVirtualBox = "VirtualBox";

    /// <summary>ASP-20 phase C2: the button in a virtual disk's row that copies its advice.</summary>
    public const string CopyAdvice = "Copy";

    /// <summary>ASP-20 phase C2: the status line after the advice was copied.</summary>
    public const string AdviceCopied = "Advice copied to the clipboard";

    /// <summary>ASP-20 phase C2: the status line when the clipboard could not be used.</summary>
    public const string AdviceCopyFailed = "Could not copy the advice";

    /// <summary>ASP-20 phase B: the Note of a row whose tool did not name an existing cache folder.</summary>
    public const string CacheNotFound = "Lean could not find this cache";

    /// <summary>ASP-20 decision 14: the Note of a browser row; Lean does not open the browser's page, the user does.</summary>
    public static string OpenBrowserClearData(string appName) => "Open " + appName + " and press Ctrl+Shift+Delete";

    /// <summary>ASP-20 phase B: the button of a shown-only row that opens its folder in File Explorer.</summary>
    public const string OpenFolder = "Open folder";

    /// <summary>ASP-20 phase B: the button that runs the ticked rows' own commands, and the confirmation's button.</summary>
    public const string Clear = "Clear";

    /// <summary>ASP-20 phase B: the button that reads Apps and caches again.</summary>
    public const string Refresh = "Refresh";

    /// <summary>ASP-20 phase B: the title of the confirmation that comes before any command runs.</summary>
    public const string ConfirmClearTitle = "Clear these caches?";

    /// <summary>ASP-20 phase B: the warning the confirmation always shows (these commands do not use the Recycle Bin).</summary>
    public const string ConfirmClearWarning = "These files are deleted for good. They do not go to the Recycle Bin and cannot be put back.";

    /// <summary>ASP-20 phase B: the first line of an item in the confirmation (the exact command follows on its own line).</summary>
    public static string ConfirmClearItem(string appName, string size) => string.Format(CultureInfo.CurrentCulture, "{0}, about {1}", appName, size);

    /// <summary>ASP-20 phase B: the status line while a command runs.</summary>
    public static string Clearing(string appName, int index, int count) =>
        string.Format(CultureInfo.CurrentCulture, "Clearing {0} ({1} of {2})…", appName, index, count);

    /// <summary>ASP-20 phase B: the report line of a command that ended with 0.</summary>
    public static string ClearReportDone(string appName, string before, string after) =>
        string.Format(CultureInfo.CurrentCulture, "{0}: {1} before, {2} now", appName, before, after);

    /// <summary>ASP-20 phase B: the status line after the commands ran.</summary>
    public static string ClearSummary(int done, int count) => string.Format(CultureInfo.CurrentCulture, "Cleared {0} of {1}", done, count);

    /// <summary>ASP-20 phase B: the reason in the report when a command could not be run at all.</summary>
    public static string ClearRunFailed(string reason) => string.Format(CultureInfo.CurrentCulture, "The command could not be run: {0}", reason);

    /// <summary>ASP-20 phase B: the short reason shown when an unexpected error stopped a run (the error's own message is not shown).</summary>
    public const string ClearRunFailedReason = "Lean could not run it";

    /// <summary>ASP-20 phase B: the status line when the window is closed while a clean is running; the window closes when it ends.</summary>
    public const string LeanClosesWhenClearingEnds = "Lean closes when clearing ends";

    /// <summary>ASP-24: the status line when the window is closed while items are being moved; every approved item is moved and journaled,
    /// then the window closes.</summary>
    public const string LeanClosesWhenMovingEnds = "Lean closes when moving ends";

    /// <summary>ASP-24: the status line when the window is closed while Put back runs; the window closes when it ends.</summary>
    public const string LeanClosesWhenPuttingBackEnds = "Lean closes when putting back ends";

    /// <summary>ASP-20 phase B: the text of a command entry in History after a run that ended with 0.</summary>
    public static string HistoryCommandDone(string appName) => appName + " cache cleared by its own command";

    /// <summary>ASP-20 phase B: the text of a command entry in History after a run that ended with an error code.</summary>
    public static string HistoryCommandFailed(string appName, int exitCode) =>
        string.Format(CultureInfo.CurrentCulture, "{0} cache: its own command ended with error {1}", appName, exitCode);

    /// <summary>ASP-20 phase B: the text of a command entry in History after a run Lean stopped waiting for.</summary>
    public static string HistoryCommandTimedOut(string appName) => appName + " cache: its own command was still running after 5 minutes";

    /// <summary>Verified removal results; discovery does not imply cleanup.</summary>
    public const string UninstallNoLeftovers = "App removal confirmed. No leftover locations found.";
    /// <summary>Factual uninstall result presentation.</summary>
    public const string UninstallSearchFailed = "App removal confirmed. Leftover locations could not be checked.";
    /// <summary>Factual uninstall result presentation.</summary>
    public const string UninstallCleanupComplete = "All approved leftover locations were moved and recorded.";
    /// <summary>Factual uninstall result presentation.</summary>
    public const string UninstallCleanupPartial = "Some approved leftover locations were not moved and recorded successfully.";
    /// <summary>Factual uninstall result presentation.</summary>
    public const string UninstallResultTitle = "Uninstall result";
    /// <summary>Factual uninstall result presentation.</summary>
    public const string UninstallResultDetails = "Show details";
    /// <summary>Factual uninstall result presentation.</summary>
    public const string UninstallResultPutBack = "Review Put back";
    /// <summary>Factual uninstall result presentation.</summary>
    public static string UninstallFoundLeftovers(int count) => string.Format(CultureInfo.CurrentCulture, "App removal confirmed. {0} leftover locations found to review. These locations have not been moved.", count);
    /// <summary>Factual uninstall result presentation.</summary>
    public static string UninstallApprovedCount(int count) => string.Format(CultureInfo.CurrentCulture, "Approved: {0}", count);
    /// <summary>Factual uninstall result presentation.</summary>
    public static string UninstallMovedCount(int count) => string.Format(CultureInfo.CurrentCulture, "Moved: {0}", count);
    /// <summary>Factual uninstall result presentation.</summary>
    public static string UninstallUnsuccessfulCount(int count) => string.Format(CultureInfo.CurrentCulture, "Unsuccessful: {0}", count);
    /// <summary>Factual uninstall result presentation.</summary>
    public static string UninstallUnattemptedCount(int count) => string.Format(CultureInfo.CurrentCulture, "Not attempted: {0}", count);

    /// <summary>Evidence for an inactive version candidate.</summary>
    public const string VersionUnusedEvidence = "Older than the registered current version; not referenced. Rechecked before moving.";
    /// <summary>Incomplete version measurement.</summary>
    public const string VersionNotMeasured = "Could not measure completely. Review only.";
    /// <summary>Unknown current/reference evidence.</summary>
    public const string VersionReferencesUnknown = "Current or updater references could not be verified. Review only.";
    /// <summary>Retained current, newest, rollback or referenced version.</summary>
    public const string VersionRetained = "Current, newest, rollback or referenced version: keep.";

    /// <summary>An updater artifact may still be needed and has no cleanup authority.</summary>
    public const string InstallerPendingUnknown = "Downloaded or pending update: current installer references are unknown. Review only.";

    /// <summary>Multiple installed apps claim this updater artifact.</summary>
    public const string InstallerSharedOwner = "Shared updater";
    /// <summary>Ambiguous metadata ownership grants no cleanup authority.</summary>
    public const string InstallerOwnershipUnknown = "Ambiguous ownership: multiple apps reference this pending download. Review only.";

    private static string Duration(System.TimeSpan elapsed) => elapsed.TotalSeconds < 60
        ? string.Format(CultureInfo.CurrentCulture, "{0:0} s", System.Math.Max(1, System.Math.Round(elapsed.TotalSeconds)))
        : string.Format(CultureInfo.CurrentCulture, "{0} min {1} s", (int)elapsed.TotalMinutes, elapsed.Seconds);
    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string SectionDeveloperSpace = "Developer space";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string SectionInstallersVersions = "Installers and versions";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string ColumnCategory = "Category";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string ReadingDeveloperSpace = "Measuring developer and application stores...";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string NoDeveloperSpace = "No supported stores were found with data to measure.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperSpaceReadFailed = "Could not read developer space. Try Refresh.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string ReadingInstallersVersions = "Reading installers and versions...";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string NoInstallersVersions = "No supported installer or version folders were found.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string InstallersVersionsReadFailed = "Could not read installers and versions. Try Refresh.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperSpaceSharedBytes = "Shared file bytes are counted in an earlier store.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCategoryToolchains = "Toolchains";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCategoryPackages = "Package stores";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCategoryTools = "Installed tools";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCategoryModels = "Tool and model data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCategoryAccounts = "Application and account data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCategoryApplications = "Installed applications";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperNoteTools = "Installed tools and toolchains may be needed by projects. Review them with the owning tool.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperNotePackages = "Installed packages and tools are not a cache cleanup estimate. Review them with the owning tool.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperNoteModels = "May contain downloaded models and tool data. Its name alone does not make it reclaimable.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperNoteAccounts = "May contain accounts, settings, sessions or projects. Review it in the owning application.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperNoteApplications = "Installed application files are measured for diagnosis. A measured size is not permission to remove them.";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperRustup = "Rust toolchains";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCargoRegistry = "Cargo registry";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCargoBin = "Cargo tools";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperSharedCache = "Shared tool data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCodex = "Codex data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperNugetPackages = "NuGet packages";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperUserPrograms = "User applications";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperStorePackages = "Store application data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCapcutApps = "CapCut installations";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperCapcutData = "CapCut user data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperLark = "Lark";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperLarkInternational = "Lark International";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperHermes = "Hermes data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperNotion = "Notion data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperOrca = "Orca data";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperNpmTools = "npm installed tools";

    /// <summary>ASP-30: read-only developer and installer diagnosis copy.</summary>
    public const string DeveloperChromeModel = "Chrome on-device model";


    /// <summary>ASP-30: a known store could not be measured safely and completely.</summary>
    public const string DeveloperSpaceUnknownRoot = "Size unknown: this store is unavailable, synced or redirected.";

    /// <summary>ASP-30: incomplete coverage or missing identities must not look like a complete zero-sized store.</summary>
    public const string DeveloperSpaceIncomplete = "Size unknown: some items could not be measured completely or contain links or cloud files.";

}
