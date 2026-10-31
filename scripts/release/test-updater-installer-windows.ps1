<#
.SYNOPSIS
Operator-only Windows Inno fixture; never launches keyboard.wtf.
.DESCRIPTION
Requires the independently reviewed SHA-256 of installer/keyboard-wtf.iss.
Compiles an isolated copy, preserving its updater code/flags and guarded Run
entry. The only payload is plain UTF-8 text named keyboard.wtf.exe, not a PE.
All owned artifacts remain under a fresh GUID verification directory. There is
no cleanup, force-kill, profile/provider access, signing, publication or download.
TimeoutSeconds is a diagnostic deadline, NOT permission to abandon a process:
after recording an overrun this script still joins original process/IO tasks.
The optional locked run records noncompletion/reboot honestly; it is not an
installation, exclusion, worker-handshake, byte-trust or relaunch proof.
.EXAMPLE
pwsh -NoProfile -File scripts/release/test-updater-installer-windows.ps1 `
  -ExpectedInstallerSourceSha256 <reviewed-64-hex-sha256> `
  -ExpectedPrerequisiteSourceSha256 <reviewed-64-hex-sha256> -IncludeLockedPayload
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[0-9a-fA-F]{64}\z')]
    [string]$ExpectedInstallerSourceSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[0-9a-fA-F]{64}\z')]
    [string]$ExpectedPrerequisiteSourceSha256,
    [string]$IsccPath,
    [switch]$IncludeLockedPayload,
    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Core' -or
    $PSVersionTable.PSVersion -lt [version]'7.2' -or
    [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'updater_installer_fixture_requires_windows_pwsh_7_2'
}

$encoding = [Text.UTF8Encoding]::new($false, $true)
$repository = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$sourcePath = Join-Path $repository 'installer\keyboard-wtf.iss'
$verificationParent = Join-Path $repository 'dist\verification\public-release\updater-final'
$fixtureId = [Guid]::NewGuid().ToString('N')
$fixtureRoot = Join-Path $verificationParent ('inno-fixture-' + $fixtureId)
$sourceStream = $null
$prerequisiteStream = $null
$payloadLock = $null
$receipt = $null
$receiptPath = Join-Path $fixtureRoot 'receipt.json'
$transformations = [Collections.Generic.List[object]]::new()
$processReceipts = [Collections.Generic.List[object]]::new()

function Assert-Fixture {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw ('updater_installer_fixture:' + $Message) }
}

