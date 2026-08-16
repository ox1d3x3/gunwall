; GunWall installer — Inno Setup 7.x (6.3+ also works)
;
; Build:  iscc tools\installer\GunWall.iss
; Inno Setup is free and open-source: https://jrsoftware.org/isinfo.php
;
; Version note: 7.x is current and is what to install. Nothing below uses a 7-only
; feature — the sections and Pascal scripting here have been stable for years —
; but ArchitecturesAllowed=x64compatible requires 6.3 or later, so anything older
; than that will refuse to compile this file.
;
; ---------------------------------------------------------------------------
; WHY THIS EXISTS
;
; Convenience is not the reason. GunWall is a single portable executable and
; needs no installer to run.
;
; The reason is UNINSTALLATION. GunWall's filters live in the Windows kernel and
; are marked PERSISTENT, so they keep enforcing after the application is closed,
; crashed or removed. Deleting the folder therefore leaves a machine filtering
; traffic with nothing installed to manage or explain it — the single worst
; failure this project has, and the one a portable build cannot fix on its own.
;
; The uninstaller below runs `GunWall.exe --unblock` BEFORE removing anything.
; That tears down every filter, restores the hosts file and adapter DNS, and
; returns the machine to Windows defaults. If it fails, the uninstall stops and
; says so rather than leaving the user locked out silently.
;
; Everything else here — shortcuts, Add/Remove Programs, upgrade in place — is
; incidental.
; ---------------------------------------------------------------------------

; WHERE THE PUBLISHED EXECUTABLE IS.
;
; Override it when your publish folder is not the repo default:
;
;   iscc /DPublishDir="C:\Users\You\Downloads\1.Gunwall-Installer\x64" tools\installer\GunWall.iss
;
; A parameter rather than a hard-coded path, because the first version assumed the
; repository layout and would simply fail to compile for anyone publishing
; somewhere else - which is everyone who uses the Visual Studio publish dialog and
; picks their own folder.
#ifndef PublishDir
  #define PublishDir "..\..\src\GunWall\bin\x64\Release\net8.0-windows\publish\win-x64"
#endif

; Fail early and say why, rather than emitting an installer around a missing file.
#if !FileExists(AddBackslash(PublishDir) + "GunWall.exe")
  #error GunWall.exe was not found in PublishDir. Publish the project first, then pass /DPublishDir="<your publish folder>" to iscc.
#endif

; Where the finished installer is written. Overridable for the same reason.
#ifndef OutDir
  #define OutDir "..\..\dist"
#endif

#define AppName        "GunWall"
#define AppPublisher   "ox1d3x3"
#define AppUrl         "https://github.com/ox1d3x3/gunwall"
#define AppExe         "GunWall.exe"

; Read the version straight from the built binary, so the installer cannot claim
; a version the executable does not have. One source of truth, checked by the
; build rather than typed here.
#define AppVersion GetVersionNumbersString(AddBackslash(PublishDir) + AppExe)

[Setup]
AppId={{9F2C41AB-7E33-4D58-9C1E-0B7A6D5E4F21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
OutputDir={#OutDir}
OutputBaseFilename=GunWall-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; GunWall cannot install or remove WFP filters without elevation, and the
; uninstaller needs it too — see the note above about why that matters.
PrivilegesRequired=admin

; Not code-signed by choice; the release publishes a SHA-256 instead.
; See README → "Verifying what you downloaded".

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup";     Description: "Start GunWall when Windows starts (recommended — see below)"; GroupDescription: "Startup:"

[Files]
Source: "{#AddBackslash(PublishDir)}{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\README.md";  DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE";    DestDir: "{app}"; Flags: ignoreversion

; NOTE: the user profile is deliberately NOT installed or removed here. It lives
; in %ProgramData%\GunWall precisely so that replacing the application does not
; touch it — rules, blocklists and settings survive every upgrade. Uninstall
; offers to remove it separately, as an explicit choice.

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#AppExe}"
Name: "{group}\GunWall on GitHub"; Filename: "{#AppUrl}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start GunWall now"; \
    Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; THE POINT OF THIS FILE.
;
; Runs before any file is deleted. `--unblock` removes every filter, clears the
; sublayer, restores the hosts file and any adapter DNS GunWall changed, and
; exits without opening a window. RunOnceId keeps it to a single execution.
Filename: "{app}\{#AppExe}"; Parameters: "--unblock"; \
    RunOnceId: "GunWallUnblock"; Flags: waituntilterminated runhidden

[Code]
const
  ProfileDir = '{commonappdata}\GunWall';

{ Windows will not let us overwrite a running executable, and a half-replaced
  firewall is a worse outcome than a refused install. Ask rather than fail. }
function GunWallIsRunning(): Boolean;
var
  ResultCode: Integer;
begin
  { By process name, not by mutex. The first draft used CheckForMutexes with a
    name GunWall does not create, so it would have returned False every time and
    the installer would have gone on to overwrite a running executable — failing
    at the file copy, after the uninstall entry had already been written.
    Checked against the source rather than assumed. }
  Result := Exec('cmd.exe',
                 '/C tasklist /FI "IMAGENAME eq GunWall.exe" | find /I "GunWall.exe"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if GunWallIsRunning() then
  begin
    if MsgBox('GunWall is running and must be closed before it can be updated.'#13#10#13#10
              + 'Closing it does NOT stop firewall filtering — the filters live in the '
              + 'Windows kernel and keep enforcing until GunWall is started again.'#13#10#13#10
              + 'Close it now?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      Exec('taskkill.exe', '/IM GunWall.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1500);
    end
    else
      Result := False;
  end;
end;

{ The startup task is offered checked, and here is why it is not merely a
  convenience: GunWall's filters persist when it is not running, but nothing can
  raise a prompt in that state. A new program is then correctly denied and simply
  fails, with nothing on screen explaining it. Running at startup is what keeps
  the deny-with-a-prompt contract intact across a reboot. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('startup') then
      RegWriteStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
                          'GunWall', '"' + ExpandConstant('{app}\{#AppExe}') + '"')
    else
      RegDeleteValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'GunWall');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'GunWall');

    { Asked, never assumed. The profile holds every allow and block decision the
      user has made; deleting it silently would be indefensible, and keeping it
      silently would leave data behind on a machine someone believes is clean.
      Defaults to KEEPING it, because a reinstall then restores their rules. }
    Dir := ExpandConstant(ProfileDir);
    if DirExists(Dir) then
      if MsgBox('Also delete GunWall''s saved rules and settings?'#13#10#13#10
                + Dir + #13#10#13#10
                + 'Choose No to keep them, so reinstalling GunWall restores your '
                + 'application rules, blocklists and preferences.'#13#10#13#10
                + 'Firewall filtering has already been removed either way.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Dir, True, True, True);
  end;
end;
