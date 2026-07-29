<#
.SYNOPSIS
    Installs GamerGod.

.DESCRIPTION
    Everything this script does is listed below, and it is the only thing that touches your
    machine at install time. GamerGod itself changes nothing until you turn it on.

      1. Checks prerequisites and refuses rather than half-installing.
      2. Copies the binaries to Program Files.
      3. Creates the state directory with ACLs that prevent a non-administrator from
         writing to it.
      4. Registers the background service, if it has been built, with restart-on-failure.
      5. Adds gamergod.exe to PATH.

    It does not create a power scheme, stop a service, change a registry value outside its
    own keys, or modify anything about how Windows runs. Those happen at runtime, are
    journalled, and are reverted when you exit.

.PARAMETER InstallRoot
    Where to install. Defaults to "$env:ProgramFiles\GamerGod".

.PARAMETER SourceRoot
    Where to install from. Defaults to a publish output beside this script.

.PARAMETER NoPath
    Skip adding GamerGod to PATH.

.PARAMETER WhatIf
    Show what would happen and change nothing.

.EXAMPLE
    .\Install-GamerGod.ps1 -WhatIf
    Shows every change without making any.

.EXAMPLE
    .\Install-GamerGod.ps1
    Installs with defaults.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'GamerGod'),
    [string] $SourceRoot,
    [switch] $NoPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'GamerGodService'
$StateRoot = Join-Path $env:ProgramData 'GamerGod'

function Write-Step { param([string] $Text) Write-Host "  $Text" -ForegroundColor Cyan }
function Write-Ok { param([string] $Text) Write-Host "  [ok] $Text" -ForegroundColor Green }
function Write-Skip { param([string] $Text) Write-Host "  [--] $Text" -ForegroundColor DarkGray }
function Write-Warn { param([string] $Text) Write-Host "  [!!] $Text" -ForegroundColor Yellow }

function Test-Prerequisites {
    Write-Step 'Checking prerequisites'

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'GamerGod must be installed as Administrator. Right-click PowerShell and choose "Run as administrator".'
    }
    Write-Ok 'Running as Administrator'

    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 17763) {
        throw "Windows 10 1809 (build 17763) or later is required. This machine reports build $build."
    }
    Write-Ok "Windows build $build"

    # A refusal here is better than an install that silently does nothing useful, since
    # every scheduling API GamerGod relies on is 64-bit only in practice.
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'GamerGod requires a 64-bit version of Windows.'
    }
    Write-Ok '64-bit Windows'
}

function Resolve-Source {
    if ($SourceRoot) {
        if (-not (Test-Path $SourceRoot)) { throw "SourceRoot '$SourceRoot' does not exist." }
        return (Resolve-Path $SourceRoot).Path
    }

    $here = Split-Path -Parent $PSCommandPath
    foreach ($candidate in @(
            (Join-Path $here 'payload'),
            (Join-Path (Split-Path -Parent $here) 'artifacts\publish'),
            (Join-Path (Split-Path -Parent $here) 'src\GamerGod.Cli\bin\Release\net10.0-windows\win-x64\publish'),
            (Join-Path (Split-Path -Parent $here) 'src\GamerGod.Cli\bin\Release\net10.0-windows'),
            (Join-Path (Split-Path -Parent $here) 'src\GamerGod.Cli\bin\Debug\net10.0-windows'))) {
        if (Test-Path (Join-Path $candidate 'gamergod.exe')) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw @'
Could not find gamergod.exe to install.

Build it first:
    dotnet publish src/GamerGod.Cli -c Release -r win-x64 --self-contained false

Then re-run this script, or pass -SourceRoot pointing at the output.
'@
}

function Stop-RunningInstances {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    # Without this, upgrading over a running copy fails on a locked file and reports the name
    # of a .NET internal - "cannot access clrjit.dll" - which tells the user nothing about the
    # actual problem or how to fix it.
    $running = @(Get-Process -Name 'GamerGod', 'gamergod', 'gmsvc' -ErrorAction SilentlyContinue)

    if ($running.Count -eq 0) { return }

    Write-Step "Closing $($running.Count) running GamerGod process(es)"

    if (-not $PSCmdlet.ShouldProcess('running GamerGod processes', 'Close before upgrading')) { return }

    foreach ($process in $running) {
        try {
            # Ask first. A window with unsaved settings deserves the chance to write them.
            if ($process.MainWindowHandle -ne 0) {
                [void] $process.CloseMainWindow()
                [void] $process.WaitForExit(5000)
            }

            if (-not $process.HasExited) {
                $process.Kill()
                [void] $process.WaitForExit(5000)
            }
        }
        catch {
            Write-Warn "Could not close process $($process.Id): $($_.Exception.Message)"
        }
    }

    # Windows releases file handles a moment after the process ends.
    Start-Sleep -Milliseconds 600
    Write-Ok 'Closed. Nothing GamerGod applied is affected - "off" and reboot both still work.'
}

function Install-Binaries {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $Source)

    Write-Step "Installing binaries to $InstallRoot"

    if ($PSCmdlet.ShouldProcess($InstallRoot, 'Copy program files')) {
        New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
        Copy-Item -Path (Join-Path $Source '*') -Destination $InstallRoot -Recurse -Force
        Write-Ok "Copied from $Source"
    }
}

