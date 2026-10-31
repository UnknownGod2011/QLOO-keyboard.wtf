#ifndef MyAppName
  #define MyAppName "keyboard.wtf"
#endif
#ifndef MyAppVersion
#define MyAppVersion "1.4.21"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "keyboard.wtf"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "keyboard.wtf.exe"
#endif
#ifndef MyAppId
  #define MyAppId "{{A2290D45-0672-4FC5-8CFB-AEB92B1EF945}"
#endif
#ifndef MyDefaultDirName
  #define MyDefaultDirName "{localappdata}\Programs\keyboard.wtf"
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "keyboard-wtf-setup"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\dist\release\keyboard-wtf-win-x64"
#endif
#ifndef MyRunValueName
  #define MyRunValueName "keyboard.wtf"
#endif
#ifndef MyDefaultGroupName
  #define MyDefaultGroupName "keyboard.wtf"
#endif
#ifndef MyShortcutName
  #define MyShortcutName "keyboard.wtf"
#endif
#ifndef MyUserDataDir
  #define MyUserDataDir "keyboard.wtf"
#endif
#ifndef MyPrerequisiteCode
  #define MyPrerequisiteCode "visual-cpp-runtime.iss"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={#MyDefaultDirName}
DefaultGroupName={#MyDefaultGroupName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist\installer
OutputBaseFilename={#MyOutputBaseFilename}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
SetupLogging=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=keyboard.wtf Windows installer
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,.keyboard.wtf-update-reservation,.keyboard.wtf-update-activity"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyShortcutName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Registry]
#ifndef MySkipStartup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyRunValueName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue
#endif

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch keyboard.wtf and finish setup"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent; Check: ShouldLaunchAfterInstall

[UninstallDelete]
Type: dirifempty; Name: "{app}\runtimes\win-x64"
Type: dirifempty; Name: "{app}\runtimes"
Type: dirifempty; Name: "{app}"

[Code]
#include MyPrerequisiteCode

const
  NativeHostRegistryPath = 'Software\Google\Chrome\NativeMessagingHosts\wtf.keyboard.browser';

var
  PreviousNativeHostManifest: String;
  NativeHostRegistrationPaused: Boolean;
  NativeHostRegistrationRestored: Boolean;

procedure StopOwnedProcessesForInstallOrUninstall(); forward;
function HasCommandLineParameter(const Name: String): Boolean; forward;

function IsUpdaterMode(): Boolean;
begin
  Result := HasCommandLineParameter('/KWTUPDATE');
end;

function ShouldLaunchAfterInstall(): Boolean;
begin
  { The authenticated update worker, once implemented, is the sole relaunch
    owner. Ordinary interactive/silent installation keeps its existing flags. }
  Result := not IsUpdaterMode();
  if VisualCppRuntimeNeedsRestart then
    Result := False;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsUpdaterMode() then
    Exit;

  { These arguments are safety requirements, NOT authentication or evidence
    that the parent exited. The desktop install-readiness guard stays disabled
    until the separate parent/worker handshake and exclusion are implemented. }
  Result := HasCommandLineParameter('/SILENT') and
    HasCommandLineParameter('/NOCLOSEAPPLICATIONS') and
    HasCommandLineParameter('/NOFORCECLOSEAPPLICATIONS') and
    HasCommandLineParameter('/NORESTARTAPPLICATIONS') and
    HasCommandLineParameter('/NORESTART') and
    HasCommandLineParameter('/RESTARTEXITCODE=3010') and
    not HasCommandLineParameter('/CLOSEAPPLICATIONS') and
    not HasCommandLineParameter('/FORCECLOSEAPPLICATIONS') and
    not HasCommandLineParameter('/RESTARTAPPLICATIONS');
  if not Result then
  begin
    Log('Rejected updater invocation without the required non-disruptive flags.');
    SuppressibleMsgBox('The update could not start safely. Reopen keyboard.wtf and try again.',
      mbError, MB_OK, IDOK);
  end;
end;

function RestoreNativeHostRegistration(): Boolean;
begin
  Result := True;
  if not NativeHostRegistrationPaused or NativeHostRegistrationRestored then
    Exit;

  Result := RegWriteStringValue(
    HKCU,
    NativeHostRegistryPath,
    '',
    PreviousNativeHostManifest);
  if Result then
  begin
    NativeHostRegistrationRestored := True;
    Log('Restored the existing Chrome native-messaging host registration.');
  end
  else
    Log('Could not restore the existing Chrome native-messaging host registration.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Complete prerequisites before pausing Helper or replacing any app files. }
  Result := EnsureVisualCppRuntime(NeedsRestart, IsUpdaterMode());
  if Result <> '' then
    Exit;
#ifndef MyPreserveNativeHostRegistration
  NativeHostRegistrationPaused := RegQueryStringValue(
    HKCU,
    NativeHostRegistryPath,
    '',
    PreviousNativeHostManifest);
  if NativeHostRegistrationPaused and
     not RegDeleteValue(HKCU, NativeHostRegistryPath, '') then
  begin
    NativeHostRegistrationPaused := False;
    Result := 'Chrome Helper could not be paused for this update. Close Chrome and keyboard.wtf, then try again.';
    Exit;
  end;

  if NativeHostRegistrationPaused then
    Log('Temporarily paused the existing Chrome native-messaging host registration for safe file replacement.');
#else
  { Side-by-side packages replace only their own files. Pausing a different
    installation's Chrome host would add a global failure path for no benefit. }
  Log('Preserved the existing Chrome native-messaging host registration for this side-by-side install.');
#endif

  { Stop only processes whose executable path belongs to this exact
    installation. For regular updates this also closes a native host Chrome
    started just before registration was paused. Other installs stay alive. }
  if not IsUpdaterMode() then
    StopOwnedProcessesForInstallOrUninstall();
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not RestoreNativeHostRegistration() then
      RaiseException('Chrome Helper registration could not be restored after the update.');
end;

procedure DeinitializeSetup();
begin
  RestoreNativeHostRegistration();
end;

function HasCommandLineParameter(const Name: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure UnregisterOwnedNativeHost();
var
  RegisteredManifest: String;
  OwnedManifest: String;
begin
  if not RegQueryStringValue(
    HKCU,
    NativeHostRegistryPath,
    '',
    RegisteredManifest) then
    Exit;

  OwnedManifest := ExpandConstant(
    '{localappdata}\{#MyUserDataDir}\browser-helper\wtf.keyboard.browser.json');
  if CompareText(RegisteredManifest, OwnedManifest) <> 0 then
  begin
    Log('Chrome native-messaging host belongs to another installation; leaving it unchanged.');
    Exit;
  end;

  if RegDeleteKeyIncludingSubkeys(HKCU, NativeHostRegistryPath) then
    Log('Removed this installation''s Chrome native-messaging host registration.')
  else
    Log('Could not remove this installation''s Chrome native-messaging host registration.');
end;

function EscapePowerShellSingleQuoted(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
end;

procedure StopOwnedProcessesForInstallOrUninstall();
var
  ResultCode: Integer;
  TargetExecutable: String;
  Script: String;
  Arguments: String;
begin
  TargetExecutable := ExpandConstant('{app}\{#MyAppExeName}');
  Script :=
    '$target=[IO.Path]::GetFullPath(''' +
    EscapePowerShellSingleQuoted(TargetExecutable) +
    '''); Get-Process -Name ''keyboard.wtf'' -ErrorAction SilentlyContinue | ' +
    'Where-Object { try { [IO.Path]::GetFullPath($_.Path) -eq $target } catch { $false } } | ' +
    'Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 600';
  Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"';

  if Exec(
       ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       Arguments,
       '',
       SW_HIDE,
       ewWaitUntilTerminated,
       ResultCode) then
    Log(Format('Stopped processes owned by this installation (exit code %d).', [ResultCode]))
  else
    Log('Could not invoke the installation-scoped process cleanup.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopOwnedProcessesForInstallOrUninstall();
    UnregisterOwnedNativeHost();
  end;

  if (CurUninstallStep = usPostUninstall) and
     HasCommandLineParameter('/REMOVEUSERDATA') then
  begin
    DelTree(ExpandConstant('{localappdata}\{#MyUserDataDir}'), True, True, True);
  end;
end;
