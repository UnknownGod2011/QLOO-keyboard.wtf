$ErrorActionPreference = 'Stop'
$toolPath = Join-Path $PSScriptRoot 'test-public-sbom-provenance.ps1'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('kwt-sbom-consistency-' + [Guid]::NewGuid().ToString('N'))))
$sbomFile = Join-Path $fixtureRoot 'sbom.json'
$inventoryFile = Join-Path $fixtureRoot 'inventory.json'
$provenanceFile = Join-Path $fixtureRoot 'provenance.json'
$commit = '0123456789abcdef0123456789abcdef01234567'
$version = '1.4.19'
$passed = 0

function Write-SbomJson {
    param([string]$Path, $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12 -Compress), [Text.UTF8Encoding]::new($false))
}

function New-SbomFile {
    param([string]$Name, [long]$Size, [string]$Hash)
    [ordered]@{
        type = 'file'; name = $Name
        hashes = @([ordered]@{ alg = 'SHA-256'; content = $Hash })
        properties = @([ordered]@{ name = 'keyboard.wtf:file-size'; value = $Size.ToString([Globalization.CultureInfo]::InvariantCulture) })
    }
}

function New-SbomLibrary {
    param([string]$Name = 'Synthetic.Library', [string]$Version = '1.2.3')
    [ordered]@{ type = 'library'; group = 'nuget'; name = $Name; version = $Version
        purl = 'pkg:nuget/' + [Uri]::EscapeDataString($Name) + '@' + [Uri]::EscapeDataString($Version) }
}

function New-SbomFixture {
    $hashA = 'a' * 64
    $hashB = 'b' * 64
    $inventory = [ordered]@{
        policyInventoryMatched = $true; policySha256 = 'c' * 64
        fileCount = 2; totalBytes = 32
        files = @(
            [ordered]@{ path = 'keyboard.wtf.exe'; sizeBytes = 20; sha256 = $hashA; expectedHashPresent = $false },
            [ordered]@{ path = 'browser-helper/manifest.json'; sizeBytes = 12; sha256 = $hashB; expectedHashPresent = $true }
        )
        policyAuthorityVerified = $false; publicReleaseApproved = $false; publicFlavorProven = $false
        authenticodeVerified = $false; licensingCleared = $false; installerInclusionProven = $false
        contentSecretFreeProven = $false; concurrentWriterConfinementProven = $false; postValidationBytesPinned = $false
        observationScope = 'read-locked bytes during this validation only; not pinned through packaging'
    }
    $sbom = [ordered]@{
        bomFormat = 'CycloneDX'; specVersion = '1.5'; serialNumber = 'urn:uuid:01234567-89ab-cdef-0123-456789abcdef'; version = 1
        metadata = [ordered]@{
            timestamp = '2026-10-04T12:34:56.1234567+00:00'
            tools = [ordered]@{ components = @([ordered]@{ type = 'application'; name = 'keyboard.wtf SBOM generator'; version = '1' }) }
            component = [ordered]@{
                type = 'application'; name = 'keyboard.wtf'; version = $version
                properties = @(
                    [ordered]@{ name = 'keyboard.wtf:source-commit'; value = $commit },
                    [ordered]@{ name = 'keyboard.wtf:target'; value = 'win-x64' },
                    [ordered]@{ name = 'keyboard.wtf:packaged-file-count'; value = '2' }
                )
            }
        }
        components = @((New-SbomFile 'keyboard.wtf.exe' 20 $hashA),
            (New-SbomFile 'browser-helper/manifest.json' 12 $hashB), (New-SbomLibrary))
    }
    $provenance = [ordered]@{
        sourceCommit = $commit; version = $version; buildDateUtc = '2026-10-04T12:35:00Z'
        installer = 'keyboard-wtf-setup.exe'; installerSha256 = 'd' * 64; signingKeyId = 'kwt-release-2026-01'
    }
    return [pscustomobject]@{ Sbom = $sbom; Inventory = $inventory; Provenance = $provenance }
}

function Save-SbomFixture {
    param($Fixture)
    Write-SbomJson $sbomFile $Fixture.Sbom
    Write-SbomJson $inventoryFile $Fixture.Inventory
    Write-SbomJson $provenanceFile $Fixture.Provenance
}

