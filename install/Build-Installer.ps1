<#
.SYNOPSIS
    Publishes GamerGod and stages the installer payload.

.DESCRIPTION
    Runs the whole gate in order: test, publish, stage. The tests run first and a failure
    stops the build, because a release that skipped its own safety suite is exactly the
    release that should not exist.

.PARAMETER SkipTests
    Skip the test run. For local iteration only; CI must never use this.

.PARAMETER Configuration
    Build configuration. Defaults to Release.
#>
[CmdletBinding()]
param(
    [switch] $SkipTests,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$payload = Join-Path $PSScriptRoot 'payload'
$artifacts = Join-Path $root 'artifacts'

function Step { param([string] $Text) Write-Host "`n  $Text" -ForegroundColor Cyan }

Step 'Cleaning previous payload'
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $artifacts 'installer') | Out-Null

if (-not $SkipTests) {
    Step 'Running the safety suite'
    & dotnet test (Join-Path $root 'GamerGod.slnx') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Tests failed. The build stops here - a release that skipped its safety suite is the one that should not exist.'
    }
    Write-Host '  All tests passed' -ForegroundColor Green
}
else {
    Write-Host '  [!!] Tests skipped. Never do this for a release.' -ForegroundColor Yellow
}

Step 'Publishing the command-line tool'
# Self-contained: a player should be able to run GamerGod without first being told to
# install a .NET runtime. The size cost is worth not losing people at step one.
& dotnet publish (Join-Path $root 'src\GamerGod.Cli\GamerGod.Cli.csproj') `
    -c $Configuration `
    -o $payload `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The desktop app goes in its own subfolder, and that is not cosmetic. The command is
# gamergod.exe and the app is GamerGod.exe - identical filenames on a case-insensitive
# filesystem, so in one folder the second publish would silently overwrite the first and the
# installer would ship one program under two names.
Step 'Publishing the desktop app'
$appPayload = Join-Path $payload 'app'
New-Item -ItemType Directory -Force -Path $appPayload | Out-Null
& dotnet publish (Join-Path $root 'src\GamerGod.Ui\GamerGod.Ui.csproj') `
    -c $Configuration `
    -o $appPayload `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'Desktop app publish failed.' }

# The service ships later; stage it automatically once it exists so this script does not
# need editing on the day it lands.
$servicePath = Join-Path $root 'src\GamerGod.Service\GamerGod.Service.csproj'
if (Test-Path $servicePath) {
    Step 'Publishing the background service'
    & dotnet publish $servicePath -c $Configuration -r win-x64 --self-contained false -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
}
else {
    Write-Host '  [--] Background service not built yet; installer will register the CLI only' -ForegroundColor DarkGray
}

Step 'Payload staged'
Get-ChildItem $payload -File | Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } |
    Format-Table -AutoSize | Out-String | Write-Host

$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if ($iscc) {
    Step 'Compiling the installer'
    & $iscc.Source (Join-Path $PSScriptRoot 'gamergod.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    Write-Host "`n  Installer written to $artifacts\installer" -ForegroundColor Green
}
else {
    Write-Host "`n  Inno Setup not found, so no .exe was produced." -ForegroundColor Yellow
    Write-Host '  Install it with:  winget install JRSoftware.InnoSetup'
    Write-Host '  Or install directly without an installer:'
    Write-Host "      pwsh $PSScriptRoot\Install-GamerGod.ps1" -ForegroundColor Cyan
}

Write-Host ''
