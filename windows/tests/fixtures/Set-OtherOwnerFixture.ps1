# ASP-7 criterion 7 / PreviewGapTests.FolderItemOwnedBySomeoneElseIsRefusedButAReadableFileIsNot.
# Run elevated once with -TestUser '<machine>\ambytest' -ProfilePath 'C:\Users\ambytest'.
# Run the test as that standard user, then run this script elevated with the same arguments and -Remove.
# Creates a fresh, dedicated fixture only; refuses an existing path. No machine/user settings are changed.
[CmdletBinding()]
param(
    [string]$TestUser = "$env:COMPUTERNAME\ambytest",
    [string]$ProfilePath = 'C:\Users\ambytest',
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal $identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this fixture script elevated; run the acceptance test as the standard user.' }
$testSid = ([Security.Principal.NTAccount]$TestUser).Translate([Security.Principal.SecurityIdentifier])
$admins = [Security.Principal.SecurityIdentifier]'S-1-5-32-544'
$system = [Security.Principal.SecurityIdentifier]'S-1-5-18'
$volumeRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($ProfilePath))
$root = Join-Path $volumeRoot 'LeanRulesAdminFixture'
$marker = Join-Path $root '.ambysto-other-owner-fixture'
$signature = 'ASP7-other-owner-v1:' + $testSid.Value
if ($Remove) {
    if (-not (Test-Path -LiteralPath $root)) { Write-Output "Fixture already absent: $root"; exit 0 }
    if ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a reparse-point fixture root.' }
    if (-not (Test-Path -LiteralPath $marker) -or [IO.File]::ReadAllText($marker) -ne $signature) { throw 'Refusing removal: fixture marker/user does not match.' }
    $allowed = @('.ambysto-other-owner-fixture', 'AdminFolder', 'admin-file.bin')
    foreach ($entry in Get-ChildItem -LiteralPath $root -Force) {
        if ($entry.Name -notin $allowed -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Unexpected fixture entry; preserving it: $($entry.FullName)" }
    }
    $folder = Join-Path $root 'AdminFolder'
    if (Test-Path -LiteralPath $folder) {
        if (@(Get-ChildItem -LiteralPath $folder -Force).Count -ne 0) { throw 'AdminFolder is not empty; preserving fixture.' }
        Remove-Item -LiteralPath $folder -Force
    }
    $file = Join-Path $root 'admin-file.bin'
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    Remove-Item -LiteralPath $marker -Force
    Remove-Item -LiteralPath $root -Force
    Write-Output "Removed isolated fixture: $root"
    exit 0
}
if (Test-Path -LiteralPath $root) { throw "Fixture path already exists; preserving it: $root" }
if (-not (Test-Path -LiteralPath $ProfilePath -PathType Container)) { throw 'The standard-user profile must already exist.' }
[void][IO.Directory]::CreateDirectory($root)
try {
    [IO.File]::WriteAllText($marker, $signature)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($admins, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($testSid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    $acl.SetOwner($admins)
    Set-Acl -LiteralPath $root -AclObject $acl
    $folder = Join-Path $root 'AdminFolder'
    [void][IO.Directory]::CreateDirectory($folder)
    $file = Join-Path $root 'admin-file.bin'
    [IO.File]::WriteAllBytes($file, [byte[]]::new(4096))
    foreach ($path in @($folder, $file)) {
        $childAcl = Get-Acl -LiteralPath $path
        $childAcl.SetOwner($admins)
        Set-Acl -LiteralPath $path -AclObject $childAcl
    }
    # icacls enables the restore privilege required to assign ownership to a different user; no recursive /T.
    & icacls.exe $root /setowner ('*' + $testSid.Value) | Write-Output
    if ($LASTEXITCODE -ne 0) { throw 'Could not assign the fixture top folder to the standard user.' }
    foreach ($path in @($root, $folder, $file)) {
        $owner = (Get-Acl -LiteralPath $path).GetOwner([Security.Principal.SecurityIdentifier]).Value
        $expected = if ($path -eq $root) { $testSid.Value } else { $admins.Value }
        if ($owner -ne $expected) { throw "Unexpected owner of $path : $owner" }
        Write-Output "$path owner=$owner"
    }
    Write-Output "Run PreviewGapTests.FolderItemOwnedBySomeoneElseIsRefusedButAReadableFileIsNot as $TestUser, then remove with this script -Remove."
} catch {
    # Only our newly created fixture exists here. Keep it inspectable and provide an explicit cleanup command.
    Write-Warning "Fixture setup failed; rerun this script with -Remove after inspecting $root."
    throw
}
exit 0
