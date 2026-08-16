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
  ; The maintainer's local publish folder, so a plain compile in the Inno Setup
  ; IDE works with no arguments. Anyone else passes /DPublishDir - the guard
  ; below names the problem if they forget.
  #define PublishDir "C:\Users\TAMGG\Downloads\1.Gunwall-Installer\x64"
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

; Uninstaller. These are Inno's defaults, stated explicitly because the uninstaller
; is the whole reason this installer exists — it is what removes GunWall's kernel
; filters before the files go. A default that changed silently would take the
; safety guarantee with it.
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

; Not code-signed by choice; the release publishes a SHA-256 instead.
; See README → "Verifying what you downloaded".

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup";     Description: "Start GunWall when Windows starts (recommended — see below)"; GroupDescription: "Startup:"

[Files]
; THE WHOLE PUBLISH OUTPUT, not just the executable.
;
; A .NET single-file WPF publish is not actually a single file: the native
; libraries stay beside it - D3DCompiler_47_cor3.dll, PenImc_cor3.dll,
; PresentationNative_cor3.dll, vcruntime140_cor3.dll, wpfgfx_cor3.dll - along
; with an Assets folder. Copying only GunWall.exe produced an installer that
; completed happily and left an application that could not start at all.
;
; Wildcarded rather than listed, because a list of five DLL names is a list to
; forget the sixth of, and the publish output is the authority on its own contents.
Source: "{#AddBackslash(PublishDir)}*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,*.xml"
Source: "..\..\README.md";  DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE";    DestDir: "{app}"; Flags: ignoreversion

; NOTE: the user profile is deliberately NOT installed or removed here. It lives
; in %ProgramData%\GunWall precisely so that replacing the application does not
; touch it — rules, blocklists and settings survive every upgrade. Uninstall
; offers to remove it separately, as an explicit choice.

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#AppExe}"
Name: "{group}\GunWall on GitHub"; Filename: "{#AppUrl}"

; A Start Menu entry for the uninstaller.
;
; Inno names the executable "unins000.exe", not "uninstall.exe", and drops it in
; the application folder — which is neither obvious nor guessable. Normally that
; does not matter because Add/Remove Programs is the expected route, but for this
; application it does: the uninstaller is the only thing that removes GunWall's
; kernel filters, so it must be easy to find rather than merely present.
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start GunWall now"; \
    Flags: nowait postinstall skipifsilent runascurrentuser

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

{ THE POINT OF THIS FILE, and it has to be checked rather than fired and forgotten.

  GunWall's filters live in the Windows kernel and are marked persistent, so they
  keep enforcing after the application is gone. Removing the files without first
  removing the filters leaves a machine filtering traffic with nothing installed to
  manage or explain it.

  This was an [UninstallRun] entry, which runs the command and ignores its result.
  If --unblock had failed - a corrupt binary, a missing dependency, elevation
  refused - the uninstall would have carried on and deleted the only thing capable
  of undoing the damage. The exit code is now read: 0 clean, 1 filters remained,
  anything else a failure to run at all. }
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  Exe: String;
begin
  Result := True;
  Exe := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(Exe) then Exit;   { nothing to run; let the uninstall proceed }

  if not Exec(Exe, '--unblock', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := MsgBox('GunWall could not be started to remove its firewall filters.'#13#10#13#10
      + 'Those filters live in the Windows kernel and will KEEP FILTERING after '
      + 'GunWall is uninstalled, with nothing left to undo them.'#13#10#13#10
      + 'Uninstall anyway?', mbError, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    Exit;
  end;

  if ResultCode = 1 then
    MsgBox('GunWall removed its own filters, but some filters in its sublayer were '
      + 'not created by this installation and were left in place.'#13#10#13#10
      + 'They are inactive without rules behind them, and a restart clears any that '
      + 'were not persistent.', mbInformation, MB_OK)
  else if ResultCode <> 0 then
    Result := MsgBox('Removing GunWall''s firewall filters failed (code '
      + IntToStr(ResultCode) + ').'#13#10#13#10
      + 'Those filters will KEEP FILTERING after GunWall is uninstalled.'#13#10#13#10
      + 'Uninstall anyway?', mbError, MB_YESNO or MB_DEFBUTTON2) = IDYES;
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
