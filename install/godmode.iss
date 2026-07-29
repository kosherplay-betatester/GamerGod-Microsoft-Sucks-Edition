; GodMode installer — Inno Setup script
;
; Produces a single signed .exe that most Windows users already know how to run.
; The actual work is delegated to Install-GodMode.ps1 rather than duplicated here, so there
; is exactly one description of what installing GodMode does, and anyone can read it before
; running it. Charter Article VIII: a tool that runs as LocalSystem has to be auditable.
;
; Build:
;   1. pwsh install\Build-Installer.ps1
;   2. iscc install\godmode.iss

#define AppName "GodMode"
#define AppVersion "0.1.0"
#define AppPublisher "GodMode contributors"
#define AppUrl "https://github.com/kosherplay-betatester/GodMode-Microsoft-Sucks-Edition"
#define CliExe "godmode.exe"

[Setup]
AppId={{7E3C1B44-9A2D-4E1F-8B77-GODMODE00001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\GodMode
DefaultGroupName=GodMode
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
InfoBeforeFile=install-notice.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=GodMode-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; GodMode registers a service and writes to ProgramData. Asking for elevation up front is
; more honest than failing partway through.
PrivilegesRequired=admin

; Every scheduling API GodMode uses is 64-bit only in practice.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

MinVersion=10.0.17763
UninstallDisplayIcon={app}\{#CliExe}
UninstallDisplayName={#AppName} {#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-GodMode.ps1"; DestDir: "{app}\install"; Flags: ignoreversion
Source: "Uninstall-GodMode.ps1"; DestDir: "{app}\install"; Flags: ignoreversion
Source: "..\CHARTER.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\GodMode"; Filename: "{app}\{#CliExe}"; Parameters: "scan"
Name: "{group}\What GodMode will never do"; Filename: "{app}\CHARTER.md"
Name: "{group}\Uninstall GodMode"; Filename: "{uninstallexe}"

[Run]
; The PowerShell script is the single source of truth for what installation changes.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\Install-GodMode.ps1"" -SourceRoot ""{app}"" -InstallRoot ""{app}"""; \
  StatusMsg: "Registering GodMode..."; \
  Flags: runhidden waituntilterminated

Filename: "{app}\{#CliExe}"; Parameters: "scan"; \
  Description: "Check this machine for things that cost you frames (changes nothing)"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
; Runs BEFORE files are removed. The machine is restored first, because deleting GodMode
; while a session is active would remove the only thing that knows how to undo it.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\Uninstall-GodMode.ps1"" -Force"; \
  RunOnceId: "GodModeRestore"; \
  Flags: runhidden waituntilterminated

[Code]
function InitializeSetup(): Boolean;
var
  Reply: Integer;
begin
  Reply := MsgBox(
    'GodMode changes nothing about how Windows runs until you turn it on.' + #13#10 + #13#10 +
    'Installing it will:' + #13#10 +
    '  - copy program files' + #13#10 +
    '  - create a state folder that only administrators can write to' + #13#10 +
    '  - register a background service that restores your machine after a crash' + #13#10 + #13#10 +
    'It will not disable your antivirus, edit your boot configuration, remove Windows ' +
    'components, or send anything anywhere. Ever.' + #13#10 + #13#10 +
    'Continue?',
    mbConfirmation, MB_YESNO);

  Result := Reply = IDYES;
end;
