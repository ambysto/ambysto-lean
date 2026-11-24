#requires -Version 5.1
<#
.SYNOPSIS
ASP-7 criterion 11: physical Windows PC performance proof, without an SDK.
.EXAMPLE
powershell.exe -NoProfile -STA -File .\perf-check.ps1 -AppPath 'C:\Program Files\Ambysto Lean\AmbystoLean.exe' -OutputDirectory "$env:USERPROFILE\LeanPerf-20261023" -ConfirmPhysicalMachine
.EXAMPLE
powershell.exe -NoProfile -STA -File .\perf-check.ps1 -AppPath 'C:\Lean\AmbystoLean.exe' -OutputDirectory "$env:USERPROFILE\LeanPerf-VM-1" -AllowVirtualMachine
.DESCRIPTION
Run unelevated in an unlocked local desktop session, with Lean closed. Supply an
installed/published self-contained x64 app (keep its adjacent runtime files).
Requires stock Windows PowerShell 5.1, Windows 11 24H2+, UI Automation and CIM;
no dotnet CLI, SDK, BuildTools or downloaded packages. Do not use PowerShell 7.

OutputDirectory MUST NOT EXIST; its parent must already exist on the profile
drive, on local NTFS, outside cloud/reparse folders. Creates 1,000,000 one-byte
files in 1,000 folders beneath it, plus proof.json. No existing file is replaced
or removed. Fixture generation is timed separately; allow substantial disk space
and time. Retain the fixture and JSON for review; cleanup is manual.

Scan timing is deliberately conservative: process start through the UIA final
summary, including startup/UI dispatch, without --scan-delay. Summary must say
exactly 1,000,000 files and no unreadable entries. Memory is the process's OS
peak working set (not private bytes), sampled through result UI readiness plus
one second (eight TypeGroup UIA items required; timeout fails), in binary MB
(1 MB = 1,048,576 bytes, plan Q3). Launch measures visible VolumeCard with loaded
UsedByThisVolume data and an enabled Scan action. Cancel uses a separate
--scan-delay 50 run with a positive observed file count; time begins
before invoking Cancel and ends at the cancelled summary, before process exit.
UIA polling adds conservative observation latency. Raw observations are retained.
These are single warm-cache runs after fixture generation, not cold-disk proof.

No system settings are changed. The app may perform its normal profile writes.
Only processes launched here are closed; a hung process is left for manual close.
CIM VM detection is heuristic: -ConfirmPhysicalMachine is also required for a
physical claim. -AllowVirtualMachine permits informative runs, always with
physicalProof:false; it never bypasses numeric gates. Detected/unknown hardware
without that flag fails. Exit 0 means all gates passed and either physical proof
or an explicitly informative run; otherwise exit 1. Read physicalProof in JSON.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$ConfirmPhysicalMachine,
    [switch]$AllowVirtualMachine
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$proof = [ordered]@{
    schemaVersion = 1
    criterion = 'ASP-7 criterion 11'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    physicalProof = $false
    informative = [bool]$AllowVirtualMachine
    operatorConfirmsPhysicalMachine = [bool]$ConfirmPhysicalMachine
    thresholds = @{ files = 1000000; scanSeconds = 60; peakWorkingSetBytes = 419430400; memoryUnit = 'binary MB (1048576 bytes)'; cancelSeconds = 1; launchSeconds = 2 }
    measurements = [ordered]@{}
    gates = [ordered]@{ fixture = $false; scan = $false; memory = $false; cancel = $false; launch = $false; environment = $false }
    errors = @()
    limitations = @('Single run; caches warm after fixture generation.', 'Scan wall time includes process startup.', 'UIA observation latency is included.', 'Memory additionally covers eight TypeGroup UIA items plus a one-second observation; readiness timeout fails the memory gate.', 'Cancel uses the documented delayed QA walk, not an undelayed performance run.', 'Physical classification requires operator attestation plus heuristic CIM checks.', 'Unmeasured inherited proofs: first tiles, UI stalls, hover, drilldown, cold-versus-warm comparisons, and real-volume scans.')
}
$ownedProcesses = New-Object System.Collections.ArrayList
$output = $null
$exitCode = 1
$originalTemp = $env:TEMP
$originalTmp = $env:TMP

