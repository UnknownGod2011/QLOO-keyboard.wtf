param([switch]$ReservedBasenameReviewOnly, [switch]$RuntimeMarkersReviewOnly)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'test-public-package-inventory.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('kwt-inventory-gate-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $fixtureRoot 'package'
$policyPath = Join-Path $fixtureRoot 'policy.json'
$passed = 0

function Remove-InventoryFixtureTree {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved -cne $fixtureRoot -and -not $resolved.StartsWith($fixtureRoot + '\', [StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the owned inventory fixture.'
    }
    if (-not [IO.Directory]::Exists($resolved)) { return }
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $directories = [Collections.Generic.List[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($resolved))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $directory.Delete() # Remove this link, never its target.
            continue
        }
        $directories.Add($directory)
        foreach ($item in $directory.EnumerateFileSystemInfos()) {
            if ($item -is [IO.DirectoryInfo]) { $pending.Push($item) }
            else { $item.Delete() }
        }
    }
    for ($index = $directories.Count - 1; $index -ge 0; $index--) { $directories[$index].Delete() }
}

function Get-FixtureHash {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-InventoryPolicy {
    param($Policy)
    [IO.File]::WriteAllText($policyPath, ($Policy | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Write-InventoryFixture {
    Remove-InventoryFixtureTree $package
    [IO.Directory]::CreateDirectory((Join-Path $package 'browser-helper')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $package 'keyboard.wtf.exe'), 'synthetic-application')
    [IO.File]::WriteAllText((Join-Path $package 'browser-helper\manifest.json'), 'synthetic-helper')
    $policy = [ordered]@{
        schemaVersion = 1
        files = @(
            [ordered]@{ path = 'keyboard.wtf.exe'; required = $true; minBytes = 1; maxBytes = 128; sha256 = Get-FixtureHash (Join-Path $package 'keyboard.wtf.exe') },
            [ordered]@{ path = 'browser-helper/manifest.json'; required = $true; minBytes = 1; maxBytes = 128; sha256 = Get-FixtureHash (Join-Path $package 'browser-helper\manifest.json') }
        )
    }
    Write-InventoryPolicy $policy
    return $policy
}

function Assert-InventoryCase {
    param([string]$Name, [scriptblock]$Action)
    try { & $Action } catch { throw "Synthetic inventory case failed: $Name. $($_.Exception.Message)" }
    $script:passed++
}

function Assert-InventoryRejection {
    param([string]$Code, [scriptblock]$Action = { Test-PublicPackageInventory $package $policyPath })
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception }
    if ($null -eq $caught -or $caught.Message -cne $Code) {
        throw "Expected rejection $Code; actual code was $($caught.Message)."
    }
    if ($caught.Message.Contains($fixtureRoot) -or $caught.Message.Contains('sensitive-fixture-value')) {
        throw 'Rejection leaked input content or an absolute path.'
    }
}

function Get-InventoryFixtureSnapshot {
    @($policyPath, (Join-Path $package 'keyboard.wtf.exe'), (Join-Path $package 'browser-helper\manifest.json')) |
        ForEach-Object { (Get-FixtureHash $_) + ':' + [IO.FileInfo]::new($_).Length + ':' + [IO.File]::GetLastWriteTimeUtc($_).Ticks }
}

function Assert-ReservedBasenamePolicyRejection {
    param([string]$Relative)
    $policy = Write-InventoryFixture
    # OPTIONAL and absent: a missing-required-file rejection cannot hide an
    # incorrectly accepted path policy. No reserved-name file is opened.
    $policy.files += [ordered]@{ path = $Relative; required = $false; minBytes = 0; maxBytes = 1; sha256 = $null }
    Write-InventoryPolicy $policy
    Assert-InventoryRejection 'package_inventory_path_invalid'
}

function Assert-NonreservedBasenameSpaceControl {
    $policy = Write-InventoryFixture
    $relative = 'release notes.txt'
    $file = Join-Path $package $relative
    [IO.File]::WriteAllText($file, 'reviewed notes')
    $policy.files += [ordered]@{ path = $relative; required = $true; minBytes = 1; maxBytes = 32; sha256 = Get-FixtureHash $file }
    Write-InventoryPolicy $policy
    if ((Test-PublicPackageInventory $package $policyPath).fileCount -ne 3) {
        throw 'Ordinary nonreserved filename with spaces was rejected.'
    }
}

function Assert-RuntimeMarkerRejection {
    param([string]$Relative, [string]$Variant)
    $policy = Write-InventoryFixture
    $marker = Join-Path $package ($Relative.Replace('/', '\'))
    if ($Variant -ne 'optional-absent') { [IO.File]::WriteAllBytes($marker, [byte[]]::new(0)) }
    if ($Variant -ne 'unlisted-present') {
        $policy.files += [ordered]@{
            path = $Relative; required = ($Variant -eq 'listed-present')
            minBytes = 0; maxBytes = 1; sha256 = $null
        }
    }
    Write-InventoryPolicy $policy
    $before = Get-InventoryFixtureSnapshot
    Assert-InventoryRejection 'package_inventory_runtime_artifact'
    if (($before -join '|') -cne ((Get-InventoryFixtureSnapshot) -join '|')) {
        throw 'Runtime-marker refusal changed known package/policy bytes.'
    }
    if ($Variant -ne 'optional-absent' -and [IO.FileInfo]::new($marker).Length -ne 0) {
        throw 'Runtime-marker refusal changed the marker.'
    }
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    Assert-InventoryCase 'Dot-sourcing performs no validation and produces no output' {
        $result = @(. $scriptPath -PackageDirectory (Join-Path $fixtureRoot 'absent') -PolicyPath (Join-Path $fixtureRoot 'absent-policy'))
        if ($result.Count -ne 0) { throw 'Dot-source produced output.' }
    }
    # Explicit arguments preserve this script's same-named PolicyPath variable;
    # dot-sourcing naturally binds script parameters into the caller's scope.
    . $scriptPath -PackageDirectory $package -PolicyPath $policyPath
    $runtimeMarkerPaths = @(
        '.keyboard.wtf-update-reservation', '.keyboard.wtf-update-activity',
        '.KEYBOARD.WTF-UPDATE-RESERVATION', '.KEYBOARD.WTF-UPDATE-ACTIVITY',
        'browser-helper/.keyboard.wtf-update-reservation', 'browser-helper/.keyboard.wtf-update-activity',
        'browser-helper/.KEYBOARD.WTF-UPDATE-RESERVATION', 'browser-helper/.KEYBOARD.WTF-UPDATE-ACTIVITY'
    )
    if ($RuntimeMarkersReviewOnly) {
        $reviewPassed = 0
        $reviewFailed = 0
        foreach ($relative in $runtimeMarkerPaths) {
            foreach ($variant in @('optional-absent', 'listed-present', 'unlisted-present')) {
                try { Assert-RuntimeMarkerRejection $relative $variant; $reviewPassed++ }
                catch { $reviewFailed++ }
            }
        }
        [pscustomobject][ordered]@{
            passed = $reviewPassed; failed = $reviewFailed; fixtureScope = 'inert inventory/policy only'
            installerExecuted = $false; installedAppTouched = $false
        } | ConvertTo-Json
        if ($reviewFailed -ne 0) { throw 'Runtime-marker package review failed.' }
        return
    }
    if ($ReservedBasenameReviewOnly) {
        $reviewPassed = 0
        $reviewFailed = 0
        foreach ($relative in @('CON .txt', 'COM1 .txt', 'LPT1 .dat', 'CONIN$ .txt')) {
            try { Assert-ReservedBasenamePolicyRejection $relative; $reviewPassed++ }
            catch { $reviewFailed++ }
        }
        try { Assert-NonreservedBasenameSpaceControl; $reviewPassed++ }
        catch { $reviewFailed++ }
        [pscustomobject][ordered]@{
            passed = $reviewPassed; failed = $reviewFailed; fixtureScope = 'synthetic-policy-only'
            reservedNameFilesOpened = $false
        } | ConvertTo-Json
        if ($reviewFailed -ne 0) { throw 'Reserved-basename policy review failed.' }
        return
    }
    Assert-InventoryCase 'Valid explicit inventory matches without mutating inputs' {
        Write-InventoryFixture | Out-Null
        $before = Get-InventoryFixtureSnapshot
        $receipt = Test-PublicPackageInventory $package $policyPath
        $after = Get-InventoryFixtureSnapshot
        if (($before -join '|') -cne ($after -join '|') -or -not $receipt.policyInventoryMatched -or $receipt.fileCount -ne 2) {
            throw 'Valid inventory failed or inputs changed.'
        }
        if (($receipt.files.path -join '|') -cne 'browser-helper/manifest.json|keyboard.wtf.exe') { throw 'Receipt order is not ordinal.' }
        foreach ($limit in @('policyAuthorityVerified', 'publicReleaseApproved', 'publicFlavorProven', 'authenticodeVerified',
            'licensingCleared', 'installerInclusionProven', 'contentSecretFreeProven', 'concurrentWriterConfinementProven', 'postValidationBytesPinned')) {
            if ($receipt.$limit -ne $false) { throw "Receipt overstates $limit." }
        }
    }
    Assert-InventoryCase 'Direct invocation has a deterministic content-free proof receipt' {
        Write-InventoryFixture | Out-Null
        $before = Get-InventoryFixtureSnapshot
        $first = (& $scriptPath -PackageDirectory $package -PolicyPath $policyPath) -join "`n"
        $second = (& $scriptPath -PackageDirectory $package -PolicyPath $policyPath) -join "`n"
        if ($first -cne $second -or $first.Contains($fixtureRoot) -or $first.Contains('synthetic-application')) {
            throw 'Receipt changed or exposed input content/absolute paths.'
        }
        if (($before -join '|') -cne ((Get-InventoryFixtureSnapshot) -join '|')) { throw 'Direct invocation changed inputs.' }
        $receipt = $first | ConvertFrom-Json
        if ($receipt.policySha256 -cne (Get-FixtureHash $policyPath) -or $receipt.files[0].sha256 -cne (Get-FixtureHash (Join-Path $package 'browser-helper\manifest.json'))) {
            throw 'Receipt hashes do not describe observed bytes.'
        }
    }
    Assert-InventoryCase 'UTF-8 BOM is accepted without changing policy bytes' {
        Write-InventoryFixture | Out-Null
        $text = [IO.File]::ReadAllText($policyPath)
        [IO.File]::WriteAllText($policyPath, $text, [Text.UTF8Encoding]::new($true))
        $before = Get-FixtureHash $policyPath
        if ((Test-PublicPackageInventory $package $policyPath).policySha256 -cne $before -or (Get-FixtureHash $policyPath) -cne $before) {
            throw 'BOM handling changed bytes or hash.'
        }
    }
    Assert-InventoryCase 'Optional generated entry requires an explicit null hash and remains observed' {
        $policy = Write-InventoryFixture
        $policy.files += [ordered]@{ path = 'runtime.dll'; required = $false; minBytes = 1; maxBytes = 32; sha256 = $null }
        Write-InventoryPolicy $policy
        if ((Test-PublicPackageInventory $package $policyPath).fileCount -ne 2) { throw 'Absent optional entry failed.' }
        [IO.File]::WriteAllText((Join-Path $package 'runtime.dll'), 'generated')
        $receipt = Test-PublicPackageInventory $package $policyPath
        $generated = @($receipt.files | Where-Object path -CEQ 'runtime.dll')[0]
        if ($receipt.fileCount -ne 3 -or $generated.expectedHashPresent -ne $false -or
            $generated.sha256 -cne (Get-FixtureHash (Join-Path $package 'runtime.dll'))) { throw 'Generated exception was not accurately observed.' }
    }
    Assert-InventoryCase 'Hash spelling may use uppercase without relaxing byte identity' {
        $policy = Write-InventoryFixture
        $policy.files[0].sha256 = $policy.files[0].sha256.ToUpperInvariant()
        Write-InventoryPolicy $policy
        if (-not (Test-PublicPackageInventory $package $policyPath).policyInventoryMatched) { throw 'Uppercase digest failed.' }
    }
    foreach ($relative in $runtimeMarkerPaths) {
        foreach ($variant in @('optional-absent', 'listed-present', 'unlisted-present')) {
            Assert-InventoryCase "Runtime marker $relative $variant cannot be approved as package content" {
                Assert-RuntimeMarkerRejection $relative $variant
            }
        }
    }
    foreach ($marker in @('.keyboard.wtf-update-reservation', '.keyboard.wtf-update-activity')) {
        Assert-InventoryCase 'A runtime-marker directory segment is forbidden even in an absent optional policy' {
            $policy = Write-InventoryFixture
            $policy.files += [ordered]@{ path = "$marker/child.txt"; required = $false; minBytes = 0; maxBytes = 1; sha256 = $null }
            Write-InventoryPolicy $policy
            Assert-InventoryRejection 'package_inventory_runtime_artifact'
        }
        Assert-InventoryCase 'Nonreserved filename sharing only a marker substring remains valid' {
            $policy = Write-InventoryFixture
            $file = "$marker-notes.txt"
            [IO.File]::WriteAllText((Join-Path $package $file), 'inert reviewed notes')
            $policy.files += [ordered]@{ path = $file; required = $true; minBytes = 1; maxBytes = 32; sha256 = Get-FixtureHash (Join-Path $package $file) }
            Write-InventoryPolicy $policy
            if ((Test-PublicPackageInventory $package $policyPath).fileCount -ne 3) {
                throw 'An ordinary nonreserved filename was rejected.'
            }
        }
    }
    Assert-InventoryCase 'Required missing entry is refused' {
        Write-InventoryFixture | Out-Null
        [IO.File]::Delete((Join-Path $package 'keyboard.wtf.exe'))
        Assert-InventoryRejection 'package_inventory_required_missing'
    }
    foreach ($extra in @('private-recording.m4a', 'settings.json', 'personal-api-vault.dat', 'release-private.key', 'keyboard.wtf.pdb', 'test-harness.exe', 'rollback.zip')) {
        Assert-InventoryCase "Unlisted $extra is refused" {
            Write-InventoryFixture | Out-Null
            [IO.File]::WriteAllText((Join-Path $package $extra), 'sensitive-fixture-value')
            Assert-InventoryRejection 'package_inventory_extra_entry'
        }
    }
    Assert-InventoryCase 'Unlisted empty directories are also refused' {
        Write-InventoryFixture | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $package 'private-diagnostics')) | Out-Null
        Assert-InventoryRejection 'package_inventory_extra_entry'
    }
    Assert-InventoryCase 'Extra detection precedes opening any listed or unknown content' {
        Write-InventoryFixture | Out-Null
        $unknown = Join-Path $package 'extra.key'
        [IO.File]::WriteAllText($unknown, 'sensitive-fixture-value')
        $knownLock = [IO.FileStream]::new((Join-Path $package 'keyboard.wtf.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $unknownLock = [IO.FileStream]::new($unknown, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try { Assert-InventoryRejection 'package_inventory_extra_entry' }
        finally { $unknownLock.Dispose(); $knownLock.Dispose() }
    }
    foreach ($relative in @('../outside.txt', 'a/../b.txt', '/rooted.txt', 'C:/rooted.txt', '\\server\file', 'a\\b.txt',
        'a//b.txt', 'a/', './a.txt', '*.dll', 'a?.txt', 'a:b.txt', 'a.txt.', 'a.txt ', ' a.txt', 'CON', 'con.txt',
        'COM1.dll', 'LPT9', 'CONIN$', 'COM¹.txt', "bad`nname.txt")) {
        Assert-InventoryCase "Noncanonical relative path $relative is refused" {
            $policy = Write-InventoryFixture
            $policy.files[0].path = $relative
            Write-InventoryPolicy $policy
            Assert-InventoryRejection 'package_inventory_path_invalid'
        }
    }
    Assert-InventoryCase 'Case-insensitive duplicate policy paths are refused' {
        $policy = Write-InventoryFixture
        $policy.files += [ordered]@{ path = 'KEYBOARD.WTF.EXE'; required = $false; minBytes = 1; maxBytes = 128; sha256 = $null }
        Write-InventoryPolicy $policy
        Assert-InventoryRejection 'package_inventory_policy_invalid'
    }
    foreach ($relative in @('CON .txt', 'COM1 .txt', 'LPT1 .dat', 'CONIN$ .txt')) {
        Assert-InventoryCase "Optional absent reserved basename with suffix spacing $relative is refused" {
            Assert-ReservedBasenamePolicyRejection $relative
        }
    }
    Assert-InventoryCase 'Ordinary nonreserved basename spacing remains allowed' {
        Assert-NonreservedBasenameSpaceControl
    }
    foreach ($mutation in @('root-duplicate', 'entry-duplicate', 'root-unknown', 'entry-unknown', 'root-case', 'entry-case',
        'root-array', 'files-object', 'entry-string', 'missing-hash', 'null-path', 'number-path', 'string-required',
        'null-required', 'string-minimum', 'fraction-maximum', 'exponent-maximum', 'negative-minimum', 'zero-maximum',
        'reversed-bounds', 'over-resource-bound', 'hash-number', 'hash-empty', 'hash-invalid', 'schema-string', 'schema-fraction',
        'schema-other', 'files-empty', 'comments', 'trailing-comma', 'invalid-json', 'invalid-utf8', 'excess-depth', 'too-many-policy-files')) {
        Assert-InventoryCase "Strict policy mutation $mutation is refused" {
            $policy = Write-InventoryFixture
            $text = [IO.File]::ReadAllText($policyPath)
            switch ($mutation) {
                'root-duplicate' { $text = $text.Replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1') }
                'entry-duplicate' { $text = $text.Replace('"required": true', '"required": true, "required": true') }
                'root-unknown' { $policy['unknown'] = 1 }
                'entry-unknown' { $policy.files[0]['unknown'] = 1 }
                'root-case' { $text = $text.Replace('"schemaVersion"', '"SchemaVersion"') }
                'entry-case' { $text = $text.Replace('"path"', '"Path"') }
                'root-array' { $text = '[1,2]' }
                'files-object' { $policy.files = [ordered]@{ path = 'keyboard.wtf.exe' } }
                'entry-string' { $policy.files = @('entry') }
                'missing-hash' { $policy.files[0].Remove('sha256') }
                'null-path' { $policy.files[0].path = $null }
                'number-path' { $policy.files[0].path = 7 }
                'string-required' { $policy.files[0].required = 'true' }
                'null-required' { $policy.files[0].required = $null }
                'string-minimum' { $policy.files[0].minBytes = '1' }
                'fraction-maximum' { $policy.files[0].maxBytes = 128.5 }
                'exponent-maximum' { $text = $text.Replace('"maxBytes": 128', '"maxBytes": 1e2') }
                'negative-minimum' { $policy.files[0].minBytes = -1 }
                'zero-maximum' { $policy.files[0].maxBytes = 0 }
                'reversed-bounds' { $policy.files[0].minBytes = 129 }
                'over-resource-bound' { $policy.files[0].maxBytes = (512MB) + 1 }
                'hash-number' { $policy.files[0].sha256 = 3 }
                'hash-empty' { $policy.files[0].sha256 = '' }
                'hash-invalid' { $policy.files[0].sha256 = 'g' * 64 }
                'schema-string' { $policy.schemaVersion = '1' }
                'schema-fraction' { $policy.schemaVersion = 1.5 }
                'schema-other' { $policy.schemaVersion = 2 }
                'files-empty' { $policy.files = @() }
                'comments' { $text = $text.Insert(1, '/* not accepted */') }
                'trailing-comma' { $text = $text.TrimEnd().TrimEnd('}') + ',}' }
                'invalid-json' { $text = '{' }
                'invalid-utf8' { }
                'excess-depth' { $text = '{"schemaVersion":1,"files":[{"path":{"nested":{"tooDeep":{}}}}]}' }
                'too-many-policy-files' { $policy.files = @(1..2001 | ForEach-Object { [ordered]@{ path = "a$_.txt"; required = $false; minBytes = 0; maxBytes = 1; sha256 = $null } }) }
            }
            if ($mutation -in @('root-duplicate', 'entry-duplicate', 'root-case', 'entry-case', 'root-array',
                'exponent-maximum', 'comments', 'trailing-comma', 'invalid-json', 'excess-depth')) {
                [IO.File]::WriteAllText($policyPath, $text, [Text.UTF8Encoding]::new($false))
            }
            elseif ($mutation -eq 'invalid-utf8') { [IO.File]::WriteAllBytes($policyPath, [byte[]]@(0xff, 0xfe, 0xff)) }
            else { Write-InventoryPolicy $policy }
            $code = if ($mutation -eq 'invalid-utf8') { 'package_inventory_input_invalid' } else { 'package_inventory_policy_invalid' }
            Assert-InventoryRejection $code
        }
    }
    foreach ($length in @(0, ((1MB) + 1))) {
        Assert-InventoryCase "Policy byte length $length is refused before parsing" {
            Write-InventoryFixture | Out-Null
            $stream = [IO.File]::OpenWrite($policyPath)
            try { $stream.SetLength($length) } finally { $stream.Dispose() }
            Assert-InventoryRejection 'package_inventory_policy_size_invalid'
        }
    }
    Assert-InventoryCase 'Wrong expected hash is refused' {
        $policy = Write-InventoryFixture
        $policy.files[0].sha256 = '0' * 64
        Write-InventoryPolicy $policy
        Assert-InventoryRejection 'package_inventory_hash_mismatch'
    }
    foreach ($minimum in @($true, $false)) {
        Assert-InventoryCase 'Actual file size outside declared bounds is refused without large files' {
            $policy = Write-InventoryFixture
            if ($minimum) { $policy.files[0].minBytes = 100 } else { $policy.files[0].maxBytes = 2 }
            Write-InventoryPolicy $policy
            Assert-InventoryRejection 'package_inventory_file_size_invalid'
        }
    }
    Assert-InventoryCase 'Input lock conflict fails without exposing paths' {
        Write-InventoryFixture | Out-Null
        $locked = [IO.FileStream]::new((Join-Path $package 'keyboard.wtf.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try { Assert-InventoryRejection 'package_inventory_input_invalid' }
        finally { $locked.Dispose() }
    }
    Assert-InventoryCase 'Missing input and invalid empty arguments are truthful' {
        Write-InventoryFixture | Out-Null
        Assert-InventoryRejection 'package_inventory_input_missing' { Test-PublicPackageInventory (Join-Path $fixtureRoot 'absent') $policyPath }
        Assert-InventoryRejection 'package_inventory_input_invalid' { Test-PublicPackageInventory '' '' }
    }
    Assert-InventoryCase 'Empty package is not a successful release inventory' {
        $policy = Write-InventoryFixture
        $policy.files | ForEach-Object { $_.required = $false }
        Write-InventoryPolicy $policy
        [IO.File]::Delete((Join-Path $package 'keyboard.wtf.exe'))
        [IO.File]::Delete((Join-Path $package 'browser-helper\manifest.json'))
        Assert-InventoryRejection 'package_inventory_empty'
    }
    Assert-InventoryCase 'Real junction is rejected without reading or changing its target' {
        Write-InventoryFixture | Out-Null
        $external = Join-Path $fixtureRoot 'junction-target'
        [IO.Directory]::CreateDirectory($external) | Out-Null
        $targetFile = Join-Path $external 'untouched.txt'
        [IO.File]::WriteAllText($targetFile, 'untouched-target')
        $before = Get-FixtureHash $targetFile
        $junction = Join-Path $package 'junction'
        try {
            New-Item -ItemType Junction -Path $junction -Target $external -ErrorAction Stop | Out-Null
        }
        catch { throw 'Real junction fixture could not be created; reparse acceptance is unverified, not skipped.' }
        Assert-InventoryRejection 'package_inventory_reparse_invalid'
        Assert-InventoryRejection 'package_inventory_reparse_invalid' { Test-PublicPackageInventory $junction $policyPath }
        if ((Get-FixtureHash $targetFile) -cne $before) { throw 'Junction target changed.' }
        [IO.Directory]::Delete($junction)
    }
    Assert-InventoryCase 'Incremental traversal rejects excessive allowed directory growth' {
        $policy = Write-InventoryFixture
        $policy.files = @()
        foreach ($index in 1..280) {
            $relative = "d$index/" + ((1..14 | ForEach-Object { "s$_" }) -join '/') + '/a.txt'
            $policy.files += [ordered]@{ path = $relative; required = $false; minBytes = 0; maxBytes = 1; sha256 = $null }
            [IO.Directory]::CreateDirectory((Join-Path $package ($relative.Substring(0, $relative.LastIndexOf('/')).Replace('/', '\')))) | Out-Null
        }
        [IO.File]::Delete((Join-Path $package 'keyboard.wtf.exe'))
        Remove-InventoryFixtureTree (Join-Path $package 'browser-helper')
        Write-InventoryPolicy $policy
        Assert-InventoryRejection 'package_inventory_tree_limit'
    }
    Assert-InventoryCase 'Windows PowerShell refuses before path validation and can dot-source harmlessly' {
        $legacy = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
        if (-not [IO.File]::Exists($legacy)) { throw 'Windows PowerShell runtime fixture is unavailable, not skipped.' }
        $output = @(& $legacy -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $scriptPath `
            -PackageDirectory (Join-Path $fixtureRoot 'missing-runtime-probe') -PolicyPath (Join-Path $fixtureRoot 'missing-policy-probe') 2>&1)
        if ($LASTEXITCODE -ne 1 -or ($output -join "`n") -notmatch '\Apackage_inventory_runtime_unsupported\s*\z') {
            throw 'Unsupported runtime did not fail before input validation.'
        }
        $command = ". '$($scriptPath.Replace("'", "''"))'; if (-not (Get-Command Test-PublicPackageInventory -ErrorAction SilentlyContinue)) { exit 3 }; 'dot-source-ok'"
        $output = @(& $legacy -NoProfile -NonInteractive -Command $command 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -cne 'dot-source-ok') { throw 'Legacy dot-source was not safe.' }
    }
    [pscustomobject][ordered]@{ passed = $passed; failed = 0; fixtureScope = 'synthetic-only'; realJunctionExercised = $true; legacyRuntimeRejectionExercised = $true } | ConvertTo-Json
}
finally {
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -cne $tempParent -or
        [IO.Path]::GetFileName($resolved) -notmatch '\Akwt-inventory-gate-[a-f0-9]{32}\z') {
        throw 'Refusing unexpected fixture cleanup path.'
    }
    Remove-InventoryFixtureTree $resolved
}