function Assert-NoReparseAncestors {
    param([string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($absolute)) {
        try {
            $attributes = [IO.File]::GetAttributes($absolute)
            Assert-Fixture (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'reparse_path'
        }
        catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        # Only genuine not-found is allowed for future fixture children. Access,
        # invalid-path and other unknown attribute failures propagate fail-closed.
        $absolute = [IO.Path]::GetDirectoryName($absolute)
    }
}

function Assert-OwnedPath {
    param([string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path)
    Assert-Fixture ($absolute.StartsWith($fixtureRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'path_outside_fixture'
    Assert-NoReparseAncestors $absolute
    return $absolute
}

function Get-BytesHash {
    param([byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-HeldHash {
    param([IO.Stream]$Stream)
    $Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Save-Receipt {
    if ($null -ne $receipt) {
        [void](Assert-OwnedPath $receiptPath)
        [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 12), $encoding)
    }
}

function Replace-OneLiteral {
    param([string]$Text, [string]$Before, [string]$After, [string]$Reason)
    Assert-Fixture ([regex]::Matches($Text, [regex]::Escape($Before)).Count -eq 1) ('source_anchor:' + $Reason)
    $transformations.Add([ordered]@{ reason = $Reason; before = $Before; after = $After })
    return $Text.Replace($Before, $After)
}

function Get-InnoSection {
    param([string]$Text, [string]$Name)
    $matches = [regex]::Matches($Text, '(?ms)^\[' + [regex]::Escape($Name) + '\]\r?\n.*?(?=^\[|\z)')
    Assert-Fixture ($matches.Count -eq 1) ('source_section:' + $Name)
    return $matches[0].Value
}

function Invoke-OwnedProcess {
    param([string]$Phase, [string]$Executable, [string[]]$Arguments)
    [void](Assert-OwnedPath (Join-Path $fixtureRoot ($Phase + '.stdout.txt')))
    Assert-NoReparseAncestors $Executable
    Assert-Fixture ($Executable -ceq $compilerPath -or $Executable -ceq $installerPath) 'unexpected_process_executable'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $fixtureRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['TEMP'] = $scratchDirectory
    $start.Environment['TMP'] = $scratchDirectory
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $record = [ordered]@{
        phase = $Phase; executable = $Executable; arguments = $Arguments
        processId = $null; startedUtc = $null; exitedUtc = $null
        diagnosticDeadlineExceeded = $false; originalProcessAndIoJoined = $false
        exitCode = $null; stdout = $Phase + '.stdout.txt'; stderr = $Phase + '.stderr.txt'
    }
    $processReceipts.Add($record)
    Save-Receipt
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $originalExit = $null
    $originalOutput = $null
    $originalError = $null
    try {
        Assert-Fixture ($process.Start()) 'process_not_started'
        $started = $true
        $originalExit = $process.WaitForExitAsync()
        $originalOutput = $process.StandardOutput.ReadToEndAsync()
        $originalError = $process.StandardError.ReadToEndAsync()
        $record.processId = $process.Id
        $record.startedUtc = $process.StartTime.ToUniversalTime().ToString('O')
        Save-Receipt
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        while (-not ($originalExit.IsCompleted -and $originalOutput.IsCompleted -and $originalError.IsCompleted)) {
            if (-not $record.diagnosticDeadlineExceeded -and [DateTimeOffset]::UtcNow -ge $deadline) {
                $record.diagnosticDeadlineExceeded = $true
                Save-Receipt
                Write-Warning "$Phase exceeded the diagnostic deadline; retaining ownership and awaiting original process/IO."
            }
            [Threading.Thread]::Sleep(100)
        }
        # PowerShell can emit an internal VoidTaskResult for a completed Task.
        # Observe the original wait, but keep that object out of this function's
        # scalar exit-code success stream.
        [void]$originalExit.GetAwaiter().GetResult()
        $output = $originalOutput.GetAwaiter().GetResult()
        $errorOutput = $originalError.GetAwaiter().GetResult()
        Assert-Fixture ($process.HasExited -and $originalExit.IsCompletedSuccessfully -and
            $originalOutput.IsCompletedSuccessfully -and $originalError.IsCompletedSuccessfully) 'process_not_positively_joined'
        $record.originalProcessAndIoJoined = $true
        $record.exitedUtc = $process.ExitTime.ToUniversalTime().ToString('O')
        $record.exitCode = $process.ExitCode
        [IO.File]::WriteAllText((Assert-OwnedPath (Join-Path $fixtureRoot $record.stdout)), $output, $encoding)
        [IO.File]::WriteAllText((Assert-OwnedPath (Join-Path $fixtureRoot $record.stderr)), $errorOutput, $encoding)
        Save-Receipt
        Assert-Fixture (-not $record.diagnosticDeadlineExceeded) ('diagnostic_deadline_exceeded:' + $Phase)
        return $process.ExitCode
    }
    catch {
        if ($null -ne $receipt -and $null -eq $receipt.firstFailure) {
            $receipt.firstFailure = $_.Exception.ToString()
            try { Save-Receipt } catch { }
        }
        throw
    }
    finally {
        if ($started -and -not $record.originalProcessAndIoJoined) {
            # Even an earlier receipt/IO failure cannot release the payload lock
            # beneath a live installer. Observe the SAME Process, never a PID lookup.
            if ($null -ne $originalExit) { [void]$originalExit.GetAwaiter().GetResult() }
            else { $process.WaitForExit() }
            if ($null -ne $originalOutput) { [void]$originalOutput.GetAwaiter().GetResult() }
            if ($null -ne $originalError) { [void]$originalError.GetAwaiter().GetResult() }
            $record.originalProcessAndIoJoined = $process.HasExited
            if ($process.HasExited) { $record.exitCode = $process.ExitCode }
        }
        $process.Dispose()
    }
}

function Get-SetupArguments {
    param([string]$LogPath)
    [void](Assert-OwnedPath $LogPath)
    return @('/SILENT', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/KWTUPDATE',
        '/NOCLOSEAPPLICATIONS', '/NOFORCECLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS',
        '/NORESTART', '/RESTARTEXITCODE=3010', ('/DIR=' + $targetDirectory), ('/LOG=' + $LogPath))
}

function Read-SetupLog {
    param([string]$LogPath)
    [void](Assert-OwnedPath $LogPath)
    Assert-Fixture ([IO.File]::Exists($LogPath)) 'setup_log_missing'
    $log = [IO.File]::ReadAllText($LogPath)
    # Inno documents logs as diagnostic, not a stable machine-readable contract.
    # This is an extra observed-launch veto; absence is not process-tracing proof.
    Assert-Fixture (-not [regex]::IsMatch($log, '(?im)^.*---\s*Run entry\s*---')) 'unexpected_run_entry_observed'
    return $log
}

try {
    Assert-NoReparseAncestors $sourcePath
    Assert-NoReparseAncestors $verificationParent
    Assert-Fixture ([IO.Path]::GetDirectoryName($fixtureRoot) -ceq $verificationParent -and
        [IO.Path]::GetFileName($fixtureRoot) -cmatch '\Ainno-fixture-[a-f0-9]{32}\z') 'fixture_root_invalid'
    Assert-Fixture (-not [IO.Directory]::Exists($fixtureRoot) -and -not [IO.File]::Exists($fixtureRoot)) 'fixture_root_exists'
    $sourceStream = [IO.FileStream]::new($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Assert-Fixture ($sourceStream.Length -gt 0 -and $sourceStream.Length -le 1MB) 'source_size'
    $memory = [IO.MemoryStream]::new()
    try { $sourceStream.CopyTo($memory); $sourceBytes = $memory.ToArray() }
    finally { $memory.Dispose() }
    $sourceHash = Get-BytesHash $sourceBytes
    Assert-Fixture ($sourceHash -ceq $ExpectedInstallerSourceSha256.ToLowerInvariant()) 'source_hash_mismatch'
    $prerequisitePath = Join-Path $repository 'installer\visual-cpp-runtime.iss'
    Assert-NoReparseAncestors $prerequisitePath
    $prerequisiteStream = [IO.FileStream]::new($prerequisitePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Assert-Fixture ($prerequisiteStream.Length -gt 0 -and $prerequisiteStream.Length -le 1MB) 'prerequisite_source_size'
    $memory = [IO.MemoryStream]::new()
    try { $prerequisiteStream.CopyTo($memory); $prerequisiteBytes = $memory.ToArray() }
    finally { $memory.Dispose() }
    $prerequisiteHash = Get-BytesHash $prerequisiteBytes
    Assert-Fixture ($prerequisiteHash -ceq $ExpectedPrerequisiteSourceSha256.ToLowerInvariant()) 'prerequisite_source_hash_mismatch'
    $sourceText = $encoding.GetString($sourceBytes)
    if ($sourceText.Length -gt 0 -and $sourceText[0] -eq [char]0xfeff) { $sourceText = $sourceText.Substring(1) }
    Assert-Fixture ($sourceText.Contains('CloseApplications=force') -and
        $sourceText.Contains('RestartApplications=no') -and
        $sourceText.Contains("if not IsUpdaterMode() then`r`n    StopOwnedProcessesForInstallOrUninstall();")) 'updater_source_contract_changed'

    if ([string]::IsNullOrWhiteSpace($IsccPath)) {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $IsccPath = $candidates | Where-Object { [IO.File]::Exists($_) } | Select-Object -First 1
    }
    Assert-Fixture (-not [string]::IsNullOrWhiteSpace($IsccPath)) 'inno_compiler_missing_no_download_attempted'
    $compilerPath = [IO.Path]::GetFullPath($IsccPath)
    Assert-NoReparseAncestors $compilerPath
    Assert-Fixture ([IO.File]::Exists($compilerPath) -and [IO.Path]::GetFileName($compilerPath) -ieq 'ISCC.exe') 'compiler_path_invalid'

    Assert-NoReparseAncestors $fixtureRoot
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $payloadDirectory = Assert-OwnedPath (Join-Path $fixtureRoot 'payload')
    $outputDirectory = Assert-OwnedPath (Join-Path $fixtureRoot 'compiled')
    $targetDirectory = Assert-OwnedPath (Join-Path $fixtureRoot 'target')
    $scratchDirectory = Assert-OwnedPath (Join-Path $fixtureRoot 'scratch')
    foreach ($directory in @($payloadDirectory, $outputDirectory, $targetDirectory, $scratchDirectory)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $payloadPath = Assert-OwnedPath (Join-Path $payloadDirectory 'keyboard.wtf.exe')
    $targetPath = Assert-OwnedPath (Join-Path $targetDirectory 'keyboard.wtf.exe')
    $installerPath = Assert-OwnedPath (Join-Path $outputDirectory 'keyboard-wtf-inert-setup.exe')
    $fixtureSourcePath = Assert-OwnedPath (Join-Path $fixtureRoot 'keyboard-wtf.fixture.iss')
    $fixtureName = 'keyboard.wtf Inert Updater Fixture ' + $fixtureId
    $defines = [ordered]@{
        MyAppName = $fixtureName; MyAppPublisher = $fixtureName
        MyAppId = '{{' + ([Guid]::ParseExact($fixtureId, 'N').ToString('D').ToUpperInvariant()) + '}'
        MyAppExeName = 'keyboard.wtf.exe'; MyDefaultDirName = $targetDirectory
        MyOutputBaseFilename = 'keyboard-wtf-inert-setup'; MySourceDir = $payloadDirectory
        MyRunValueName = 'kwt-updater-fixture-' + $fixtureId; MyDefaultGroupName = $fixtureName
        MyShortcutName = $fixtureName; MyUserDataDir = 'kwt-updater-fixture-profile-' + $fixtureId
        MyPrerequisiteCode = Assert-OwnedPath (Join-Path $fixtureRoot 'visual-cpp-runtime.iss')
    }
    $prefixLines = [Collections.Generic.List[string]]::new()
    foreach ($definition in $defines.GetEnumerator()) {
        Assert-Fixture ($definition.Value -notmatch '["\r\n]') 'invalid_define_value'
        $prefixLines.Add('#define ' + $definition.Key + ' "' + $definition.Value + '"')
    }
    $prefixLines.Add('#define MySkipStartup 1')
    $prefixLines.Add('#define MyPreserveNativeHostRegistration 1')
    $prefix = ($prefixLines -join "`r`n") + "`r`n`r`n"
    $icons = Get-InnoSection $sourceText 'Icons'
    $fixtureText = Replace-OneLiteral $sourceText $icons '' 'remove_shortcut_section'
    $fixtureText = Replace-OneLiteral $fixtureText 'OutputDir=..\dist\installer' ('OutputDir=' + $outputDirectory) 'contain_compiler_output'
    $fixtureText = Replace-OneLiteral $fixtureText '[Languages]' "Uninstallable=no`r`nCreateUninstallRegKey=no`r`n`r`n[Languages]" 'disable_uninstaller_and_registration'
    $originalRegistry = "NativeHostRegistryPath = 'Software\Google\Chrome\NativeMessagingHosts\wtf.keyboard.browser';"
    $fixtureRegistry = "NativeHostRegistryPath = 'Software\keyboard.wtf-updater-fixture\$fixtureId\NativeHost';"
    $fixtureText = Replace-OneLiteral $fixtureText $originalRegistry $fixtureRegistry 'isolate_dormant_native_registry_constant'
    $transformations.Add([ordered]@{ reason = 'prepend_fixture_only_defines'; before = ''; after = $prefix })
    $fixtureText = $prefix + $fixtureText
    $originalCode = Get-InnoSection $sourceText 'Code'
    $fixtureCode = Get-InnoSection $fixtureText 'Code'
    Assert-Fixture ($fixtureCode -ceq $originalCode.Replace($originalRegistry, $fixtureRegistry)) 'updater_code_not_preserved'
    Assert-Fixture ((Get-InnoSection $fixtureText 'Files') -ceq (Get-InnoSection $sourceText 'Files') -and
        (Get-InnoSection $fixtureText 'Run') -ceq (Get-InnoSection $sourceText 'Run')) 'payload_or_guarded_run_changed'
    $newBytes = $encoding.GetBytes("INERT TEXT - NOT A WINDOWS EXECUTABLE`r`nfixture=$fixtureId`r`npayload=new`r`n")
    $oldBytes = $encoding.GetBytes("INERT TEXT - NOT A WINDOWS EXECUTABLE`r`nfixture=$fixtureId`r`npayload=old-locked`r`n")
    Assert-Fixture ($newBytes[0] -ne 0x4d -and $oldBytes[0] -ne 0x4d) 'payload_must_not_be_pe'
    [IO.File]::WriteAllBytes($payloadPath, $newBytes)
    [IO.File]::WriteAllBytes((Assert-OwnedPath (Join-Path $fixtureRoot 'keyboard-wtf.original.iss')), $sourceBytes)
    [IO.File]::WriteAllBytes((Assert-OwnedPath (Join-Path $fixtureRoot 'visual-cpp-runtime.iss')), $prerequisiteBytes)
    [IO.File]::WriteAllText($fixtureSourcePath, $fixtureText, $encoding)
    $receipt = [ordered]@{
        schemaVersion = 1; fixtureId = $fixtureId; fixtureRoot = $fixtureRoot
        startedUtc = [DateTimeOffset]::UtcNow.ToString('O'); completedUtc = $null
        originalSource = $sourcePath; originalSourceSha256 = $sourceHash
        prerequisiteSourceSha256 = $prerequisiteHash; prerequisiteSourceReadLockedThroughout = $true
        originalSourceReadLockedThroughout = $true
        installerSha256 = $null
        transformedSourceSha256 = Get-BytesHash ($encoding.GetBytes($fixtureText))
        originalCodeSha256 = Get-BytesHash ($encoding.GetBytes($originalCode))
        updaterCodePreservedExceptIsolatedRegistryConstant = $true
        transformations = $transformations; compiler = $compilerPath
        compilerSha256 = (Get-FileHash -LiteralPath $compilerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        inertPayloadSha256 = Get-BytesHash $newBytes
        shortcutsRemoved = $true; startupCompiledOut = $true; nativeRegistrationPauseCompiledOut = $true
        uninstallDisabled = $true; guardedRunEntryPreserved = $true
        processes = $processReceipts; unlocked = $null; locked = $null
        diagnosticTimeoutSeconds = $TimeoutSeconds; firstFailure = $null
        fixtureChecksPassed = $false; safeUpdateInstallationReadyActivated = $false
        actualAppOrNativeHostLaunched = $false; workerHandshakeProven = $false
        realInstallerBytesOrInstalledProductProven = $false; ingressExclusionOrRelaunchProven = $false
        scope = 'Transformed current updater branch with inert local bytes; no app/profile/provider, registry mutation, cleanup or cloud operation.'
    }
    Save-Receipt
    $compileExit = Invoke-OwnedProcess 'compile' $compilerPath @($fixtureSourcePath)
    Assert-Fixture ($compileExit -eq 0 -and [IO.File]::Exists($installerPath)) 'fixture_compile_failed'
    $receipt.installerSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $successLog = Assert-OwnedPath (Join-Path $fixtureRoot 'unlocked.install.log')
    $successExit = Invoke-OwnedProcess 'unlocked' $installerPath (Get-SetupArguments $successLog)
    $successLogText = Read-SetupLog $successLog
    $observedHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $receipt.unlocked = [ordered]@{ exitCode = $successExit; targetSha256 = $observedHash
        exactInertBytesInstalled = ($observedHash -ceq $receipt.inertPayloadSha256)
        runEntryLogMarkerObserved = $false; logSha256 = (Get-FileHash -LiteralPath $successLog -Algorithm SHA256).Hash.ToLowerInvariant() }
    Save-Receipt
    Assert-Fixture ($successExit -eq 0 -and $receipt.unlocked.exactInertBytesInstalled) 'unlocked_install_not_exact_success'

    if ($IncludeLockedPayload) {
        [void](Assert-OwnedPath $targetPath)
        [IO.File]::WriteAllBytes($targetPath, $oldBytes)
        $payloadLock = [IO.FileStream]::new($targetPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $oldHash = Get-HeldHash $payloadLock
        Assert-Fixture ($oldHash -ceq (Get-BytesHash $oldBytes) -and $oldHash -cne $receipt.inertPayloadSha256) 'locked_fixture_not_distinct'
        $lockLog = Assert-OwnedPath (Join-Path $fixtureRoot 'locked.install.log')
        $receipt.locked = [ordered]@{ oldSha256 = $oldHash; fileShare = 'Read (denies write and delete)'
            heldThroughOriginalProcessAndIoJoin = $false; exitCode = $null; observedOutcome = 'pending'
            targetUnchangedWhileHeld = $null; acceptedAsInstallSuccess = $false; logSha256 = $null }
        Save-Receipt
        $lockedExit = Invoke-OwnedProcess 'locked' $installerPath (Get-SetupArguments $lockLog)
        $lockLogText = Read-SetupLog $lockLog
        $heldAfterHash = Get-HeldHash $payloadLock
        $targetAfterHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $receipt.locked.heldThroughOriginalProcessAndIoJoin = $true
        $receipt.locked.exitCode = $lockedExit
        $receipt.locked.targetUnchangedWhileHeld = ($heldAfterHash -ceq $oldHash -and $targetAfterHash -ceq $oldHash)
        $receipt.locked.observedOutcome = if ($lockedExit -eq 3010) { 'reboot-required-not-immediate-success' }
            elseif ($lockedExit -ne 0) { 'nonzero-refusal-or-abort-see-original-log' }
            else { 'unexpected-zero-exit-under-held-lock' }
        $receipt.locked.logSha256 = (Get-FileHash -LiteralPath $lockLog -Algorithm SHA256).Hash.ToLowerInvariant()
        Save-Receipt
        Assert-Fixture ($lockedExit -ne 0 -and $receipt.locked.targetUnchangedWhileHeld) 'locked_run_did_not_truthfully_refuse_or_defer'
        $payloadLock.Dispose()
        $payloadLock = $null
    }
    Assert-Fixture ((Get-HeldHash $sourceStream) -ceq $sourceHash) 'original_source_changed'
    Assert-Fixture ((Get-HeldHash $prerequisiteStream) -ceq $prerequisiteHash) 'prerequisite_source_changed'
    $receipt.fixtureChecksPassed = $true
    $receipt.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Save-Receipt
    $receipt | ConvertTo-Json -Depth 12
}
catch {
    if ($null -ne $receipt) {
        if ($null -eq $receipt.firstFailure) { $receipt.firstFailure = $_.Exception.ToString() }
        $receipt.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        try { Save-Receipt } catch { [Console]::Error.WriteLine('Could not save final receipt; previously owned artifacts are retained.') }
    }
    throw
}
finally {
    if ($null -ne $payloadLock) { $payloadLock.Dispose() }
    if ($null -ne $sourceStream) { $sourceStream.Dispose() }
    if ($null -ne $prerequisiteStream) { $prerequisiteStream.Dispose() }
    # Deliberately no deletion. Compiler/setup logs, source copies, receipts,
    # target bytes and any failed-install residue remain for operator review.
}
