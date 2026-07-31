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
#define AppVersion "1.6.2"
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
; Registration is NOT here. The PowerShell script is still the single source of truth for what
; installing GamerGod changes, but it is run from CurStepChanged below, because Inno ignores a
; [Run] entry's exit code entirely — and it ignored this one while the script was failing before
; it ever reached the part that registers the background service. Setup reported success and the
; machine was left with the binaries installed, no service, and no indication anything was wrong.
; An installation step whose failure nobody looks at is not a step.
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
{
  Runs the registration script and refuses to call the install a success if it failed.

  Replaces the [Run] entry above for the purpose of noticing. Everything that makes GamerGod
  more than a folder of executables happens in that script — the state directory and its ACL,
  the service registration, PATH — and all of it was skipped silently when the script threw.
}
procedure RegisterGamerGod();
var
  Code: Integer;
  Shell, Script, Root, LogPath, Message: String;
begin
  Shell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Script := ExpandConstant('{app}\install\Install-GamerGod.ps1');
  Root := ExpandConstant('{app}');
  LogPath := ExpandConstant('{app}\install\registration.log');

  { Run through cmd so the transcript is kept. The script prints exactly what it changed —
    service, state directory ACL, PATH — and when it fails, that output is the only thing that
    says why. Without it a failed registration is a number. }
  if not Exec(
       ExpandConstant('{cmd}'),
       '/C ""' + Shell + '" -NoProfile -ExecutionPolicy Bypass -File "' + Script + '"'
         + ' -SourceRoot "' + Root + '" -InstallRoot "' + Root + '"'
         + ' > "' + LogPath + '" 2>&1"',
       '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    RaiseException('GamerGod could not be registered: the registration step would not start.');
  end;

  if Code <> 0 then
  begin
    Message :=
      'GamerGod could not be registered (the registration step exited with code '
      + IntToStr(Code) + ').' #13#10 #13#10
      + 'The program files are installed, but the background service that restores your machine '
      + 'after a crash is not registered, and the state folder may not be locked down.' #13#10 #13#10
      + 'What went wrong is written here:' #13#10
      + '  ' + LogPath + #13#10 #13#10
      + 'You can retry it from an administrator PowerShell:' #13#10
      + '  & "' + Script + '" -SourceRoot "' + Root + '" -InstallRoot "' + Root + '"';

    { RaiseException aborts an interactive install, and under /SUPPRESSMSGBOXES it does not —
      the message box is answered OK for us and Setup carries on to report success. A silent
      install that leaves no service must still exit non-zero, or every unattended deployment
      of this thing is broken and says it is fine. }
    if WizardSilent() then
    begin
      Log('GamerGod registration failed: ' + Message);
      Abort();
    end;

    RaiseException(Message);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterGamerGod();
end;

{
  The disclosure below is shown before anything is copied, because somebody installing a tool
  that runs as LocalSystem deserves to read what it will do before it does it.

  It must not be shown when nobody is there to read it. /SUPPRESSMSGBOXES suppresses Setup's own
  message boxes and has no effect on a MsgBox called from [Code], so an unattended install sat on
  this dialog for ever — and under /VERYSILENT, with no progress window to explain itself, the
  only symptom was an installer process that never exited. Found by running exactly that: a
  scripted upgrade that hung until it was killed.

  Silent means consent has already been given, on the command line, by whoever typed it.
}
function InitializeSetup(): Boolean;
var
  Reply: Integer;
begin
  if WizardSilent() then
  begin
    Result := True;
    Exit;
  end;

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
