<#
.SYNOPSIS
Read-only source checks plus an inert Inno policy fixture. Never installs VC++.
.DESCRIPTION
Compiles and starts only a no-files/no-registry/no-uninstaller fixture whose
InitializeSetup exercises the actual version/result functions then exits before
installation. The real prerequisite function is compiled but NEVER invoked.
Optional ReviewedDownloadPath inspects an already downloaded Microsoft file;
it is never run, redistributed, repaired, or installed. No network is used.
#>
[CmdletBinding()]
param([string]$ReviewedDownloadPath, [string]$IsccPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$taskPrerequisitePath = Join-Path $taskRoot 'installer\visual-cpp-runtime.iss'
$taskInstallerPath = Join-Path $taskRoot 'installer\keyboard-wtf.iss'
$taskSource = [IO.File]::ReadAllText($taskPrerequisitePath)
$taskInstaller = [IO.File]::ReadAllText($taskInstallerPath)
$taskChecks = 0
function Assert-Prerequisite([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "visual_cpp_prerequisite:$Name" }
    $script:taskChecks++
}
function Read-Constant([string]$Name) {
    $matches = [regex]::Matches($taskSource, "(?m)^  $Name = '([^'\r\n]+)';\r?$")
    Assert-Prerequisite ($matches.Count -eq 1) "constant_$Name"
    return $matches[0].Groups[1].Value
}
$taskUrl = Read-Constant 'VisualCppRuntimeUrl'
$taskSha = Read-Constant 'VisualCppRuntimeSha256'
Assert-Prerequisite ($taskUrl -cmatch '\Ahttps://download\.visualstudio\.microsoft\.com/download/pr/[a-f0-9-]+/[A-F0-9]{64}/VC_redist\.x64\.exe\z') 'immutable_official_url'
Assert-Prerequisite ($taskSha -cmatch '\A[a-f0-9]{64}\z' -and $taskUrl.Contains('/' + $taskSha.ToUpperInvariant() + '/')) 'url_content_pin'
Assert-Prerequisite (-not [regex]::IsMatch($taskSource, "'https?://aka\.ms/")) 'no_mutable_download_fallback'
Assert-Prerequisite ($taskSource.Contains("VisualCppRuntimeDownloadPage.Add(VisualCppRuntimeUrl, VisualCppRuntimeDownloadName, VisualCppRuntimeSha256)")) 'inno_download_hash_required'
Assert-Prerequisite ($taskSource.Contains('GetSHA256OfFile(DownloadPath)')) 'pre_execution_hash_recheck'
Assert-Prerequisite ($taskSource.Contains('PackVersionComponents(14, 51, 36247, 0)')) 'exact_reviewed_package_version'
Assert-Prerequisite ($taskSource.Contains('RegQueryDWordValue(HKLM64, VisualCppRuntimeRegistryPath')) 'x64_registry_view'
foreach ($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')) {
    Assert-Prerequisite ($taskSource.Contains("IsVisualCppRuntimeFileAvailable('$name')")) "required_file_$name"
}
Assert-Prerequisite ($taskSource.Contains('if WizardSilent or UpdaterMode then')) 'no_surprise_silent_elevation'
Assert-Prerequisite ($taskSource.Contains("'/install /norestart'")) 'visible_licensed_install_no_restart'
Assert-Prerequisite (-not [regex]::IsMatch($taskSource, "(?i)'[^']*/(?:quiet|passive|repair|uninstall)[^']*'")) 'no_suppressed_licence_or_runtime_removal'
Assert-Prerequisite ($taskSource.Contains('ewWaitUntilTerminated, ResultCode')) 'original_installer_joined'
Assert-Prerequisite ($taskSource.Contains('if ResultCode = 3010 then') -and $taskSource.Contains('NeedsRestart := True;')) 'reboot_is_not_instant_success'
$taskPrepare = $taskInstaller.Substring($taskInstaller.IndexOf('function PrepareToInstall'))
Assert-Prerequisite ($taskPrepare.IndexOf('EnsureVisualCppRuntime') -lt $taskPrepare.IndexOf('NativeHostRegistrationPaused :=')) 'prerequisite_before_nativehost_mutation'
Assert-Prerequisite ($taskPrepare.IndexOf("if Result <> '' then") -lt $taskPrepare.IndexOf('NativeHostRegistrationPaused :=')) 'failure_preserves_app'
Assert-Prerequisite ($taskInstaller.Contains('PrivilegesRequired=lowest')) 'app_stays_per_user'
Assert-Prerequisite ($taskInstaller.Contains('if VisualCppRuntimeNeedsRestart then')) 'no_launch_before_required_reboot'
Assert-Prerequisite (-not [regex]::IsMatch($taskInstaller, '(?i)Source:\s*[^\r\n]*(?:vc_redist|vcruntime|msvcp140)')) 'no_redistributed_microsoft_payload'

if (-not [string]::IsNullOrWhiteSpace($ReviewedDownloadPath)) {
    $taskDownload = Get-Item -LiteralPath $ReviewedDownloadPath
    Assert-Prerequisite ($taskDownload -is [IO.FileInfo] -and $taskDownload.Length -eq 18731856) 'reviewed_download_size'
    Assert-Prerequisite ((Get-FileHash -LiteralPath $taskDownload.FullName -Algorithm SHA256).Hash -ieq $taskSha) 'reviewed_download_sha256'
    $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskDownload.FullName
    Assert-Prerequisite ($taskSignature.Status -eq 'Valid') 'microsoft_signature_valid'
    Assert-Prerequisite ($taskSignature.SignerCertificate.Subject -ceq 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US') 'microsoft_signer'
    Assert-Prerequisite ($taskSignature.SignerCertificate.Thumbprint -ceq '1D77A9B9E8FE2075D9AD15123257FB90DB0DA4A1') 'reviewed_signer_certificate'
    Assert-Prerequisite ($null -ne $taskSignature.TimeStamperCertificate) 'signature_timestamp_present'
    Assert-Prerequisite ($taskDownload.VersionInfo.FileVersion -ceq '14.51.36247.0' -and
        $taskDownload.VersionInfo.OriginalFilename -ceq 'VC_redist.x64.exe' -and
        $taskDownload.VersionInfo.ProductName.Contains('(x64)')) 'reviewed_version_architecture'
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $IsccPath = @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')) |
        Where-Object { [IO.File]::Exists($_) } | Select-Object -First 1
}
Assert-Prerequisite ([IO.File]::Exists($IsccPath)) 'inno_compiler_available'
$taskId = [Guid]::NewGuid().ToString('N')
$taskFixture = Join-Path $taskRoot ('dist\verification\public-release\launch\vc-policy-' + $taskId)
[void][IO.Directory]::CreateDirectory($taskFixture)
$taskFixtureSource = Join-Path $taskFixture 'policy.iss'
$taskFixtureExe = Join-Path $taskFixture 'vc-runtime-inert-policy.exe'
$taskFixtureLog = Join-Path $taskFixture 'policy.log'
$taskFixtureText = @"
[Setup]
AppName=keyboard.wtf Inert VC Policy $taskId
AppVersion=1.0
DefaultDirName=$taskFixture\never-installed
OutputDir=$taskFixture
OutputBaseFilename=vc-runtime-inert-policy
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=no
CreateUninstallRegKey=no
CreateAppDir=no
DisableStartupPrompt=yes
[Code]
#include "$taskPrerequisitePath"
procedure AssertPolicy(const Condition: Boolean; const Name: String);
begin
  if not Condition then RaiseException('VC_POLICY_FAILED:' + Name);
end;
function InitializeSetup(): Boolean;
var Code: Integer;
begin
  AssertPolicy(not IsVisualCppRuntimeVersionSupported(PackVersionComponents(0, 0, 0, 0)), 'missing');
  AssertPolicy(not IsVisualCppRuntimeVersionSupported(PackVersionComponents(14, 40, 0, 0)), 'old');
  AssertPolicy(not IsVisualCppRuntimeVersionSupported(PackVersionComponents(14, 50, 35718, 65535)), 'below');
  AssertPolicy(IsVisualCppRuntimeVersionSupported(PackVersionComponents(14, 50, 35719, 0)), 'accepted-current');
  AssertPolicy(IsVisualCppRuntimeVersionSupported(PackVersionComponents(14, 51, 36247, 0)), 'reviewed');
  AssertPolicy(not IsVisualCppRuntimeVersionSupported(PackVersionComponents(15, 0, 0, 0)), 'unreviewed-major');
  for Code := -1 to 4000 do
  begin
    AssertPolicy(not IsVisualCppInstallerSuccess(Code, False), 'missing-postcheck');
    AssertPolicy(IsVisualCppInstallerSuccess(Code, True) = ((Code = 0) or (Code = 1638)), 'exit-code');
  end;
  Log('VC_POLICY_PASSED:8004');
  Log('VC_RUNTIME_ALREADY_AVAILABLE:' + IntToStr(Ord(IsVisualCppRuntimeAvailable())));
  Result := False;
end;
"@
[IO.File]::WriteAllText($taskFixtureSource, $taskFixtureText, [Text.UTF8Encoding]::new($false))
& $IsccPath /Q $taskFixtureSource
Assert-Prerequisite ($LASTEXITCODE -eq 0 -and [IO.File]::Exists($taskFixtureExe)) 'inert_pascal_compile'
$taskArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-',('/LOG=' + $taskFixtureLog))
$taskProcess = Start-Process -FilePath $taskFixtureExe -ArgumentList ($taskArguments | ForEach-Object { '"' + $_ + '"' }) -WindowStyle Hidden -PassThru
try {
    $taskProcess.WaitForExit()
    Assert-Prerequisite ($taskProcess.ExitCode -eq 1) 'fixture_exits_before_install'
} finally { $taskProcess.Dispose() }
$taskLogText = [IO.File]::ReadAllText($taskFixtureLog)
Assert-Prerequisite ($taskLogText.Contains('VC_POLICY_PASSED:8004')) 'actual_pascal_cases'
Assert-Prerequisite (-not [IO.Directory]::Exists((Join-Path $taskFixture 'never-installed'))) 'no_application_install'

# Exercise the real prerequisite control flow without running a prerequisite.
# Only the read-only availability branch is forced false; silent setup must
# return BEFORE the confirmation/download/launch code, even on this prepared PC.
$taskAvailabilityAnchor = 'if IsVisualCppRuntimeAvailable() then'
Assert-Prerequisite ([regex]::Matches($taskSource, [regex]::Escape($taskAvailabilityAnchor)).Count -eq 1) 'missing_runtime_fixture_single_anchor'
$taskMissingCode = Join-Path $taskFixture 'missing-runtime.iss'
[IO.File]::WriteAllText($taskMissingCode, $taskSource.Replace($taskAvailabilityAnchor, 'if False then'), [Text.UTF8Encoding]::new($false))
$taskMissingSource = Join-Path $taskFixture 'missing-policy.iss'
$taskMissingExe = Join-Path $taskFixture 'vc-runtime-inert-missing-policy.exe'
$taskMissingLog = Join-Path $taskFixture 'missing-policy.log'
$taskPolicyBody = @"
function InitializeSetup(): Boolean;
var Restart: Boolean; Error: String;
begin
  Restart := False;
  Error := EnsureVisualCppRuntime(Restart, False);
  AssertPolicy(Pos('Run this keyboard.wtf installer normally', Error) > 0, 'silent-missing-refused');
  AssertPolicy(not Restart, 'silent-no-restart');
  Error := EnsureVisualCppRuntime(Restart, True);
  AssertPolicy(Pos('Run this keyboard.wtf installer normally', Error) > 0, 'updater-missing-refused');
  VisualCppRuntimeNeedsRestart := True;
  Error := EnsureVisualCppRuntime(Restart, False);
  AssertPolicy(Restart and (Pos('Restart Windows', Error) > 0), 'pending-restart-retained');
  AssertPolicy(VisualCppRuntimeDownloadPage = nil, 'no-download-page-created');
  Log('VC_MISSING_POLICY_PASSED:5');
  Result := False;
end;
"@
$taskMissingText = $taskFixtureText.Replace($taskPrerequisitePath, $taskMissingCode).
    Replace('OutputBaseFilename=vc-runtime-inert-policy', 'OutputBaseFilename=vc-runtime-inert-missing-policy')
$taskInitIndex = $taskMissingText.IndexOf('function InitializeSetup(): Boolean;')
Assert-Prerequisite ($taskInitIndex -gt 0) 'missing_runtime_fixture_entry'
$taskMissingText = $taskMissingText.Substring(0, $taskInitIndex) + $taskPolicyBody
[IO.File]::WriteAllText($taskMissingSource, $taskMissingText, [Text.UTF8Encoding]::new($false))
& $IsccPath /Q $taskMissingSource
Assert-Prerequisite ($LASTEXITCODE -eq 0 -and [IO.File]::Exists($taskMissingExe)) 'inert_missing_pascal_compile'
$taskMissingArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-',('/LOG=' + $taskMissingLog))
$taskProcess = Start-Process -FilePath $taskMissingExe -ArgumentList ($taskMissingArgs | ForEach-Object { '"' + $_ + '"' }) -WindowStyle Hidden -PassThru
try {
    $taskProcess.WaitForExit()
    Assert-Prerequisite ($taskProcess.ExitCode -eq 1) 'missing_fixture_exits_before_install'
} finally { $taskProcess.Dispose() }
$taskMissingLogText = [IO.File]::ReadAllText($taskMissingLog)
Assert-Prerequisite ($taskMissingLogText.Contains('VC_MISSING_POLICY_PASSED:5')) 'actual_silent_missing_branch'
Assert-Prerequisite (-not [IO.Directory]::Exists((Join-Path $taskFixture 'never-installed'))) 'missing_fixture_no_application_install'
[ordered]@{ checks=$taskChecks; pascalPolicyCases=8010; fixture=$taskFixture;
    missingRuntimeGuardCases=5;
    downloadedFileExecuted=$false; productBuiltOrInstalled=$false;
    runtimeAlreadyAvailable=$taskLogText.Contains('VC_RUNTIME_ALREADY_AVAILABLE:1') } | ConvertTo-Json
