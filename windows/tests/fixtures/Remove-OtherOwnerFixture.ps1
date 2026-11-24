# Gates teardown for Set-OtherOwnerFixture.ps1; same elevated identity and arguments as setup.
[CmdletBinding()]
param(
    [string]$TestUser = "$env:COMPUTERNAME\ambytest",
    [string]$ProfilePath = 'C:\Users\ambytest'
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Set-OtherOwnerFixture.ps1') -TestUser $TestUser -ProfilePath $ProfilePath -Remove
if ($LASTEXITCODE -ne 0) { throw "Other-owner fixture teardown failed with exit code $LASTEXITCODE" }
exit 0