function Find-Element($Process, [string]$Id) {
    if ($Process.HasExited) { throw "App exited unexpectedly (exit $($Process.ExitCode))." }
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)))
    foreach ($window in $windows) {
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)))
        if ($null -ne $element) { return $element }
    }
    return $null
}

function Start-Lean([string]$Arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $script:app
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($script:app)
    $info.UseShellExecute = $false
    $info.Arguments = $Arguments
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    [void]$script:ownedProcesses.Add($process)
    return @{ process = $process; clock = $clock }
}

function Close-Lean($Process) {
    if (!$Process.HasExited) {
        [void]$Process.CloseMainWindow()
        if (!$Process.WaitForExit(10000)) { throw "App PID $($Process.Id) did not close; close it manually before rerunning." }
    }
}

try {
    if ($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use stock Windows PowerShell 5.1 on Windows.' }
    if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Start powershell.exe with -STA.' }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as a standard user, without elevation.' }
    if (![Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or $env:SESSIONNAME -like 'RDP*') { throw 'Run in an unlocked local desktop session, not a service or RDP session.' }
    if ($ConfirmPhysicalMachine -and $AllowVirtualMachine) { throw 'Choose physical attestation OR informative mode.' }

    $app = (Resolve-Path -LiteralPath $AppPath).ProviderPath
    if (![IO.File]::Exists($app) -or [IO.Path]::GetExtension($app) -ne '.exe') { throw 'AppPath must be the published/installed app executable.' }
    if (@(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($app)) -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Close every existing Lean instance first.' }

    $requested = [IO.Path]::GetFullPath($OutputDirectory)
    if ($OutputDirectory -notmatch '^[A-Za-z]:\\' -or [Management.Automation.WildcardPattern]::ContainsWildcardCharacters($requested)) { throw 'OutputDirectory must be an absolute local path without wildcard characters.' }
    if ([IO.Directory]::Exists($requested) -or [IO.File]::Exists($requested)) { throw 'OutputDirectory already exists; choose a new directory. Nothing was overwritten.' }
    $parent = [IO.Path]::GetDirectoryName($requested.TrimEnd('\'))
    if (![IO.Directory]::Exists($parent)) { throw 'OutputDirectory parent must already exist.' }
    $driveRoot = [IO.Path]::GetPathRoot($requested)
    if ($driveRoot -ne [IO.Path]::GetPathRoot($env:USERPROFILE)) { throw 'Fixture must be on the profile drive (the app refuses scans on other drives).' }
    $drive = New-Object IO.DriveInfo($driveRoot)
    if ($drive.DriveFormat -ne 'NTFS' -or $drive.DriveType -ne [IO.DriveType]::Fixed) { throw 'Use a local fixed NTFS drive.' }
    $cursor = $parent
    while ($cursor) {
        $attributes = [IO.File]::GetAttributes($cursor)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Output path must not pass through a reparse point/cloud folder.' }
        $next = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        if ($next -eq $cursor) { break }
        $cursor = $next
    }
    foreach ($cloudRoot in @($env:OneDrive, $env:OneDriveConsumer, $env:OneDriveCommercial)) {
        if ($cloudRoot -and ($requested + '\').StartsWith(([IO.Path]::GetFullPath($cloudRoot).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a path outside OneDrive.' }
    }

    # No -Force: New-Item refuses an existing directory. All fixture files and
    # results additionally use CreateNew, so none can overwrite existing data.
    [void](New-Item -ItemType Directory -Path $requested -ErrorAction Stop)
    $output = $requested
    $proof.outputDirectory = $output
    # Add-Type's stock Framework compiler can write temporary files. Keep those
    # inside the explicit output tree as well, restoring the environment at exit.
    $compilerTemp = Join-Path $output 'compiler-temp'
    [void](New-Item -ItemType Directory -Path $compilerTemp -ErrorAction Stop)
    $env:TEMP = $compilerTemp
    $env:TMP = $compilerTemp
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class LeanPerfFixture {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    public static bool HasInputDesktop() {
        IntPtr desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero) return false;
        CloseDesktop(desktop); return true;
    }
    public static int Generate(string root) {
        int count = 0;
        for (int folder = 0; folder < 1000; folder++) {
            string path = Path.Combine(root, "d" + folder.ToString("D4"));
            if (!CreateDirectory(path, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            for (int file = 0; file < 1000; file++) {
                using (var stream = new FileStream(Path.Combine(path, "f" + file.ToString("D4") + ".bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.WriteByte(1);
                count++;
            }
        }
        return count;
    }
}
'@
    if (![LeanPerfFixture]::HasInputDesktop()) { throw 'Desktop is locked or unavailable; unlock the local desktop.' }
    $proof.app = @{ path = $app; sha256 = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash; fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion; productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion }

    $os = Get-CimInstance Win32_OperatingSystem
    $computer = Get-CimInstance Win32_ComputerSystem
    $bios = Get-CimInstance Win32_BIOS
    $cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors)
    $hardwareText = "$($computer.Manufacturer) $($computer.Model) $($bios.Manufacturer) $($bios.SMBIOSBIOSVersion)"
    $vmDetected = $hardwareText -match '(?i)virtual|vmware|qemu|kvm|xen|parallels|innotek|virtualbox|bochs|amazon ec2|google compute|openstack|hvm domu'
    $hardwareKnown = ![string]::IsNullOrWhiteSpace($computer.Manufacturer) -and ![string]::IsNullOrWhiteSpace($computer.Model)
    # HypervisorPresent also occurs with VBS/Hyper-V on real PCs. Record it, but
    # never use that bit alone as either physical or virtual proof.
    $proof.environment = @{ os = $os.Caption; version = $os.Version; build = $os.BuildNumber; architecture = $os.OSArchitecture; cpu = $cpu; manufacturer = $computer.Manufacturer; model = $computer.Model; biosManufacturer = $bios.Manufacturer; biosVersion = $bios.SMBIOSBIOSVersion; hypervisorPresent = $computer.HypervisorPresent; virtualMachineDetected = [bool]$vmDetected; hardwareKnown = [bool]$hardwareKnown; totalPhysicalMemoryBytes = $computer.TotalPhysicalMemory; sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId; drive = $driveRoot; filesystem = $drive.DriveFormat }
    $proof.gates.environment = ([int]$os.BuildNumber -ge 26100) -and ($os.ProductType -eq 1) -and ($os.OSArchitecture -match '64') -and ($AllowVirtualMachine -or ($ConfirmPhysicalMachine -and $hardwareKnown -and !$vmDetected))
    if (!$proof.gates.environment) { throw 'Environment gate failed: Windows 11 24H2+, known physical hardware with -ConfirmPhysicalMachine, or explicit -AllowVirtualMachine required.' }

    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $fixture = Join-Path $output 'fixture'
    if (![LeanPerfFixture]::CreateDirectory($fixture, [IntPtr]::Zero)) { throw 'Could not create fixture directory.' }
    Write-Host 'Generating 1,000,000 one-byte files; this time is excluded from scan timing.'
    $generation = [Diagnostics.Stopwatch]::StartNew()
    $created = [LeanPerfFixture]::Generate($fixture)
    $generation.Stop()
    $verification = [Diagnostics.Stopwatch]::StartNew()
    $count = 0
    foreach ($file in [IO.Directory]::EnumerateFiles($fixture, '*', [IO.SearchOption]::AllDirectories)) { $count++ }
    $verification.Stop()
    $proof.fixture = @{ path = $fixture; createdFiles = $created; verifiedFiles = $count; bytesPerFile = 1; folders = 1000; generationSeconds = $generation.Elapsed.TotalSeconds; verificationSeconds = $verification.Elapsed.TotalSeconds }
    $proof.gates.fixture = $created -eq 1000000 -and $count -eq 1000000
    if (!$proof.gates.fixture) { throw 'Fixture count mismatch.' }

    Write-Host 'Measuring launch to loaded volume card.'
    $run = Start-Lean ''
    $launch = $null
    while ($run.clock.Elapsed.TotalSeconds -lt 15) {
        $card = Find-Element $run.process 'VolumeCard'
        $used = Find-Element $run.process 'UsedByThisVolume'
        $scanAction = Find-Element $run.process 'Scan'
        if ($null -ne $card -and !$card.Current.IsOffscreen -and $null -ne $used -and !$used.Current.IsOffscreen -and $used.Current.Name -match '^Used by this volume: .+' -and $null -ne $scanAction -and $scanAction.Current.IsEnabled -and !$scanAction.Current.IsOffscreen) { $launch = $run.clock.Elapsed.TotalSeconds; break }
        Start-Sleep -Milliseconds 10
    }
    $proof.measurements.launch = @{ seconds = $launch; boundary = 'Process.Start to visible loaded VolumeCard and enabled visible Scan action'; observedAtUtc = [DateTime]::UtcNow.ToString('o'); timeoutSeconds = 15 }
    $proof.gates.launch = $null -ne $launch -and $launch -le 2
    Close-Lean $run.process

    Write-Host 'Measuring scan and process peak working set.'
    $run = Start-Lean ('--scan "' + $fixture + '"')
    $samples = New-Object System.Collections.ArrayList
    $peak = 0L
    $scan = $null
    $summary = $null
    $reportedFiles = $null
    $lastSample = -1.0
    while ($run.clock.Elapsed.TotalSeconds -lt 120) {
        $run.process.Refresh()
        if ($run.process.HasExited) { throw 'Scan app exited before its final summary.' }
        $peak = [Math]::Max($peak, $run.process.PeakWorkingSet64)
        if ($run.clock.Elapsed.TotalSeconds - $lastSample -ge 0.1) {
            $lastSample = $run.clock.Elapsed.TotalSeconds
            [void]$samples.Add(@{ seconds = $lastSample; peakWorkingSetBytes = $run.process.PeakWorkingSet64; workingSetBytes = $run.process.WorkingSet64 })
        }
        $status = Find-Element $run.process 'Status'
        if ($null -ne $status -and $status.Current.Name -match '^Scanned (.+?) files in (.+?) folders in (.+)$') {
            $summary = $status.Current.Name
            $scan = $run.clock.Elapsed.TotalSeconds
            $reportedFiles = [long]($Matches[1] -replace '[^0-9]', '')
            break
        }
        Start-Sleep -Milliseconds 10
    }
    $run.process.Refresh()
    $peak = [Math]::Max($peak, $run.process.PeakWorkingSet64)
    $summaryPeak = $peak
    # RefreshResultsAsync starts before the summary but can complete after it.
    # Eight TypeGroup UIA items mean ResultsBuilder has returned. BuildTypeGroups
    # and FillLargest run synchronously on the UI thread; the UIA response also
    # crosses a dispatch. Observe for one additional second, clearly labelled,
    # so a pass covers those result/render allocations rather than only walking.
    $uiReadySeconds = $null
    $settledSeconds = $null
    $readinessObservations = New-Object System.Collections.ArrayList
    $settlingClock = [Diagnostics.Stopwatch]::StartNew()
    $lastReadinessCheck = -1.0
    if ($null -ne $scan) {
        while ($settlingClock.Elapsed.TotalSeconds -lt 30) {
            $run.process.Refresh()
            if ($run.process.HasExited) { throw 'Scan app exited before result UI readiness.' }
            $peak = [Math]::Max($peak, $run.process.PeakWorkingSet64)
            if ($run.clock.Elapsed.TotalSeconds - $lastSample -ge 0.1) {
                $lastSample = $run.clock.Elapsed.TotalSeconds
                [void]$samples.Add(@{ seconds = $lastSample; phase = 'postSummary'; peakWorkingSetBytes = $run.process.PeakWorkingSet64; workingSetBytes = $run.process.WorkingSet64 })
            }
            if ($settlingClock.Elapsed.TotalSeconds - $lastReadinessCheck -ge 0.1) {
                $lastReadinessCheck = $settlingClock.Elapsed.TotalSeconds
                $groups = Find-Element $run.process 'TypeGroups'
                $groupCount = 0
                $groupNames = @()
                if ($null -ne $groups) {
                    $groupItems = $groups.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TypeGroup')))
                    $groupCount = $groupItems.Count
                    $groupNames = @($groupItems | ForEach-Object { $_.Current.Name })
                }
                [void]$readinessObservations.Add(@{ seconds = $run.clock.Elapsed.TotalSeconds; typeGroupCount = $groupCount; typeGroupNames = $groupNames })
                if ($groupCount -eq 8) {
                    if ($null -eq $uiReadySeconds) { $uiReadySeconds = $run.clock.Elapsed.TotalSeconds }
                    if ($run.clock.Elapsed.TotalSeconds - $uiReadySeconds -ge 1.0) {
                        $settledSeconds = $run.clock.Elapsed.TotalSeconds
                        break
                    }
                }
                else { $uiReadySeconds = $null }
            }
            Start-Sleep -Milliseconds 10
        }
    }
    $run.process.Refresh()
    $peak = [Math]::Max($peak, $run.process.PeakWorkingSet64)
    [void]$samples.Add(@{ seconds = $run.clock.Elapsed.TotalSeconds; phase = 'finalMemoryObservation'; peakWorkingSetBytes = $run.process.PeakWorkingSet64; workingSetBytes = $run.process.WorkingSet64 })
    $proof.measurements.scan = @{ seconds = $scan; boundary = 'Process.Start to observed final UIA summary'; summary = $summary; reportedFiles = $reportedFiles; summaryPeakWorkingSetBytes = $summaryPeak; summaryPeakWorkingSetBinaryMB = $summaryPeak / 1MB; settledUiPeakWorkingSetBytes = $peak; peakWorkingSetBytes = $peak; peakWorkingSetBinaryMB = $peak / 1MB; samples = @($samples.ToArray()); timeoutSeconds = 120; memoryBoundary = 'Process.Start through eight TypeGroup UIA items plus one second of observation'; uiReadySeconds = $uiReadySeconds; settledUiObservationEndedSeconds = $settledSeconds; uiReadinessTimeoutSeconds = 30; additionalUiObservationSeconds = 1.0; readinessObservations = @($readinessObservations.ToArray()) }
    $proof.gates.scan = $null -ne $scan -and $scan -le 60 -and $reportedFiles -eq 1000000 -and $summary -notmatch 'could not be read'
    $proof.gates.memory = $null -ne $scan -and $null -ne $settledSeconds -and $peak -gt 0 -and $peak -le 400MB
    Close-Lean $run.process

    Write-Host 'Measuring cancellation on a separate delayed scan.'
    $run = Start-Lean ('--scan "' + $fixture + '" --scan-delay 50')
    $cancel = $null
    $progressBeforeCancel = $null
    while ($run.clock.Elapsed.TotalSeconds -lt 30) {
        $candidate = Find-Element $run.process 'Cancel'
        $progress = Find-Element $run.process 'ScanProgress'
        $filesSeen = 0L
        if ($null -ne $progress -and $progress.Current.Name -match '\s([0-9][0-9\s,.\u00a0\u202f]*) files,') {
            $filesSeen = [long]($Matches[1] -replace '[^0-9]', '')
        }
        # Enabled alone also occurs before the first batch starts. Require actual
        # file progress, still short of completion, to prove mid-walk cancellation.
        if ($null -ne $candidate -and $candidate.Current.IsEnabled -and !$candidate.Current.IsOffscreen -and $filesSeen -gt 0 -and $filesSeen -lt 1000000) {
            $cancel = $candidate
            $progressBeforeCancel = $progress.Current.Name
            break
        }
        Start-Sleep -Milliseconds 10
    }
    if ($null -eq $cancel) { throw 'No active mid-walk Cancel button with nonzero file progress on delayed scan.' }
    $beforeCancel = (Find-Element $run.process 'Status').Current.Name
    $invoke = $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $cancelClock = [Diagnostics.Stopwatch]::StartNew()
    $invoke.Invoke()
    $cancelSeconds = $null
    $cancelSummary = $null
    while ($cancelClock.Elapsed.TotalSeconds -lt 10) {
        $status = Find-Element $run.process 'Status'
        if ($null -ne $status -and $status.Current.Name.StartsWith('Scan cancelled')) { $cancelSeconds = $cancelClock.Elapsed.TotalSeconds; $cancelSummary = $status.Current.Name; break }
        Start-Sleep -Milliseconds 10
    }
    $proof.measurements.cancel = @{ seconds = $cancelSeconds; scanDelayMilliseconds = 50; statusBefore = $beforeCancel; progressBefore = $progressBeforeCancel; filesSeenBeforeCancel = $filesSeen; summary = $cancelSummary; boundary = 'Before UIA Invoke to cancelled summary'; timeoutSeconds = 10 }
    $proof.gates.cancel = $null -ne $cancelSeconds -and $cancelSeconds -le 1 -and $beforeCancel.StartsWith('Scanning ')
    Close-Lean $run.process
    if (![LeanPerfFixture]::HasInputDesktop()) { throw 'Desktop became unavailable during the measurements.' }

    $failed = @($proof.gates.Keys | Where-Object { !$proof.gates[$_] })
    if ($failed.Count -gt 0) { throw ('Failed gates: ' + ($failed -join ', ')) }
    $proof.physicalProof = !$AllowVirtualMachine -and $ConfirmPhysicalMachine -and !$vmDetected -and $hardwareKnown
    $exitCode = 0
}
catch {
    $proof.errors += $_.Exception.Message
    Write-Warning $_.Exception.Message
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    foreach ($process in $ownedProcesses) {
        try { Close-Lean $process } catch { $proof.errors += $_.Exception.Message; $exitCode = 1; $proof.physicalProof = $false }
    }
    $proof.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $proof.success = $exitCode -eq 0
    if ($null -ne $output) {
        $jsonPath = Join-Path $output 'proof.json'
        $json = $proof | ConvertTo-Json -Depth 12
        # CreateNew protects even the result file from an unexpected overwrite.
        $stream = New-Object IO.FileStream($jsonPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $writer = New-Object IO.StreamWriter($stream, (New-Object Text.UTF8Encoding($false)))
        try { $writer.WriteLine($json) } finally { $writer.Dispose() }
        Write-Host "Proof: $jsonPath"
    }
    Write-Host ($proof.gates | ConvertTo-Json -Compress)
    if ($proof.Contains('fixture')) { Write-Host "Fixture: $($proof.fixture.verifiedFiles) files; generation $($proof.fixture.generationSeconds) s; verification $($proof.fixture.verificationSeconds) s" }
    foreach ($name in $proof.measurements.Keys) {
        Write-Host "$name seconds: $($proof.measurements[$name].seconds)"
    }
    if ($proof.measurements.Contains('scan')) { Write-Host "Peak working set: $($proof.measurements.scan.peakWorkingSetBytes) bytes ($($proof.measurements.scan.peakWorkingSetBinaryMB) binary MB)" }
    Write-Host "physicalProof=$($proof.physicalProof); informative=$($proof.informative); exit=$exitCode"
}
exit $exitCode
