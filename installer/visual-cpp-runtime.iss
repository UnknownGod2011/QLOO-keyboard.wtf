{ Microsoft prerequisites only. This file contains no app/profile/provider work.
  The exact downloaded bytes were inspected on 2026-10-07; see the release gate.
  Never replace the immutable URL with aka.ms or remove the SHA-256 pin. }
const
  VisualCppRuntimeRegistryPath = 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  VisualCppRuntimeUrl = 'https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe';
  VisualCppRuntimeSha256 = '843068991daaa1f73ad9f6239bce4d0f6a07a51f18c37ea2a867e9beca71295c';
  VisualCppRuntimeDownloadName = 'keyboard-wtf-microsoft-vc-runtime-x64.exe';

var
  VisualCppRuntimeNeedsRestart: Boolean;
  VisualCppRuntimeDownloadPage: TDownloadWizardPage;

function VisualCppRuntimeMinimumVersion(): Int64;
begin
  { This is the known-working runtime of the accepted release, not a claim
    about an upstream compiler's undocumented minimum version. }
  Result := PackVersionComponents(14, 50, 35719, 0);
end;

function IsVisualCppRuntimeVersionSupported(const Version: Int64): Boolean;
begin
  Result := (ComparePackedVersion(Version, VisualCppRuntimeMinimumVersion()) >= 0) and
    (ComparePackedVersion(Version, PackVersionComponents(15, 0, 0, 0)) < 0);
end;

function IsVisualCppRuntimeFileAvailable(const Name: String): Boolean;
var
  Version: Int64;
begin
  Result := GetPackedVersion(ExpandConstant('{sys}\') + Name, Version);
  if Result then
    Result := IsVisualCppRuntimeVersionSupported(Version);
end;

function IsVisualCppRuntimeAvailable(): Boolean;
var
  Installed, Major, Minor, Build, Revision: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, VisualCppRuntimeRegistryPath, 'Installed', Installed) and
    (Installed = 1) and
    RegQueryDWordValue(HKLM64, VisualCppRuntimeRegistryPath, 'Major', Major) and
    RegQueryDWordValue(HKLM64, VisualCppRuntimeRegistryPath, 'Minor', Minor) and
    RegQueryDWordValue(HKLM64, VisualCppRuntimeRegistryPath, 'Bld', Build) and
    RegQueryDWordValue(HKLM64, VisualCppRuntimeRegistryPath, 'Rbld', Revision);
  if not Result then
    Exit;

  { Packing must never truncate a corrupt registry value into a valid version. }
  Result := (Major <= 65535) and (Minor <= 65535) and
    (Build <= 65535) and (Revision <= 65535);
  if not Result then
    Exit;
  Result := IsVisualCppRuntimeVersionSupported(PackVersionComponents(Major, Minor, Build, Revision)) and
    IsVisualCppRuntimeFileAvailable('msvcp140.dll') and
    IsVisualCppRuntimeFileAvailable('vcruntime140.dll') and
    IsVisualCppRuntimeFileAvailable('vcruntime140_1.dll') and
    IsVisualCppRuntimeFileAvailable('vcomp140.dll');
end;

function IsVisualCppInstallerSuccess(const ExitCode: Integer; const RuntimeAvailable: Boolean): Boolean;
begin
  { 1638 means a newer package already exists; that is safe only if our actual
    runtime checks pass. A reboot-required result is deliberately NOT success. }
  Result := ((ExitCode = 0) or (ExitCode = 1638)) and RuntimeAvailable;
end;

function EnsureVisualCppRuntime(var NeedsRestart: Boolean; const UpdaterMode: Boolean): String;
var
  DownloadPath: String;
  Version: Int64;
  ResultCode: Integer;
begin
  Result := '';
  if VisualCppRuntimeNeedsRestart then
  begin
    NeedsRestart := True;
    Result := 'Microsoft Visual C++ Runtime requires a Windows restart. Restart Windows, then run this keyboard.wtf installer again.';
    Exit;
  end;

  if IsVisualCppRuntimeAvailable() then
  begin
    Log('Microsoft Visual C++ x64 Runtime is already available; no download or installation is needed.');
    Exit;
  end;

  { A silent/update install must not surprise the user with elevation, network
    setup, or a prerequisite licence acceptance. It leaves the old app intact. }
  if WizardSilent or UpdaterMode then
  begin
    Result := 'Microsoft Visual C++ x64 Runtime is required. Run this keyboard.wtf installer normally to install it, then retry the update.';
    Exit;
  end;

  if MsgBox('keyboard.wtf needs Microsoft Visual C++ x64 Runtime for local voice processing.'#13#10#13#10 +
    'Setup will download the verified Microsoft installer (about 18 MB). Microsoft will show its licence terms and may ask for administrator approval. Your keyboard.wtf installation will not be changed until this step succeeds.'#13#10#13#10 +
    'Continue?', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := 'The required Microsoft runtime was not installed. No keyboard.wtf files have been replaced.';
    Exit;
  end;

  DownloadPath := ExpandConstant('{tmp}\') + VisualCppRuntimeDownloadName;
  try
    if VisualCppRuntimeDownloadPage = nil then
    begin
      VisualCppRuntimeDownloadPage := CreateDownloadPage(
        'Preparing local voice processing', 'Downloading the verified Microsoft runtime...', nil);
      VisualCppRuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
    end;
    VisualCppRuntimeDownloadPage.Clear;
    VisualCppRuntimeDownloadPage.Add(VisualCppRuntimeUrl, VisualCppRuntimeDownloadName, VisualCppRuntimeSha256);
    VisualCppRuntimeDownloadPage.Show;
    try
      VisualCppRuntimeDownloadPage.Download;
    finally
      VisualCppRuntimeDownloadPage.Hide;
    end;

    { The hash binds these bytes to the Microsoft Authenticode signature and
      x64-package metadata reviewed for this release. Recheck before execution. }
    if CompareText(GetSHA256OfFile(DownloadPath), VisualCppRuntimeSha256) <> 0 then
      RaiseException('Microsoft runtime download did not pass verification.');
    if not GetPackedVersion(DownloadPath, Version) then
      RaiseException('Microsoft runtime version could not be verified.');
    if ComparePackedVersion(Version, PackVersionComponents(14, 51, 36247, 0)) <> 0 then
      RaiseException('Microsoft runtime version did not match this release.');

    { Do not suppress Microsoft's licence UI and never force a reboot. The
      bootstrapper owns any administrator approval, independently of our app. }
    if not ShellExec('open', DownloadPath, '/install /norestart', '', SW_SHOWNORMAL,
      ewWaitUntilTerminated, ResultCode) then
    begin
      Log(Format('Microsoft runtime installer could not start (Windows error %d).', [ResultCode]));
      Result := 'Microsoft runtime setup was not started or permission was denied. No keyboard.wtf files have been replaced.';
      Exit;
    end;
    Log(Format('Microsoft runtime installer completed (exit code %d).', [ResultCode]));
    if ResultCode = 3010 then
    begin
      VisualCppRuntimeNeedsRestart := True;
      NeedsRestart := True;
      Result := 'Microsoft Visual C++ Runtime requires a Windows restart. Restart Windows, then run this keyboard.wtf installer again.';
      Exit;
    end;
    if not IsVisualCppInstallerSuccess(ResultCode, IsVisualCppRuntimeAvailable()) then
      Result := 'Microsoft Visual C++ Runtime setup did not complete successfully. Complete its setup, then retry. No keyboard.wtf files have been replaced.';
  except
    Log('Microsoft runtime prerequisite failed: ' + GetExceptionMessage);
    Result := 'The verified Microsoft runtime could not be downloaded or prepared. Check your connection and retry. No keyboard.wtf files have been replaced.';
  end;
end;
