; GamerGod installer — Inno Setup script
;
; Produces a single signed .exe that most Windows users already know how to run.
; The actual work is delegated to Install-GamerGod.ps1 rather than duplicated here, so there
; is exactly one description of what installing GamerGod does, and anyone can read it before
; running it. Charter Article VIII: a tool that runs as LocalSystem has to be auditable.
;
; Build:
;   1. pwsh install\Build-Installer.ps1
;   2. iscc install\gamergod.iss

#define AppName "GamerGod"
#define AppVersion "1.5.0"
#define AppPublisher "GamerGod contributors"
#define AppUrl "https://github.com/kosherplay-betatester/GamerGod-Microsoft-Sucks-Edition"
#define CliExe "gamergod.exe"

[Setup]
AppId={{7E3C1B44-9A2D-4E1F-8B77-GAMERGOD00001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\GamerGod
DefaultGroupName=GamerGod
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
InfoBeforeFile=install-notice.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=GamerGod-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; GamerGod registers a service and writes to ProgramData. Asking for elevation up front is
; more honest than failing partway through.
PrivilegesRequired=admin

; Every scheduling API GamerGod uses is 64-bit only in practice.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

MinVersion=10.0.17763

; The mark, on the setup executable itself. Without this Inno stamps its own default, which
; is the first thing anyone sees of this product and says nothing about it.
SetupIconFile=gamergod.ico

; Points at the desktop app rather than the command-line tool: this is the icon Windows shows
; in Installed apps, and the app is what a player recognises.
UninstallDisplayIcon={app}\app\GamerGod.exe
UninstallDisplayName={#AppName} {#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-GamerGod.ps1"; DestDir: "{app}\install"; Flags: ignoreversion
Source: "Uninstall-GamerGod.ps1"; DestDir: "{app}\install"; Flags: ignoreversion
Source: "..\CHARTER.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\GamerGod"; Filename: "{app}\app\GamerGod.exe"; WorkingDir: "{app}\app"
Name: "{autodesktop}\GamerGod"; Filename: "{app}\app\GamerGod.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon
Name: "{group}\Check my machine"; Filename: "{app}\{#CliExe}"; Parameters: "scan"
Name: "{group}\What GamerGod will never do"; Filename: "{app}\CHARTER.md"
Name: "{group}\Uninstall GamerGod"; Filename: "{uninstallexe}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Run]
; The PowerShell script is the single source of truth for what installation changes.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\Install-GamerGod.ps1"" -SourceRoot ""{app}"" -InstallRoot ""{app}"""; \
  StatusMsg: "Registering GamerGod..."; \
  Flags: runhidden waituntilterminated

Filename: "{app}\app\GamerGod.exe"; \
  Description: "Open GamerGod"; \
  WorkingDir: "{app}\app"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
; Runs BEFORE files are removed. The machine is restored first, because deleting GamerGod
; while a session is active would remove the only thing that knows how to undo it.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\Uninstall-GamerGod.ps1"" -Force"; \
  RunOnceId: "GamerGodRestore"; \
  Flags: runhidden waituntilterminated

[Code]
function InitializeSetup(): Boolean;
var
  Reply: Integer;
begin
  Reply := MsgBox(
    'GamerGod changes nothing about how Windows runs until you turn it on.' + #13#10 + #13#10 +
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