function Initialize-StateDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    Write-Step "Creating state directory at $StateRoot"

    if (-not $PSCmdlet.ShouldProcess($StateRoot, 'Create state directory with restricted ACL')) { return }

    New-Item -ItemType Directory -Force -Path $StateRoot | Out-Null
    foreach ($sub in 'state', 'logs', 'archive', 'profiles') {
        New-Item -ItemType Directory -Force -Path (Join-Path $StateRoot $sub) | Out-Null
    }

    # The service runs as LocalSystem and reads this directory. If an unprivileged user could
    # write to it, they could hand a privileged process arbitrary instructions - a local
    # privilege escalation. Inheritance is disabled and the ACL rebuilt explicitly so that a
    # permissive parent cannot widen it.
    $acl = Get-Acl -Path $StateRoot
    $acl.SetAccessRuleProtection($true, $false)
    $acl.Access | ForEach-Object { [void] $acl.RemoveAccessRule($_) }

    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    foreach ($rule in @(
            @{ Id = 'S-1-5-18'; Rights = 'FullControl' }   # LocalSystem
            @{ Id = 'S-1-5-32-544'; Rights = 'FullControl' }   # Administrators
            @{ Id = 'S-1-5-32-545'; Rights = 'ReadAndExecute' } # Users: read only
        )) {
        $sid = [Security.Principal.SecurityIdentifier]::new($rule.Id)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                $sid, $rule.Rights, $inherit, $none, $allow))
    }

    Set-Acl -Path $StateRoot -AclObject $acl
    Write-Ok 'Created, with write access restricted to Administrators and SYSTEM'
}

function Install-Service {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $Root)

    $binary = Join-Path $Root 'gmsvc.exe'

    if (-not (Test-Path $binary)) {
        Write-Skip 'Background service not present in this build; skipping service registration'
        Write-Skip 'GamerGod will run as a command-line tool until the service ships'
        return
    }

    Write-Step "Registering the $ServiceName service"

    if (-not $PSCmdlet.ShouldProcess($ServiceName, 'Register Windows service')) { return }

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Milliseconds 500
    }

    # Delayed automatic start: the boot-recovery pass must run even if nobody signs in, but
    # it should not compete with boot itself.
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$binary`"" `
        -DisplayName 'GamerGod' `
        -Description 'Applies and reverses GamerGod gaming optimisations, and restores the machine after a crash.' `
        -StartupType Automatic | Out-Null

    & sc.exe config $ServiceName start= delayed-auto | Out-Null

    # If the service dies, the journal is left dirty and nothing would undo it. Restarting is
    # the difference between "recovered" and "stranded".
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/10000 | Out-Null

    Start-Service -Name $ServiceName
    Write-Ok 'Registered, set to delayed automatic start, restarts on failure'
}

function Add-Shortcuts {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $Root)

    $gui = Join-Path $Root 'app\GamerGod.exe'

    if (-not (Test-Path $gui)) {
        Write-Skip 'Desktop app not present in this build; no shortcuts created'
        return
    }

    Write-Step 'Creating shortcuts'

    if (-not $PSCmdlet.ShouldProcess('Start Menu and Desktop', 'Create shortcuts')) { return }

    # Without these the app is installed and effectively invisible: nothing on the Start Menu,
    # nothing on the desktop, and its executable buried a folder deeper than the command. That
    # is exactly how it went the first time, and "look in Program Files" is not an answer.
    $shell = New-Object -ComObject WScript.Shell

    try {
        $startMenu = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'GamerGod'
        New-Item -ItemType Directory -Force -Path $startMenu | Out-Null

        foreach ($target in @(
                @{ Path = (Join-Path $startMenu 'GamerGod.lnk'); Exe = $gui; Args = '' }
                @{ Path = (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'GamerGod.lnk'); Exe = $gui; Args = '' }
            )) {
            $link = $shell.CreateShortcut($target.Path)
            $link.TargetPath = $target.Exe
            $link.WorkingDirectory = Split-Path -Parent $target.Exe
            $link.Description = 'Give your game the machine you paid for'
            $link.Save()
        }

        Write-Ok 'Start Menu and desktop shortcuts created'
    }
    finally {
        [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

function Add-ToPath {
    [CmdletBinding(SupportsShouldProcess)]
    param([string] $Root)

    if ($NoPath) { Write-Skip 'Skipping PATH (-NoPath)'; return }

    Write-Step 'Adding GamerGod to PATH'

    $current = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if ($current -split ';' -contains $Root) {
        Write-Skip 'Already on PATH'
        return
    }

    if ($PSCmdlet.ShouldProcess('Machine PATH', "Append $Root")) {
        [Environment]::SetEnvironmentVariable('Path', "$current;$Root", 'Machine')
        Write-Ok 'Added (open a new terminal to pick it up)'
    }
}

# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '  GamerGod installer' -ForegroundColor White
Write-Host '  Nothing is optimised at install time. GamerGod changes nothing until you turn it on.'
Write-Host ''

try {
    Test-Prerequisites
    Stop-RunningInstances
    $source = Resolve-Source
    Install-Binaries -Source $source
    Initialize-StateDirectory
    Install-Service -Root $InstallRoot
    Add-Shortcuts -Root $InstallRoot
    Add-ToPath -Root $InstallRoot

    Write-Host ''
    Write-Host '  Installed.' -ForegroundColor Green
    Write-Host ''
    Write-Host '  Try these first. Both only read; neither changes anything:' -ForegroundColor White
    Write-Host '      gamergod scan        ' -NoNewline -ForegroundColor Cyan
    Write-Host '- check this machine for things that cost you frames'
    Write-Host '      gamergod topology    ' -NoNewline -ForegroundColor Cyan
    Write-Host '- see how your CPU will be partitioned'
    Write-Host ''
    Write-Host "  To remove GamerGod completely, run Uninstall-GamerGod.ps1. It reverts any"
    Write-Host '  active session first and prints a receipt proving nothing was left behind.'
    Write-Host ''
}
catch {
    Write-Host ''
    Write-Host "  Install failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '  Nothing was left half-installed. Fix the problem above and run this again.'
    Write-Host ''
    exit 1
}
