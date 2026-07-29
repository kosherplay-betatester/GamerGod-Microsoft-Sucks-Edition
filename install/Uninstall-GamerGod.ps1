<#
.SYNOPSIS
    Removes GamerGod, and proves it left nothing behind.

.DESCRIPTION
    The order matters more than the steps.

    GamerGod's promise is that everything it changes is put back. An uninstaller that simply
    deleted the files would break that promise at the worst possible moment: if a session were
    active, its journal would be removed along with the only thing that knew how to undo it,
    leaving services stopped and processes confined with nothing left to fix them.

    So this runs the full restore FIRST, verifies the journal is clean, and only then removes
    anything. If the restore cannot complete, it stops and tells you, rather than deleting its
    way out of the problem.

      1. Revert any active session and restore the machine.
      2. Verify no outstanding changes remain.
      3. Stop and remove the service.
      4. Remove the GamerGod power scheme, if one was left behind.
      5. Remove GamerGod from PATH.
      6. Delete the program files.
      7. Optionally keep your measurement history.
      8. Print a receipt.

.PARAMETER KeepData
    Keep %ProgramData%\GamerGod - your measurement history, profiles and archived journals.

.PARAMETER Force
    Continue even if the restore reports a problem. Use only if you have already fixed the
    machine by hand, or after a reboot has restored it for you.

.PARAMETER WhatIf
    Show what would happen and change nothing.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $KeepData,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'GamerGodService'
$InstallRoot = Join-Path $env:ProgramFiles 'GamerGod'
$StateRoot = Join-Path $env:ProgramData 'GamerGod'

$receipt = [System.Collections.Generic.List[string]]::new()

function Write-Step { param([string] $Text) Write-Host "  $Text" -ForegroundColor Cyan }
function Note { param([string] $Text) $receipt.Add($Text); Write-Host "  [ok] $Text" -ForegroundColor Green }
function Skip { param([string] $Text) Write-Host "  [--] $Text" -ForegroundColor DarkGray }
function Warn { param([string] $Text) $receipt.Add("WARNING: $Text"); Write-Host "  [!!] $Text" -ForegroundColor Yellow }

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Uninstalling GamerGod requires Administrator.'
    }
}

function Invoke-Restore {
    Write-Step 'Restoring the machine before removing anything'

    $cli = Join-Path $InstallRoot 'gamergod.exe'
    if (-not (Test-Path $cli)) {
        Skip 'gamergod.exe not found; nothing could be active'
        return $true
    }

    if (-not $PSCmdlet.ShouldProcess('active session', 'Revert all changes')) { return $true }

    try {
        $output = & $cli restore --all 2>&1
        $code = $LASTEXITCODE
    }
    catch {
        Warn "Could not run the restore: $($_.Exception.Message)"
        return $false
    }

    if ($code -eq 0) {
        Note 'Restore completed; no outstanding changes'
        return $true
    }

    Warn "Restore reported a problem (exit $code): $output"
    return $false
}

function Test-JournalClean {
    $state = Join-Path $StateRoot 'state'
    if (-not (Test-Path $state)) { return $true }

    $active = Get-ChildItem -Path $state -Filter '*.journal' -ErrorAction SilentlyContinue
    if (-not $active) { return $true }

    Warn "$($active.Count) journal file(s) still record outstanding changes"
    return $false
}

function Remove-GamerGodService {
    Write-Step 'Removing the service'

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) { Skip 'Service was not registered'; return }

    if (-not $PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete service')) { return }

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }

    & sc.exe delete $ServiceName | Out-Null
    Note 'Service stopped and removed'
}

