# Builds a tree of Folders x FilesPerFolder small files for walker timings (plan group 4; intent criterion 11 uses the
# same tree on a real PC). Sizes cycle through 0, 100, 1500, 4096 and 6000 bytes, so resident, one-cluster and
# two-cluster files all occur. Writes only inside -Root, which must not exist yet or be empty, and the manifest
# <Root>.manifest.json beside it (files, folders and logical bytes, for Measure-Scan.ps1 to check a scan against); give it a
# scratch path (on the test machine: under $env:AMBYSTO_TESTDATA). Needs PowerShell 7.
#   pwsh -File New-ScanFixture.ps1 -Root C:\Users\ambytest\AmbyTestData\scan-1m [-Folders 1000] [-FilesPerFolder 1000]
param(
    [Parameter(Mandatory)][string]$Root,
    [int]$Folders = 1000,
    [int]$FilesPerFolder = 1000
)
$ErrorActionPreference = 'Stop'
$Root = [System.IO.Path]::GetFullPath($Root)
if ((Test-Path -LiteralPath $Root) -and (Get-ChildItem -LiteralPath $Root -Force | Select-Object -First 1)) {
    throw "$Root exists and is not empty"
}
[void][System.IO.Directory]::CreateDirectory($Root)
$clock = [System.Diagnostics.Stopwatch]::StartNew()
1..$Folders | ForEach-Object -ThrottleLimit 8 -Parallel {
    $sizes = 0, 100, 1500, 4096, 6000
    $bytes = [byte[]]::new(6000)
    $folder = [System.IO.Path]::Combine($using:Root, ('d{0:D4}' -f $_))
    [void][System.IO.Directory]::CreateDirectory($folder)
    for ($i = 0; $i -lt $using:FilesPerFolder; $i++) {
        $stream = [System.IO.File]::Create([System.IO.Path]::Combine($folder, ('f{0:D4}.dat' -f $i)))
        $stream.Write($bytes, 0, $sizes[$i % 5])
        $stream.Dispose()
    }
}
$sizes = 0, 100, 1500, 4096, 6000
$logical = 0L
for ($i = 0; $i -lt $FilesPerFolder; $i++) { $logical += $sizes[$i % 5] }
[ordered]@{ files = [long]$Folders * $FilesPerFolder; folders = $Folders; logicalBytes = $logical * $Folders } | ConvertTo-Json | Set-Content -LiteralPath ($Root + '.manifest.json')
'built {0:N0} files in {1:N0} folders under {2} in {3:N0} s' -f ($Folders * $FilesPerFolder), $Folders, $Root, $clock.Elapsed.TotalSeconds
