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
  ; Default local publish folder, so a plain compile in the Inno Setup
  ; IDE works with no arguments. Anyone else passes /DPublishDir - the guard
  ; below names the problem if they forget.
  #define PublishDir "C:\Users\X1\Downloads\1.Gunwall-Installer\x64"
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
#define AppPublisher   "Ox1d3x3"
#define AppCopyright   "Copyright (c) Ox1d3x3. MIT licence."
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
AppCopyright={#AppCopyright}
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

; Version resource on setup.exe itself. Inno derives these from AppPublisher,
; AppCopyright and AppName when they are unset, but only when those values hold
; no unresolvable constants - otherwise it emits a warning and leaves the field
; blank. Stated explicitly so the publisher shown in the setup binary's file
; properties cannot silently become empty.
; Directive names verified against Compiler.SetupCompiler.pas in jrsoftware/issrc,
; not recalled.
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright={#AppCopyright}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

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
; Inno's default, stated for the same reason as the two above: this is where
; unins000.exe is written, and it is the file that removes GunWall's kernel
; filters. It belongs beside the application, not somewhere a user has to find.
UninstallFilesDir={app}

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
  SvcName = 'GunWallService';

var
  ServiceWasRunning: Boolean;
  InstallDone: Boolean;

{ The background service (0.99.199) is GunWall.exe --service, so it holds the
  same files the installer replaces. It is stopped through the service manager
  before any copy - a plain kill would be restarted by Windows' recovery
  settings within seconds - and started again afterwards if it was running. }
function ServiceInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query ' + SvcName, '', SW_HIDE,
                 ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function ServiceRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/C sc query ' + SvcName + ' | find "RUNNING"', '', SW_HIDE,
                 ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function ServiceStopped(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/C sc query ' + SvcName + ' | find "STOPPED"', '', SW_HIDE,
                 ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

{ Waits for STOPPED, not merely "not RUNNING": sc stop returns at STOP_PENDING,
  and a taskkill of GunWall.exe that lands on a stopping service is a crash to
  Windows - whose recovery would restart it from the old files mid-copy. Recovery
  is switched off first for the same reason; RegisterService puts it back. }
procedure StopServiceAndWait();
var
  ResultCode, I: Integer;
begin
  if not ServiceInstalled() then Exit;
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure ' + SvcName + ' reset= 0 actions= ""',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ' + SvcName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 60 do
  begin
    if ServiceStopped() then Break;
    Sleep(500);
  end;
  Sleep(2000);   { STOPPED is reported a moment before the process has exited }
end;

{ Registered on every install, so the Settings option can switch it on. Created
  with a manual start: it starts with Windows only once the user turns it on in
  GunWall (which sets it to automatic). An existing registration keeps its start
  type; only the path and recovery settings are refreshed. }
procedure RegisterService();
var
  ResultCode: Integer;
  Bin: String;
begin
  Bin := '"\"' + ExpandConstant('{app}\{#AppExe}') + '\" --service"';
  if ServiceInstalled() then
    Exec(ExpandConstant('{sys}\sc.exe'), 'config ' + SvcName + ' binPath= ' + Bin,
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    Exec(ExpandConstant('{sys}\sc.exe'), 'create ' + SvcName + ' binPath= ' + Bin
         + ' start= demand DisplayName= "GunWall Protection"',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'description ' + SvcName
       + ' "Keeps GunWall''s firewall protection running while the GunWall window is closed."',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure ' + SvcName
       + ' reset= 86400 actions= restart/5000/restart/30000/restart/60000',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  { Recovery also when the service reports a failure, not only when it crashes. }
  Exec(ExpandConstant('{sys}\sc.exe'), 'failureflag ' + SvcName + ' 1',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

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
  ServiceWasRunning := ServiceInstalled() and ServiceRunning();
  if ServiceWasRunning then StopServiceAndWait();
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
{ Setup cancelled or failed after the service was stopped: put it back. }
procedure DeinitializeSetup();
var
  ResultCode: Integer;
begin
  if ServiceWasRunning and not InstallDone and ServiceInstalled() then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'failure ' + SvcName
         + ' reset= 86400 actions= restart/5000/restart/30000/restart/60000',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'start ' + SvcName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  { Recorded BEFORE any file is copied, while the previous installation is still
    identifiable. If a profile folder is already here, this machine has run
    GunWall before, so the first-run offer of the optional databases must not
    appear - the user has already made that choice, and re-asking every release
    is how a prompt becomes something people dismiss without reading.

    A marker FILE rather than an edit to rules.json: the installer has no JSON
    writer, and the application is the only thing that should be writing to its
    own store. GunWall consumes and deletes this on the next launch. }
  if CurStep = ssInstall then
  begin
    { ExpandConstant: without it this tested the literal constant text, not the
      folder, and the marker was never written (found in review, 0.99.199). }
    if DirExists(ExpandConstant(ProfileDir)) then
    begin
      ForceDirectories(ExpandConstant(ProfileDir));
      SaveStringToFile(ExpandConstant(ProfileDir) + '\upgraded.marker',
                       'Written by the installer on upgrade. Consumed and deleted '
                       + 'by GunWall on the next launch. Safe to delete.', False);
    end;
  end;

  if CurStep = ssPostInstall then
  begin
    RegisterService();
    if ServiceWasRunning then
      Exec(ExpandConstant('{sys}\sc.exe'), 'start ' + SvcName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    InstallDone := True;

    { The data folder: SYSTEM and Administrators only, everyone else read.
      Inherited from ProgramData it let any user create files there - a planted
      engine.lock could keep the background service out, and a planted
      pending-prompts.json could put a program in front of an elevated Allow
      button (0.99.199). }
    ForceDirectories(ExpandConstant(ProfileDir));
    { Three steps, in this order. Owner first, so administrators can always
      rewrite the permissions. Then the FOLDER alone: protected, with entries its
      contents inherit. Then everything inside reset to inherit from it.
      0.99.199 applied the folder grant with /T, i.e. to every file as well; on a
      file those inheritance flags are refused, which left the files with no
      permissions at all and the profile unreadable (2026-10-10). }
    Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant(ProfileDir) + '" /setowner *S-1-5-32-544 /T /C /Q',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant(ProfileDir) + '" /inheritance:r '
         + '/grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /C /Q',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant(ProfileDir) + '\*" /reset /T /C /Q',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

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

  { Close it BEFORE --unblock, not after.

    A running GunWall watches its own filters and re-installs them when they go
    missing. It cannot distinguish a deliberate teardown from an attack, so it
    treats this one as an attack: on 2026-09-06 the uninstaller removed 24
    filters at 18:25:38 and the running instance restored 28 at 18:25:47. The
    uninstall then completed and left them enforcing in the kernel with nothing
    installed that could remove them - the exact outcome the message below warns
    about.

    PrepareToInstall has closed the app since the installer gained an upgrade
    path. The uninstall path never did, and the two runs on 2026-09-06 differed
    only in whether GunWall happened to be open. Most people uninstalling a
    firewall have it open; that is how they reached the decision.

    Not fatal if it fails. --unblock closes other instances itself for exactly
    this reason, and the codes below still report what happened. }
  { The background service goes first and for good: a running service would take
    the engine back and re-apply the filters --unblock is about to remove. }
  if ServiceInstalled() then
  begin
    StopServiceAndWait();
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete ' + SvcName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if GunWallIsRunning() then
  begin
    Exec('taskkill.exe', '/IM GunWall.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;

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
  TaskResult: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'GunWall');

    { Run at startup can also be a scheduled task, created by the app itself
      (Settings, Run at startup). Uninstall used to leave it behind, pointing at
      a deleted GunWall.exe. Result ignored: no task is the normal case. }
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "GunWallAutoStart" /F', '',
      SW_HIDE, ewWaitUntilTerminated, TaskResult);

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
