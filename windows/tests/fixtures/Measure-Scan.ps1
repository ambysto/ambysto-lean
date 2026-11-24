# Times the walker on a folder: a full scan twice ("first after build" and "warm"), then a scan cancelled 40 ms in. Checks the file and logical-byte counts against the manifest that
# New-ScanFixture.ps1 wrote (<Root>.manifest.json). Informative on the test machine; the proof of intent criterion 11 runs on
# a real PC. Read-only (it builds a small console program in the temp folder for the memory line). Needs PowerShell 7 and a Release build of windows\Ambysto.Lean.slnx.
#   pwsh -File Measure-Scan.ps1 -Root C:\Users\ambytest\AmbyTestData\scan-1m
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Core = (Join-Path $PSScriptRoot '..\..\src\Ambysto.Lean.Core\bin\Release\net10.0-windows10.0.26100.0\Ambysto.Lean.Core.dll')
)
$ErrorActionPreference = 'Stop'
[void][System.Reflection.Assembly]::LoadFrom((Resolve-Path $Core).Path)
$none = [System.Threading.CancellationToken]::None
$manifestPath = $Root.TrimEnd('\') + '.manifest.json'
$manifest = if (Test-Path -LiteralPath $manifestPath) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } else { $null }
foreach ($pass in 'first after build', 'warm') {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $tree = [Ambysto.Lean.Core.Measure.Scanner]::new().ScanAsync($Root, $null, $none).GetAwaiter().GetResult()
    $scan = $clock.Elapsed.TotalSeconds
    '{0}: scan {1:N2} s, {2:N0} files, {3:N0} logical bytes, {4:N0} allocated bytes' -f $pass, $scan, $tree.Root.FileCount, $tree.Root.LogicalBytes, $tree.Root.AllocatedBytes
    if ($manifest) {
        $match = ($tree.Root.FileCount -eq $manifest.files) -and ($tree.Root.LogicalBytes -eq $manifest.logicalBytes)
        '{0}: manifest says {1:N0} files and {2:N0} logical bytes' -f $(if ($match) { 'PASS' } else { 'FAIL' }), $manifest.files, $manifest.logicalBytes
    } else {
        'no manifest at ' + $manifestPath + ': counts not checked'
    }
    $tree = $null
    [GC]::Collect()
}

# Cancel 40 ms into a scan; report the latency only when the scan really was cancelled mid-walk.
$cts = [System.Threading.CancellationTokenSource]::new()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$task = [Ambysto.Lean.Core.Measure.Scanner]::new().ScanAsync($Root, $null, $cts.Token)
Start-Sleep -Milliseconds 40
$finishedBeforeCancel = $task.IsCompleted
$requested = $clock.Elapsed
$cts.Cancel()
$tree = $task.GetAwaiter().GetResult()
$returned = $clock.Elapsed
if ($tree.Cancelled -and -not $finishedBeforeCancel) {
    'cancel: requested at {0:N0} ms, returned {1:N0} ms later, {2:N0} files measured so far' -f $requested.TotalMilliseconds, ($returned - $requested).TotalMilliseconds, $tree.Root.FileCount
} else {
    'cancel: not exercised (the scan had finished, or ignored the cancel; cancelled={0})' -f $tree.Cancelled
}

# Memory, in a plain console host (PowerShell would add its own): a tiny program is built in the temp folder, scans the
# folder once and prints the managed heap with the tree alive and the process peak.
$core = (Resolve-Path $Core).Path
$probe = Join-Path ([System.IO.Path]::GetTempPath()) 'lean-memprobe'
[void][System.IO.Directory]::CreateDirectory($probe)
Set-Content -LiteralPath (Join-Path $probe 'memprobe.csproj') -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.26100.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><Reference Include="Ambysto.Lean.Core"><HintPath>$core</HintPath></Reference></ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $probe 'Program.cs') -Value @'
using System.Diagnostics;
using Ambysto.Lean.Core.Measure;
var tree = await new Scanner().ScanAsync(args[0], null, CancellationToken.None);
var process = Process.GetCurrentProcess();
Console.WriteLine($"memory: {tree.Root.FileCount:N0} files, managed heap after the scan (tree alive) {GC.GetTotalMemory(true) / 1048576.0:N0} MB, process peak working set {process.PeakWorkingSet64 / 1048576.0:N0} MB");
GC.KeepAlive(tree);
'@
Push-Location $probe
try {
    dotnet build -c Release -nologo -v q | Out-Null
    & (Join-Path $probe 'bin\Release\net10.0-windows10.0.26100.0\memprobe.exe') $Root
} finally {
    Pop-Location
}
