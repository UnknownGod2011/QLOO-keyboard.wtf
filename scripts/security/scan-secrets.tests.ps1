$ErrorActionPreference = 'Stop'

# Test the ACTUAL scanner in positively joined child pwsh processes against
# GUID-owned inert LOCAL Git indexes. No commits, remotes, real credentials,
# app/profile, providers, installer, deployment or history rewrite is used.
# Runtime-assembled pattern sentinels are intentionally absent from this file.
$scannerPath = Join-Path $PSScriptRoot 'scan-secrets.ps1'
$pwshPath = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$gitPath = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$passed = 0
$failed = [Collections.Generic.List[string]]::new()
$ordinarySentinel = (([char[]]@(115, 107, 45)) -join '') + ('z' * 24)
$publicSentinel = (([char[]]@(65, 73, 122, 97)) -join '') + ('y' * 34)
$safeEncoding = [Text.UTF8Encoding]::new($false)
$childTimeout = [TimeSpan]::FromSeconds(30)

function Assert-ScanTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "source_scan_test:$Message" }
}

function Assert-OwnedRoot {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    Assert-ScanTest ([IO.Path]::GetDirectoryName($resolved) -ceq $temp -and
        [IO.Path]::GetFileName($resolved) -cmatch '\Akwt-secret-scan-[a-f0-9]{32}\z') 'owned_root_invalid'
    return $resolved
}

function New-ScanFixture {
    $fixture = [pscustomobject]@{
        Root = Join-Path ([IO.Path]::GetTempPath()) ('kwt-secret-scan-' + [Guid]::NewGuid().ToString('N'))
        Repository = $null
        Children = [Collections.Generic.List[object]]::new()
    }
    $fixture.Root = Assert-OwnedRoot $fixture.Root
    $fixture.Repository = Join-Path $fixture.Root 'repository'
    [IO.Directory]::CreateDirectory($fixture.Repository) | Out-Null
    try {
        $initialize = Invoke-OwnedChild $fixture $gitPath @('-C', $fixture.Repository, 'init', '--quiet')
        Assert-ScanTest ($initialize.ExitCode -eq 0) 'synthetic_git_init_failed'
        return $fixture
    }
    catch {
        Remove-ScanFixture $fixture
        throw
    }
}

function Invoke-OwnedChild {
    param($Fixture, [string]$Executable, [string[]]$Arguments)
    [void](Assert-OwnedRoot $Fixture.Root)
    Assert-ScanTest ($Executable -in @($pwshPath, $gitPath)) 'unexpected_child_executable'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $Fixture.Repository
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    # Deterministic inert Git init/add: no user templates, hooks, global config,
    # optional index locks, signing, pager, credentials or network operation.
    $start.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $start.Environment['GIT_CONFIG_GLOBAL'] = Join-Path $Fixture.Root 'absent-global-config'
    $start.Environment['GIT_TEMPLATE_DIR'] = Join-Path $Fixture.Root 'absent-templates'
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $record = [pscustomobject]@{ Process = $process; Started = $false; Exit = $null; Output = $null; Error = $null; Joined = $false }
    $Fixture.Children.Add($record)
    Assert-ScanTest ($process.Start()) 'owned_child_not_started'
    $record.Started = $true
    $record.Exit = $process.WaitForExitAsync()
    $record.Output = $process.StandardOutput.ReadToEndAsync()
    $record.Error = $process.StandardError.ReadToEndAsync()
    # A timeout is only an abandoned waiter, never successful child completion.
    # Cleanup below must join the ORIGINAL process/read tasks or retain evidence.
    $originals = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($record.Exit, $record.Output, $record.Error))
    $originals.WaitAsync($childTimeout).GetAwaiter().GetResult()
    Assert-ScanTest ($process.HasExited -and $record.Exit.IsCompletedSuccessfully -and
        $record.Output.IsCompletedSuccessfully -and $record.Error.IsCompletedSuccessfully) 'child_not_positively_joined'
    $record.Joined = $true
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $record.Output.Result
        Error = $record.Error.Result
    }
}