function Invoke-SbomFixture {
    Test-PublicSbomProvenance -SbomPath $sbomFile -InventoryReceiptPath $inventoryFile `
        -ProvenancePath $provenanceFile -ExpectedSourceCommit $commit -ExpectedVersion $version
}

function Get-SbomInputSnapshot {
    foreach ($path in @($sbomFile, $inventoryFile, $provenanceFile)) {
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash + ':' +
            [IO.FileInfo]::new($path).Length + ':' + [IO.File]::GetLastWriteTimeUtc($path).Ticks
    }
}

function Assert-SbomCase {
    param([string]$Name, [scriptblock]$Action)
    try { & $Action } catch { throw "Synthetic SBOM consistency case failed: $Name. $($_.Exception.Message)" }
    $script:passed++
}

function Assert-SbomRejection {
    param([string]$Code, [scriptblock]$Action = { Invoke-SbomFixture })
    $caughtFailure = $null
    try { & $Action | Out-Null } catch { $caughtFailure = $_.Exception }
    if ($null -eq $caughtFailure -or $caughtFailure.Message -cne $Code) { throw "Expected $Code; actual $($caughtFailure.Message)." }
    if ($caughtFailure.Message.Contains($fixtureRoot) -or $caughtFailure.Message.Contains('sensitive-fixture-value')) {
        throw 'Rejection leaked an absolute path or input content.'
    }
}

function Assert-SbomMutation {
    param([string]$Name, [scriptblock]$Mutation, [string]$Code = 'sbom_provenance_schema_invalid')
    Assert-SbomCase $Name {
        $fixture = New-SbomFixture
        & $Mutation $fixture
        Save-SbomFixture $fixture
        Assert-SbomRejection $Code
    }
}

function Remove-SbomFixture {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $name = [IO.Path]::GetFileName($resolved)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolved), $parent, [StringComparison]::OrdinalIgnoreCase) -or
        -not $name.StartsWith('kwt-sbom-consistency-', [StringComparison]::Ordinal)) { throw 'Unsafe fixture cleanup target.' }
    $identity = [Guid]::Empty
    if (-not [Guid]::TryParseExact($name.Substring('kwt-sbom-consistency-'.Length), 'N', [ref]$identity)) {
        throw 'Unsafe fixture identity.'
    }
    if (-not [IO.Directory]::Exists($resolved)) { return }
    if (([IO.File]::GetAttributes($resolved) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse fixture retained.' }
    $allowedFiles = @('sbom.json', 'inventory.json', 'provenance.json', 'inventory-policy.json',
        'package/readme.txt', 'package/browser-helper/manifest.json', 'links-target/sbom.json')
    $allowedDirectories = @('package', 'package/browser-helper', 'links-target')
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $directories = [Collections.Generic.List[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($resolved))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $directories.Add($directory)
        foreach ($item in $directory.EnumerateFileSystemInfos()) {
            $relative = $item.FullName.Substring($resolved.Length + 1).Replace('\', '/')
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                if ($relative -cne 'json-link' -or $item -isnot [IO.DirectoryInfo]) { throw 'Unknown reparse fixture retained.' }
                $item.Delete() # Delete only the owned junction, never follow it.
            }
            elseif ($item -is [IO.DirectoryInfo]) {
                if ($allowedDirectories -cnotcontains $relative) { throw 'Unknown fixture directory retained.' }
                $pending.Push($item)
            }
            else {
                if ($allowedFiles -cnotcontains $relative) { throw 'Unknown fixture file retained.' }
                $item.Delete()
            }
        }
    }
    for ($i = $directories.Count - 1; $i -ge 0; $i--) { $directories[$i].Delete() }
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    Assert-SbomCase 'Dot-source produces no output or input access' {
        $output = @(. $toolPath -SbomPath 'Z:\missing.json' -InventoryReceiptPath 'Z:\other.json' -ProvenancePath 'Z:\third.json')
        if ($output.Count -ne 0) { throw 'Dot-source produced output.' }
    }
    . $toolPath -SbomPath $sbomFile -InventoryReceiptPath $inventoryFile -ProvenancePath $provenanceFile `
        -ExpectedSourceCommit $commit -ExpectedVersion $version

    Assert-SbomCase 'Current-profile declarations match without modifying three inputs' {
        Save-SbomFixture (New-SbomFixture)
        $before = @(Get-SbomInputSnapshot)
        $result = Invoke-SbomFixture
        if (-not $result.consistencyMatched -or $result.fileCount -ne 2 -or $result.totalBytes -ne 32 -or
            $result.libraryCount -ne 1 -or $result.sourceCommitDeclaration -cne $commit -or $result.versionDeclaration -cne $version) {
            throw 'Expected consistency receipt.'
        }
        foreach ($flag in @('provenanceAuthenticated', 'sourceAttested', 'fullCycloneDxValidated', 'dependencyCompletenessProven',
            'policyAuthorityVerified', 'publicFlavorProven', 'authenticodeVerified', 'licensingCleared', 'installerInclusionProven',
            'currentPackageBytesVerified', 'concurrentWriterConfinementProven', 'postValidationBytesPinned', 'publicReleaseApproved')) {
            if ($null -eq $result.$flag -or $result.$flag -ne $false) { throw "Untruthful proof flag: $flag." }
        }
        if (($before -join '|') -cne (@(Get-SbomInputSnapshot) -join '|')) { throw 'Inputs were modified.' }
    }
    Assert-SbomCase 'One-file zero-library explicit array succeeds' {
        $fixture = New-SbomFixture
        $fixture.Inventory.files = @($fixture.Inventory.files[0]); $fixture.Inventory.fileCount = 1; $fixture.Inventory.totalBytes = 20
        $fixture.Sbom.components = @($fixture.Sbom.components[0]); $fixture.Sbom.metadata.component.properties[2].value = '1'
        Save-SbomFixture $fixture
        $result = Invoke-SbomFixture
        if ($result.libraryCount -ne 0 -or $result.fileCount -ne 1) { throw 'Single explicit array was not respected.' }
    }
    Assert-SbomCase 'Case-insensitive Windows identities and uppercase hashes remain compatible' {
        $fixture = New-SbomFixture
        $fixture.Sbom.components[0].name = 'KEYBOARD.WTF.EXE'
        $fixture.Sbom.components[0].hashes[0].content = 'A' * 64
        $fixture.Provenance.sourceCommit = $commit.ToUpperInvariant()
        Save-SbomFixture $fixture
        if (-not (Invoke-SbomFixture).consistencyMatched) { throw 'Expected ordinary casing compatibility.' }
    }
    Assert-SbomCase 'Ordinary nonreserved spaces and Unicode names are allowed' {
        $fixture = New-SbomFixture
        $fixture.Sbom.components[0].name = 'release notes/文档.bin'; $fixture.Inventory.files[0].path = 'release notes/文档.bin'
        Save-SbomFixture $fixture
        Invoke-SbomFixture | Out-Null
    }
    Assert-SbomCase 'Three exact inputs may use an ordinary different drive-letter case' {
        Save-SbomFixture (New-SbomFixture)
        Test-PublicSbomProvenance -SbomPath $sbomFile.ToUpperInvariant() -InventoryReceiptPath $inventoryFile `
            -ProvenancePath $provenanceFile -ExpectedSourceCommit $commit -ExpectedVersion $version | Out-Null
    }

    foreach ($template in @('../bad.bin', '/absolute.bin', 'C:/absolute.bin', 'bad\file.bin', 'a//b.bin', 'a/./b.bin',
        'a/../b.bin', 'a/file.bin.', 'a/file.bin ', ' a/file.bin', 'a/file.bin:ads', 'a/*.bin', 'a/CON.bin', 'a/CON .txt',
        'a/COM1 .txt', 'a/LPT1 .dat', 'a/CONIN$ .txt', 'a/COM¹.txt', "a/control`nbin", ('a/' * 16 + 'leaf.bin'))) {
        foreach ($role in @('sbom', 'inventory')) {
            Assert-SbomMutation "Unsafe relative identity $role $template" {
                param($f)
                if ($role -ceq 'sbom') { $f.Sbom.components[0].name = $template }
                else { $f.Inventory.files[0].path = $template }
            } $(if ($template -cne $template.Trim() -or $template.Contains("`n")) {
                'sbom_provenance_schema_invalid'
            } else { 'sbom_provenance_path_invalid' })
        }
    }
    foreach ($path in @('Z:\missing.json', ($sbomFile + ':ads'), ($sbomFile + '.'), ($sbomFile + ' '),
        $sbomFile.Replace('\', '/'), (Join-Path $fixtureRoot '.\sbom.json'), (Join-Path $fixtureRoot '..\sbom.json'),
        (Join-Path $fixtureRoot '*.json'), ('\\?\' + $sbomFile), '\\example.test\share\sbom.json', 'relative.json')) {
        Assert-SbomCase 'Input alias or unknown exact JSON path is rejected' {
            Save-SbomFixture (New-SbomFixture)
            Assert-SbomRejection 'sbom_provenance_input_invalid' {
                Test-PublicSbomProvenance -SbomPath $path -InventoryReceiptPath $inventoryFile -ProvenancePath $provenanceFile `
                    -ExpectedSourceCommit $commit -ExpectedVersion $version
            }
        }
    }
    Assert-SbomCase 'Repeated input identity is not accepted as distinct evidence' {
        Save-SbomFixture (New-SbomFixture)
        Assert-SbomRejection 'sbom_provenance_input_invalid' {
            Test-PublicSbomProvenance -SbomPath $sbomFile -InventoryReceiptPath $sbomFile -ProvenancePath $provenanceFile `
                -ExpectedSourceCommit $commit -ExpectedVersion $version
        }
    }

    foreach ($pair in @(
        @('bomFormat', 'CycloneDX'), @('specVersion', '1.5'), @('timestamp', '2026-10-04T12:34:56.1234567+00:00'),
        @('type', 'file'), @('name', 'keyboard.wtf SBOM generator'), @('name', 'keyboard.wtf'),
        @('name', 'keyboard.wtf:source-commit'), @('alg', 'SHA-256'), @('name', 'keyboard.wtf:file-size'),
        @('group', 'nuget'), @('name', 'Synthetic.Library'))) {
        Assert-SbomCase 'Duplicate SBOM object field is rejected rather than last-wins' {
            Save-SbomFixture (New-SbomFixture)
            $text = [IO.File]::ReadAllText($sbomFile)
            $needle = '"' + $pair[0] + '":"' + $pair[1] + '"'
            if (-not $text.Contains($needle)) { throw 'Duplicate mutation needle missing.' }
            [IO.File]::WriteAllText($sbomFile, $text.Replace($needle, $needle + ',' + $needle), [Text.UTF8Encoding]::new($false))
            Assert-SbomRejection 'sbom_provenance_schema_invalid'
        }
    }
    foreach ($role in @('sbom', 'inventory', 'provenance')) {
        foreach ($variant in @('duplicate', 'unknown', 'wrong-case', 'comment', 'trailing-comma', 'array-root', 'deep')) {
            Assert-SbomCase "Strict JSON $role $variant" {
                Save-SbomFixture (New-SbomFixture)
                $path = switch ($role) { 'sbom' { $sbomFile } 'inventory' { $inventoryFile } 'provenance' { $provenanceFile } }
                $text = [IO.File]::ReadAllText($path)
                $firstField = switch ($role) { 'sbom' { '"bomFormat":"CycloneDX"' }
                    'inventory' { '"policyInventoryMatched":true' } 'provenance' { '"sourceCommit":"' + $commit + '"' } }
                $changed = switch ($variant) {
                    'duplicate' { $text.Replace($firstField, $firstField + ',' + $firstField) }
                    'unknown' { '{"sensitive-fixture-value":false,' + $text.Substring(1) }
                    'wrong-case' { $text.Replace($firstField, $firstField.Replace($firstField.Substring(1, 1), $firstField.Substring(1, 1).ToUpperInvariant())) }
                    'comment' { '/*not permitted*/' + $text }
                    'trailing-comma' { $text.Substring(0, $text.Length - 1) + ',}' }
                    'array-root' { '[' + $text + ']' }
                    'deep' { '{"unexpected":' + ('[' * 9) + '0' + (']' * 9) + '}' }
                }
                [IO.File]::WriteAllText($path, $changed, [Text.UTF8Encoding]::new($false))
                Assert-SbomRejection 'sbom_provenance_schema_invalid'
            }
        }
    }

    $mutations = @(
        @('Metadata unknown field', { param($f) $f.Sbom.metadata['extra'] = $true }),
        @('Tools unknown field', { param($f) $f.Sbom.metadata.tools['extra'] = $true }),
        @('Application unknown field', { param($f) $f.Sbom.metadata.component['extra'] = $true }),
        @('Tool unknown field', { param($f) $f.Sbom.metadata.tools.components[0]['extra'] = $true }),
        @('Hash unknown field', { param($f) $f.Sbom.components[0].hashes[0]['extra'] = $true }),
        @('Property unknown field', { param($f) $f.Sbom.components[0].properties[0]['extra'] = $true }),
        @('Library unknown field', { param($f) $f.Sbom.components[2]['extra'] = $true }),
        @('Inventory file unknown field', { param($f) $f.Inventory.files[0]['extra'] = $true }),
        @('Root wrong spec', { param($f) $f.Sbom.specVersion = '1.6' }),
        @('Serial not a UUID', { param($f) $f.Sbom.serialNumber = 'urn:uuid:garbage' }),
        @('Root version string', { param($f) $f.Sbom.version = '1' }),
        @('Root version fractional', { param($f) $f.Sbom.version = 1.5 }),
        @('Components not array', { param($f) $f.Sbom.components = $f.Sbom.components[0] }),
        @('File hashes not array', { param($f) $f.Sbom.components[0].hashes = $f.Sbom.components[0].hashes[0] }),
        @('File size not string', { param($f) $f.Sbom.components[0].properties[0].value = 20 }),
        @('File size leading zero', { param($f) $f.Sbom.components[0].properties[0].value = '020' }),
        @('File size sign', { param($f) $f.Sbom.components[0].properties[0].value = '+20' }),
        @('File size overflow', { param($f) $f.Sbom.components[0].properties[0].value = '9223372036854775808' }),
        @('File size above resource cap', { param($f) $f.Sbom.components[0].properties[0].value = ([long](512MB) + 1).ToString() }),
        @('Inventory size not integer', { param($f) $f.Inventory.files[0].sizeBytes = '20' }),
        @('Inventory size fractional', { param($f) $f.Inventory.files[0].sizeBytes = 20.5 }),
        @('Inventory size negative', { param($f) $f.Inventory.files[0].sizeBytes = -1 }),
        @('Inventory size cap', { param($f) $f.Inventory.files[0].sizeBytes = [long](512MB) + 1 }),
        @('Inventory total cap', { param($f) $f.Inventory.totalBytes = [long](2GB) + 1 }),
        @('Receipt proof claimed', { param($f) $f.Inventory.publicReleaseApproved = $true }),
        @('Receipt match untrue', { param($f) $f.Inventory.policyInventoryMatched = $false }),
        @('Receipt proof string', { param($f) $f.Inventory.authenticodeVerified = 'false' }),
        @('Hash presence wrong type', { param($f) $f.Inventory.files[0].expectedHashPresent = 'false' }),
        @('Malformed hash', { param($f) $f.Sbom.components[0].hashes[0].content = 'g' * 64 }),
        @('Null inventory hash', { param($f) $f.Inventory.files[0].sha256 = $null }),
        @('Different hash algorithm', { param($f) $f.Sbom.components[0].hashes[0].alg = 'sha256' }),
        @('Duplicate file identity', { param($f) $f.Sbom.components += $f.Sbom.components[0] }),
        @('Case duplicate inventory', { param($f) $copy = [ordered]@{ path = 'KEYBOARD.WTF.EXE'; sizeBytes = 20; sha256 = 'a' * 64; expectedHashPresent = $false }; $f.Inventory.files += $copy }),
        @('Library duplicate identity', { param($f) $f.Sbom.components += New-SbomLibrary 'SYNTHETIC.LIBRARY' '1.2.3' }),
        @('Library wrong purl', { param($f) $f.Sbom.components[2].purl = 'pkg:nuget/Synthetic.Library@1.2.4' }),
        @('Unsupported component type', { param($f) $f.Sbom.components[2].type = 'framework' }),
        @('Too many tool components', { param($f) $f.Sbom.metadata.tools.components += $f.Sbom.metadata.tools.components[0] }),
        @('Duplicate metadata property', { param($f) $f.Sbom.metadata.component.properties[1].name = 'keyboard.wtf:source-commit' }),
        @('Provenance UTC offset', { param($f) $f.Provenance.buildDateUtc = '2026-10-04T12:35:00+01:00' }),
        @('Metadata UTC whitespace', { param($f) $f.Sbom.metadata.timestamp += "`n" }),
        @('Invalid calendar date', { param($f) $f.Sbom.metadata.timestamp = '2026-02-31T12:35:00Z' }),
        @('Wrong installer filename', { param($f) $f.Provenance.installer = 'keyboard.wtf-setup.exe' }),
        @('Malformed signing key ID', { param($f) $f.Provenance.signingKeyId = 'bad/key' }),
        @('Empty file list', { param($f) $f.Inventory.files = @() }),
        @('Too-long component name', { param($f) $f.Sbom.components[0].name = 'x' * 1025 })
    )
    foreach ($case in $mutations) { Assert-SbomMutation $case[0] $case[1] }
    foreach ($case in @(
        @('SBOM source mismatch', { param($f) $f.Sbom.metadata.component.properties[0].value = 'f' * 40 }),
        @('Provenance source mismatch', { param($f) $f.Provenance.sourceCommit = 'f' * 40 }),
        @('SBOM version mismatch', { param($f) $f.Sbom.metadata.component.version = '1.4.20' }),
        @('Provenance version mismatch', { param($f) $f.Provenance.version = '1.4.20' }),
        @('Target mismatch', { param($f) $f.Sbom.metadata.component.properties[1].value = 'win-arm64' })
    )) { Assert-SbomMutation $case[0] $case[1] 'sbom_provenance_declaration_mismatch' }
    foreach ($case in @(
        @('Missing SBOM file', { param($f) $f.Sbom.components = @($f.Sbom.components[0], $f.Sbom.components[2]) }),
        @('Extra SBOM file', { param($f) $f.Sbom.components += New-SbomFile 'unexpected.bin' 1 ('a' * 64) }),
        @('Different file identity', { param($f) $f.Sbom.components[0].name = 'not-current.bin' }),
        @('Hash mismatch', { param($f) $f.Sbom.components[0].hashes[0].content = 'f' * 64 }),
        @('Size mismatch', { param($f) $f.Sbom.components[0].properties[0].value = '21' }),
        @('Inventory count mismatch', { param($f) $f.Inventory.fileCount = 1 }),
        @('Inventory total mismatch', { param($f) $f.Inventory.totalBytes = 33 }),
        @('SBOM metadata count mismatch', { param($f) $f.Sbom.metadata.component.properties[2].value = '1' })
    )) { Assert-SbomMutation $case[0] $case[1] 'sbom_provenance_file_mismatch' }

    Assert-SbomCase 'Declared resource boundaries do not require real package bytes' {
        $fixture = New-SbomFixture
        $fixture.Inventory.files = @(); $fixture.Sbom.components = @(); $fixture.Inventory.fileCount = 4; $fixture.Inventory.totalBytes = 2GB
        $fixture.Sbom.metadata.component.properties[2].value = '4'
        for ($i = 0; $i -lt 4; $i++) {
            $name = "declared-$i.bin"
            $fixture.Inventory.files += [ordered]@{ path = $name; sizeBytes = 512MB; sha256 = 'a' * 64; expectedHashPresent = $false }
            $fixture.Sbom.components += New-SbomFile $name 512MB ('a' * 64)
        }
        Save-SbomFixture $fixture
        $result = Invoke-SbomFixture
        if ($result.totalBytes -ne 2GB -or $result.currentPackageBytesVerified -ne $false) { throw 'Resource limit/control failed.' }
        if ([IO.Directory]::GetFiles($fixtureRoot).Length -ne 3) { throw 'Component paths must never be materialized.' }
    }
    Assert-SbomMutation '2001 observed file entries are rejected' {
        param($f)
        $f.Inventory.files = @(for ($i = 0; $i -lt 2001; $i++) {
            [ordered]@{ path = "file-$i.bin"; sizeBytes = 1; sha256 = 'a' * 64; expectedHashPresent = $false }
        })
        $f.Inventory.fileCount = 2001; $f.Inventory.totalBytes = 2001
    }
    Assert-SbomMutation '2001 library entries are rejected' {
        param($f)
        $f.Sbom.components = @($f.Sbom.components[0], $f.Sbom.components[1]) +
            @(for ($i = 0; $i -lt 2001; $i++) { New-SbomLibrary "Synthetic$i" })
    }
    Assert-SbomMutation '4001 total components are rejected before element traversal' {
        param($f) $f.Sbom.components = @($f.Sbom.components[0]) * 4001
    }

    foreach ($pair in @(@($sbomFile, 2MB), @($inventoryFile, 1MB), @($provenanceFile, 16KB))) {
        foreach ($length in @(0, ($pair[1] + 1))) {
            Assert-SbomCase 'Bounded input bytes fail before parse' {
                Save-SbomFixture (New-SbomFixture)
                [IO.File]::WriteAllBytes($pair[0], [byte[]]::new($length))
                Assert-SbomRejection 'sbom_provenance_input_size_invalid'
            }
        }
        Assert-SbomCase 'Exact input byte ceiling permits JSON plus whitespace' {
            Save-SbomFixture (New-SbomFixture)
            $text = [IO.File]::ReadAllText($pair[0]); $padded = $text + (' ' * ($pair[1] - [Text.Encoding]::UTF8.GetByteCount($text)))
            [IO.File]::WriteAllText($pair[0], $padded, [Text.UTF8Encoding]::new($false))
            Invoke-SbomFixture | Out-Null
        }
    }
    foreach ($path in @($sbomFile, $inventoryFile, $provenanceFile)) {
        Assert-SbomCase 'Invalid UTF8 is a content-free failure' {
            Save-SbomFixture (New-SbomFixture)
            [IO.File]::WriteAllBytes($path, [byte[]]@(0xff, 0xff, 0xff))
            Assert-SbomRejection 'sbom_provenance_input_invalid'
        }
        Assert-SbomCase 'Existing writer prevents read-lock validation and failure releases earlier handles' {
            Save-SbomFixture (New-SbomFixture)
            $writer = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Write,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            try { Assert-SbomRejection 'sbom_provenance_input_invalid' }
            finally { $writer.Dispose() }
            foreach ($inputPath in @($sbomFile, $inventoryFile, $provenanceFile)) {
                $exclusive = [IO.FileStream]::new($inputPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $exclusive.Dispose()
            }
            Invoke-SbomFixture | Out-Null
        }
        Assert-SbomCase 'UTF8 BOM is accepted consistently with existing generator/tooling' {
            Save-SbomFixture (New-SbomFixture)
            $bytes = [IO.File]::ReadAllBytes($path)
            [IO.File]::WriteAllBytes($path, [byte[]](@(0xef, 0xbb, 0xbf) + $bytes))
            Invoke-SbomFixture | Out-Null
        }
    }
    foreach ($bad in @('', ('f' * 39), ('f' * 41), (('f' * 40) + "`n"))) {
        Assert-SbomCase 'Expected commit input is exact and bounded' {
            Save-SbomFixture (New-SbomFixture)
            Assert-SbomRejection 'sbom_provenance_expected_invalid' {
                Test-PublicSbomProvenance $sbomFile $inventoryFile $provenanceFile $bad $version
            }
        }
    }
    foreach ($bad in @('', '1.4', '01.4.19', '1.4.19.0', '1.4.19-beta', "1.4.19`n", '2147483648.4.19')) {
        Assert-SbomCase 'Expected version input is canonical three-part version' {
            Save-SbomFixture (New-SbomFixture)
            Assert-SbomRejection 'sbom_provenance_expected_invalid' {
                Test-PublicSbomProvenance $sbomFile $inventoryFile $provenanceFile $commit $bad
            }
        }
    }

    Assert-SbomCase 'Deterministic receipt and unchanged input state across fifty local cycles' {
        Save-SbomFixture (New-SbomFixture)
        $before = @(Get-SbomInputSnapshot) -join '|'
        $receipt = (Invoke-SbomFixture | ConvertTo-Json -Depth 6 -Compress)
        for ($i = 0; $i -lt 50; $i++) {
            if ((Invoke-SbomFixture | ConvertTo-Json -Depth 6 -Compress) -cne $receipt) { throw 'Receipt drift.' }
        }
        if ((@(Get-SbomInputSnapshot) -join '|') -cne $before) { throw 'Inputs changed during repeated validation.' }
    }
    Assert-SbomCase 'Actual existing inventory receipt matches the current generator profile on an owned inert tree' {
        $package = Join-Path $fixtureRoot 'package'
        $policy = Join-Path $fixtureRoot 'inventory-policy.json'
        [IO.Directory]::CreateDirectory((Join-Path $package 'browser-helper')) | Out-Null
        $plainFile = Join-Path $package 'readme.txt'
        $helperFile = Join-Path $package 'browser-helper\manifest.json'
        [IO.File]::WriteAllText($plainFile, 'inert package observation')
        [IO.File]::WriteAllText($helperFile, 'inert helper observation')
        $policyValue = [ordered]@{ schemaVersion = 1; files = @(
            [ordered]@{ path = 'readme.txt'; required = $true; minBytes = 1; maxBytes = 128; sha256 = (Get-FileHash $plainFile).Hash },
            [ordered]@{ path = 'browser-helper/manifest.json'; required = $true; minBytes = 1; maxBytes = 128; sha256 = (Get-FileHash $helperFile).Hash }
        ) }
        Write-SbomJson $policy $policyValue
        . (Join-Path $PSScriptRoot 'test-public-package-inventory.ps1') -PackageDirectory $package -PolicyPath $policy
        $beforeTree = @($plainFile, $helperFile, $policy) | ForEach-Object { (Get-FileHash $_).Hash + ':' + [IO.FileInfo]::new($_).Length }
        $observed = Test-PublicPackageInventory -PackageDirectory $package -PolicyPath $policy
        $fixture = New-SbomFixture
        $fixture.Inventory = $observed
        $fixture.Sbom.components = @($observed.files | ForEach-Object { New-SbomFile $_.path $_.sizeBytes $_.sha256 })
        $fixture.Sbom.components += New-SbomLibrary
        $fixture.Sbom.metadata.component.properties[2].value = $observed.fileCount.ToString()
        Save-SbomFixture $fixture
        $beforeJson = @(Get-SbomInputSnapshot) -join '|'
        $receipt = Invoke-SbomFixture
        $afterTree = @($plainFile, $helperFile, $policy) | ForEach-Object { (Get-FileHash $_).Hash + ':' + [IO.FileInfo]::new($_).Length }
        if ($receipt.fileCount -ne $observed.fileCount -or $receipt.totalBytes -ne $observed.totalBytes -or
            ($beforeTree -join '|') -cne ($afterTree -join '|') -or (@(Get-SbomInputSnapshot) -join '|') -cne $beforeJson) {
            throw 'Observed inventory compatibility/input preservation failed.'
        }
    }
    Assert-SbomCase 'Real owned junction ancestor is rejected without following it and cleanup removes only the link' {
        Save-SbomFixture (New-SbomFixture)
        $target = Join-Path $fixtureRoot 'links-target'
        $junction = Join-Path $fixtureRoot 'json-link'
        [IO.Directory]::CreateDirectory($target) | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $target 'sbom.json'), [IO.File]::ReadAllBytes($sbomFile))
        $before = (Get-FileHash (Join-Path $target 'sbom.json')).Hash
        & $env:ComSpec /d /c mklink /J $junction $target | Out-Null
        if ($LASTEXITCODE -ne 0 -or ([IO.File]::GetAttributes($junction) -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw 'Real junction control was not established; no silent skip.'
        }
        Assert-SbomRejection 'sbom_provenance_input_invalid' {
            Test-PublicSbomProvenance -SbomPath (Join-Path $junction 'sbom.json') -InventoryReceiptPath $inventoryFile `
                -ProvenancePath $provenanceFile -ExpectedSourceCommit $commit -ExpectedVersion $version
        }
        if ((Get-FileHash (Join-Path $target 'sbom.json')).Hash -cne $before) { throw 'Junction target changed.' }
    }
    Assert-SbomCase 'CLI failure returns only stable code without paths/content' {
        Save-SbomFixture (New-SbomFixture)
        $result = @(& (Get-Process -Id $PID).Path -NoProfile -File $toolPath -SbomPath $sbomFile `
            -InventoryReceiptPath $inventoryFile -ProvenancePath $provenanceFile -ExpectedSourceCommit $commit `
            -ExpectedVersion 'invalid' 2>&1)
        if ($LASTEXITCODE -ne 1 -or ($result -join '') -cne 'sbom_provenance_expected_invalid') { throw 'Unexpected CLI failure contract.' }
    }
    Assert-SbomCase 'Actual Windows PowerShell 5.1 dot-source and invocation refuse before file/path validation' {
        $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (-not [IO.File]::Exists($windowsPowerShell)) { throw 'Required Windows PowerShell control unavailable.' }
        $safeTool = $toolPath.Replace("'", "''")
        $command = '$ErrorActionPreference=''Stop''; $o=@(. ''' + $safeTool + ''' -SbomPath ''not-a-path''); ' +
            'if($o.Count -ne 0){exit 2}; try{Test-PublicSbomProvenance ''not-a-path'' ''not-a-path'' ''not-a-path'' ''bad'' ''bad''|Out-Null;exit 3} ' +
            'catch{[Console]::WriteLine($_.Exception.Message);if($_.Exception.Message -ceq ''sbom_provenance_runtime_unsupported''){exit 0}else{exit 4}}'
        $result = @(& $windowsPowerShell -NoProfile -NonInteractive -Command $command 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($result -join '') -cne 'sbom_provenance_runtime_unsupported') { throw 'Unsupported-runtime/dot-source control failed.' }
    }
    Write-Output "Synthetic SBOM/provenance consistency checks passed: $passed."
}
finally { Remove-SbomFixture }