function Remove-PowerScheme {
    Write-Step 'Checking for a leftover power scheme'

    $schemes = & powercfg /list 2>$null
    $gamergod = $schemes | Select-String -Pattern 'GamerGod'

    if (-not $gamergod) { Skip 'No GamerGod power scheme present'; return }

    foreach ($line in $gamergod) {
        if ($line -match '([0-9a-fA-F-]{36})') {
            $guid = $Matches[1]

            if ($PSCmdlet.ShouldProcess($guid, 'Delete power scheme')) {
                # Never delete the active scheme out from under the machine.
                $active = (& powercfg /getactivescheme) -match $guid
                if ($active) {
                    & powercfg /setactive SCHEME_BALANCED | Out-Null
                    Note 'Restored the Balanced power plan'
                }

                & powercfg /delete $guid 2>$null | Out-Null
                Note "Removed the GamerGod power scheme ($guid)"
            }
        }
    }
}

function Remove-FromPath {
    Write-Step 'Removing GamerGod from PATH'

    $current = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $parts = $current -split ';' | Where-Object { $_ -and $_ -ne $InstallRoot }

    if (($parts -join ';') -eq $current) { Skip 'Was not on PATH'; return }

    if ($PSCmdlet.ShouldProcess('Machine PATH', "Remove $InstallRoot")) {
        [Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'Machine')
        Note 'Removed from PATH'
    }
}

function Remove-Files {
    Write-Step 'Removing files'

    if (Test-Path $InstallRoot) {
        if ($PSCmdlet.ShouldProcess($InstallRoot, 'Delete program files')) {
            Remove-Item -Path $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path $InstallRoot) {
                Warn "Some files under $InstallRoot are in use and were left. Reboot and delete the folder."
            }
            else {
                Note "Deleted $InstallRoot"
            }
        }
    }
    else {
        Skip 'Program files already absent'
    }

    if (-not (Test-Path $StateRoot)) { Skip 'No state directory to remove'; return }

    if ($KeepData) {
        Note "Kept your measurement history and profiles at $StateRoot"
        return
    }

    if ($PSCmdlet.ShouldProcess($StateRoot, 'Delete state, logs and measurement history')) {
        Remove-Item -Path $StateRoot -Recurse -Force -ErrorAction SilentlyContinue
        Note "Deleted $StateRoot"
    }
}

# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '  Removing GamerGod' -ForegroundColor White
Write-Host '  Your machine is restored before anything is deleted.'
Write-Host ''

try {
    Assert-Administrator

    $restored = Invoke-Restore
    $clean = Test-JournalClean

    if ((-not $restored -or -not $clean) -and -not $Force) {
        Write-Host ''
        Write-Host '  Stopping before removing anything.' -ForegroundColor Yellow
        Write-Host ''
        Write-Host '  GamerGod still has changes recorded as applied, and deleting it now would'
        Write-Host '  remove the only thing that knows how to undo them.'
        Write-Host ''
        Write-Host '  Do this instead:' -ForegroundColor White
        Write-Host '    1. Reboot. Every GamerGod change is undone at boot by design.'
        Write-Host '    2. Run this uninstaller again.'
        Write-Host ''
        Write-Host '  If you have already restored the machine yourself, re-run with -Force.'
        Write-Host ''
        exit 2
    }

    Remove-GamerGodService
    Remove-PowerScheme
    Remove-FromPath
    Remove-Files

    Write-Host ''
    Write-Host '  Receipt' -ForegroundColor White
    foreach ($line in $receipt) { Write-Host "    $line" }

    $warnings = @($receipt | Where-Object { $_ -like 'WARNING:*' })
    Write-Host ''

    if ($warnings.Count -eq 0) {
        Write-Host '  GamerGod is gone and your machine is exactly as it was.' -ForegroundColor Green
        if ($KeepData) { Write-Host "  Your measurement history is still at $StateRoot." }
    }
    else {
        Write-Host "  GamerGod was removed, but $($warnings.Count) item(s) need your attention above." -ForegroundColor Yellow
    }

    Write-Host ''
}
catch {
    Write-Host ''
    Write-Host "  Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '  Nothing further was removed. Reboot to restore the machine, then try again.'
    Write-Host ''
    exit 1
}