function Write-FixtureText {
    param($Fixture, [string]$Relative, [string]$Text)
    Assert-ScanTest ($Relative -notmatch '(^|[/\\])\.\.([/\\]|$)' -and -not [IO.Path]::IsPathRooted($Relative)) 'unsafe_fixture_relative_path'
    $path = [IO.Path]::GetFullPath((Join-Path $Fixture.Repository $Relative))
    Assert-ScanTest ($path.StartsWith($Fixture.Repository + '\', [StringComparison]::OrdinalIgnoreCase)) 'fixture_file_escaped'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text, $safeEncoding)
    return $path
}

function Track-FixtureFile {
    param($Fixture, [string]$Relative)
    $add = Invoke-OwnedChild $Fixture $gitPath @('-C', $Fixture.Repository, '-c', 'core.hooksPath=/dev/null', 'add', '--', $Relative)
    Assert-ScanTest ($add.ExitCode -eq 0) 'synthetic_git_add_failed'
}

function Invoke-Scanner {
    param($Fixture)
    $result = Invoke-OwnedChild $Fixture $pwshPath @('-NoProfile', '-NonInteractive', '-File', $scannerPath, '-RepositoryRoot', $Fixture.Repository)
    $combined = $result.Output + $result.Error
    Assert-ScanTest (-not $combined.Contains($ordinarySentinel) -and -not $combined.Contains($publicSentinel)) 'sentinel_content_leaked'
    return $result
}

function Assert-ScannerPass {
    param($Result)
    Assert-ScanTest ($Result.ExitCode -eq 0 -and
        $Result.Output.Contains('Tracked and untracked source secret scan passed.')) 'expected_scan_pass'
}

function Assert-ScannerRefusal {
    param($Result, [string]$Code)
    Assert-ScanTest ($Result.ExitCode -ne 0) 'unscanned_input_falsely_passed'
    Assert-ScanTest (-not $Result.Output.Contains('Tracked and untracked source secret scan passed.')) 'failure_claimed_scan_pass'
    $combined = $Result.Output + $Result.Error
    Assert-ScanTest ($combined.Contains($Code)) "expected_refusal_$Code"
    # Existing pwsh formatting can show script source locations. The new
    # coverage code itself must not include the private fixture path/content.
    if ($Code.StartsWith('source_scan_input_', [StringComparison]::Ordinal)) {
        foreach ($line in $combined.Split("`n")) {
            if ($line.Contains($Code)) {
                Assert-ScanTest (-not $line.Contains('kwt-secret-scan-') -and
                    -not $line.Contains($ordinarySentinel) -and -not $line.Contains($publicSentinel)) 'coverage_code_leaks_input'
            }
        }
    }
}

function Remove-ScanFixture {
    param($Fixture)
    $resolved = Assert-OwnedRoot $Fixture.Root
    # Do not dispose/delete underneath a started child or its output drains.
    # Unknown completion retains the owned tree and fails; no force-kill guess.
    foreach ($record in $Fixture.Children) {
        if ($record.Started) {
            $originals = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($record.Exit, $record.Output, $record.Error))
            $originals.WaitAsync($childTimeout).GetAwaiter().GetResult()
            Assert-ScanTest ($record.Process.HasExited -and $originals.IsCompletedSuccessfully) 'cleanup_child_still_active'
            $record.Joined = $true
        }
        $record.Process.Dispose()
    }
    if (-not [IO.Directory]::Exists($resolved)) { return }
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $directories = [Collections.Generic.List[IO.DirectoryInfo]]::new()
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($resolved))
    $entries = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-ScanTest (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'cleanup_reparse_refused'
        $directories.Add($directory)
        foreach ($item in $directory.EnumerateFileSystemInfos()) {
            $entries++
            Assert-ScanTest ($entries -le 128 -and
                $item.FullName.StartsWith($resolved + '\', [StringComparison]::OrdinalIgnoreCase)) 'cleanup_scope_invalid'
            Assert-ScanTest (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'cleanup_reparse_refused'
            if ($item -is [IO.DirectoryInfo]) { $pending.Push($item) }
            else { $files.Add($item) }
        }
    }
    # Complete bounded path/reparse validation BEFORE only explicit leaf/direct
    # deletion. No recursive command, glob target or alternate cleanup shell.
    foreach ($file in $files) {
        if (($file.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
            $file.Attributes = $file.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
        }
        $file.Delete()
    }
    for ($index = $directories.Count - 1; $index -ge 0; $index--) { $directories[$index].Delete() }
}

function Assert-Case {
    param([string]$Name, [scriptblock]$Action)
    $fixture = $null
    $failure = $null
    try {
        $fixture = New-ScanFixture
        & $Action $fixture
    }
    catch { $failure = $_.Exception }
    finally {
        if ($null -ne $fixture) {
            # Cleanup failures remain fatal and stop the suite; uncertain child
            # completion is not one more ordinary accepted regression failure.
            Remove-ScanFixture $fixture
        }
    }
    if ($null -ne $failure) {
        $failed.Add($Name)
        Write-Host ("FAIL: {0}; {1}" -f $Name, $failure.Message)
    }
    else { $script:passed++ }
}

Assert-Case 'Empty local repository preserves scan success' {
    param($fixture)
    Assert-ScannerPass (Invoke-Scanner $fixture)
}
Assert-Case 'Ordinary supported untracked source remains scanned and allowed' {
    param($fixture)
    [void](Write-FixtureText $fixture 'ordinary.cs' 'class Harmless { }')
    Assert-ScannerPass (Invoke-Scanner $fixture)
}
Assert-Case 'Ordinary supported tracked source preserves scan success' {
    param($fixture)
    [void](Write-FixtureText $fixture 'ordinary.json' '{"harmless":true}')
    Track-FixtureFile $fixture 'ordinary.json'
    Assert-ScannerPass (Invoke-Scanner $fixture)
}
Assert-Case 'Exactly five MiB of ordinary supported text remains allowed' {
    param($fixture)
    $path = Write-FixtureText $fixture 'boundary.txt' (' ' * 5MB)
    Assert-ScanTest ([IO.FileInfo]::new($path).Length -eq 5MB) 'exact_size_fixture'
    Assert-ScannerPass (Invoke-Scanner $fixture)
}
Assert-Case 'Ordinary unsupported binary extension preserves existing exclusion policy' {
    param($fixture)
    [void](Write-FixtureText $fixture 'ordinary.bin' 'inert unsupported format')
    Assert-ScannerPass (Invoke-Scanner $fixture)
}
Assert-Case 'Small runtime-assembled pattern sentinel is still rejected' {
    param($fixture)
    [void](Write-FixtureText $fixture 'pattern.cs' $ordinarySentinel)
    Assert-ScannerRefusal (Invoke-Scanner $fixture) 'Potential secret material exists'
}
foreach ($forbidden in @('.env', 'service-account-fixture.json', 'inert.pfx')) {
    Assert-Case "Existing forbidden filename remains refused: $forbidden" {
        param($fixture)
        [void](Write-FixtureText $fixture $forbidden 'inert filename control')
        Assert-ScannerRefusal (Invoke-Scanner $fixture) 'Potential secret material exists'
    }
}
foreach ($publicPath in @('staging/site/config.js', 'production/site/config.js')) {
    Assert-Case "Existing public Firebase configuration exception remains unchanged: $publicPath" {
        param($fixture)
        $configuration = 'const config = { apiKey: "' + $publicSentinel + '" };'
        [void](Write-FixtureText $fixture $publicPath $configuration)
        Assert-ScannerPass (Invoke-Scanner $fixture)
    }
}
Assert-Case 'The same Firebase-style sentinel outside public config remains rejected' {
    param($fixture)
    [void](Write-FixtureText $fixture 'ordinary.json' ('{"value":"' + $publicSentinel + '"}'))
    Assert-ScannerRefusal (Invoke-Scanner $fixture) 'Potential secret material exists'
}
Assert-Case 'Public config exception does not suppress the existing unrelated pattern check' {
    param($fixture)
    [void](Write-FixtureText $fixture 'production/site/config.js' $ordinarySentinel)
    Assert-ScannerRefusal (Invoke-Scanner $fixture) 'Potential secret material exists'
}
Assert-Case 'Scanner source self-exclusion policy stays unchanged' {
    param($fixture)
    [void](Write-FixtureText $fixture 'scripts/security/scan-secrets.ps1' $ordinarySentinel)
    Assert-ScannerPass (Invoke-Scanner $fixture)
}
Assert-Case 'Ordinary supported Unicode tracked and untracked filenames remain allowed' {
    param($fixture)
    [void](Write-FixtureText $fixture 'résumé.cs' 'class HarmlessUnicode { }')
    Track-FixtureFile $fixture 'résumé.cs'
    [void](Write-FixtureText $fixture 'nested/文档.cs' 'class AnotherHarmlessUnicode { }')
    Assert-ScannerPass (Invoke-Scanner $fixture)
}
foreach ($unicodeCase in @(
    [pscustomobject]@{ Relative = 'résumé.cs'; Tracked = $true },
    [pscustomobject]@{ Relative = 'nested/文档.cs'; Tracked = $false }
)) {
    Assert-Case "Supported Unicode filename pattern must be scanned (tracked=$($unicodeCase.Tracked))" {
        param($fixture)
        [void](Write-FixtureText $fixture $unicodeCase.Relative $ordinarySentinel)
        if ($unicodeCase.Tracked) { Track-FixtureFile $fixture $unicodeCase.Relative }
        # Independently demonstrate default Git's QUOTED path representation,
        # not a misheard/provider issue or nonexistent supported extension.
        # This diagnostic only emits paths, never opens or prints file content.
        $listing = Invoke-OwnedChild $fixture $gitPath @('-C', $fixture.Repository, 'ls-files', '-co', '--exclude-standard')
        Assert-ScanTest ($listing.ExitCode -eq 0 -and
            [Text.RegularExpressions.Regex]::IsMatch($listing.Output, '(?m)^".*\.cs"\r?$')) 'unicode_git_quote_fixture'
        Assert-ScannerRefusal (Invoke-Scanner $fixture) 'Potential secret material exists'
    }
}
Assert-Case 'Ordinary supported filename with spaces remains allowed' {
    param($fixture)
    [void](Write-FixtureText $fixture 'ordinary source.cs' 'class HarmlessSpaces { }')
    Assert-ScannerPass (Invoke-Scanner $fixture)
}
Assert-Case 'Supported filename with spaces still receives existing pattern checks' {
    param($fixture)
    [void](Write-FixtureText $fixture 'pattern source.cs' $ordinarySentinel)
    Assert-ScannerRefusal (Invoke-Scanner $fixture) 'Potential secret material exists'
}
Assert-Case 'Missing tracked supported source cannot claim complete scan success' {
    param($fixture)
    $path = Write-FixtureText $fixture 'missing.cs' 'inert tracked file'
    Track-FixtureFile $fixture 'missing.cs'
    [IO.File]::Delete($path)
    Assert-ScannerRefusal (Invoke-Scanner $fixture) 'source_scan_input_missing'
}
Assert-Case 'Oversized supported untracked text without a pattern cannot silently pass' {
    param($fixture)
    $path = Write-FixtureText $fixture 'large.json' (' ' * ((5MB) + 1))
    Assert-ScanTest ([IO.FileInfo]::new($path).Length -gt 5MB) 'oversize_fixture'
    Assert-ScannerRefusal (Invoke-Scanner $fixture) 'source_scan_input_oversized'
}
Assert-Case 'Oversized supported untracked text with a pattern cannot bypass scan coverage' {
    param($fixture)
    $path = Write-FixtureText $fixture 'large.json' ((' ' * 5MB) + $ordinarySentinel)
    Assert-ScanTest ([IO.FileInfo]::new($path).Length -gt 5MB) 'oversize_sentinel_fixture'
    Assert-ScannerRefusal (Invoke-Scanner $fixture) 'source_scan_input_oversized'
}
Assert-Case 'Oversized supported tracked text cannot bypass scan coverage' {
    param($fixture)
    [void](Write-FixtureText $fixture 'large.txt' (' ' * ((5MB) + 1)))
    Track-FixtureFile $fixture 'large.txt'
    Assert-ScannerRefusal (Invoke-Scanner $fixture) 'source_scan_input_oversized'
}

[pscustomobject][ordered]@{
    passed = $passed; failed = $failed.Count; failingCases = $failed.ToArray()
    fixtureScope = 'GUID-local inert Git indexes and actual joined scanner children'
    realCredentialsUsed = $false; realRepositoryChanged = $false; networkUsed = $false
    fullHistoryCleared = $false; unsupportedFormatsCleared = $false
} | ConvertTo-Json -Depth 4
if ($failed.Count -ne 0) { throw 'Secret scanner regression suite failed.' }
