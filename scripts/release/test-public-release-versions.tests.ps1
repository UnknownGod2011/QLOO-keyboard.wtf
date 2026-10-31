$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'test-public-release-versions.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('kwt-version-gate-' + [Guid]::NewGuid().ToString('N'))
$passed = 0

function Assert-VersionCase {
    param([string]$Name, [scriptblock]$Action)
    & $Action
    $script:passed++
}

function Write-VersionFixture {
    param(
        [string]$Project = '<Project><PropertyGroup><Version>1.4.20</Version><FileVersion>1.4.20.0</FileVersion><AssemblyVersion>1.4.20.0</AssemblyVersion></PropertyGroup></Project>',
        [string]$Installer = "#ifndef MyAppVersion`n#define MyAppVersion `"1.4.20`"`n#endif`n[Setup]`nAppVersion={#MyAppVersion}`nVersionInfoVersion={#MyAppVersion}`n"
    )
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'src\KeyboardWtf.csproj'), $Project)
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'installer\keyboard-wtf.iss'), $Installer)
}

function Assert-VersionRejection {
    param([scriptblock]$Action, [string]$Code)
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception }
    if ($null -eq $caught -or -not $caught.Message.StartsWith($Code, [StringComparison]::Ordinal)) {
        throw "Expected rejection code $Code."
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'src'), (Join-Path $fixtureRoot 'installer') -Force | Out-Null
    Assert-VersionCase 'Dot-source has no output or filesystem validation' {
        $result = @(. $scriptPath -RepositoryRoot (Join-Path $fixtureRoot 'does-not-exist'))
        if ($result.Count -ne 0) { throw 'Dot-source produced output.' }
    }
    # Dot-source at script scope so the callable function remains available.
    . $scriptPath
    Assert-VersionCase 'Canonical values and optional release metadata agree' {
        Write-VersionFixture
        $result = Test-PublicReleaseVersions $fixtureRoot -ExpectedVersion '1.4.20' -ExpectedTag 'v1.4.20'
        if (-not $result.passed -or $result.fileVersion -cne '1.4.20.0') { throw 'Valid fixture failed.' }
    }
    Assert-VersionCase 'Optional metadata may be omitted' {
        Write-VersionFixture
        if ((Test-PublicReleaseVersions $fixtureRoot).releaseTag -cne 'v1.4.20') { throw 'Missing derived tag.' }
    }
    Assert-VersionCase 'Direct invocation produces a verification receipt without changing inputs' {
        Write-VersionFixture
        $projectPath = Join-Path $fixtureRoot 'src\KeyboardWtf.csproj'
        $installerPath = Join-Path $fixtureRoot 'installer\keyboard-wtf.iss'
        $before = @((Get-FileHash -LiteralPath $projectPath).Hash, (Get-FileHash -LiteralPath $installerPath).Hash)
        $result = (& $scriptPath -RepositoryRoot $fixtureRoot -ExpectedTag 'v1.4.20' | ConvertFrom-Json)
        $after = @((Get-FileHash -LiteralPath $projectPath).Hash, (Get-FileHash -LiteralPath $installerPath).Hash)
        if (-not $result.passed -or $before[0] -cne $after[0] -or $before[1] -cne $after[1]) {
            throw 'Direct invocation changed an input or did not return a receipt.'
        }
    }
    Assert-VersionCase 'XML namespaces and ordinary formatting are supported' {
        Write-VersionFixture -Project '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><Version> 1.4.20 </Version><FileVersion>1.4.20.0</FileVersion><AssemblyVersion>1.4.20.0</AssemblyVersion></PropertyGroup></Project>'
        Test-PublicReleaseVersions $fixtureRoot | Out-Null
    }
    foreach ($property in @('Version', 'FileVersion', 'AssemblyVersion')) {
        Assert-VersionCase "Missing $property is refused" {
            Write-VersionFixture
            $path = Join-Path $fixtureRoot 'src\KeyboardWtf.csproj'
            [IO.File]::WriteAllText($path, ([IO.File]::ReadAllText($path) -replace "<$property>[^<]*</$property>", ''))
            Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_property_ambiguous'
        }
        Assert-VersionCase "Duplicate $property is refused" {
            Write-VersionFixture
            $path = Join-Path $fixtureRoot 'src\KeyboardWtf.csproj'
            [IO.File]::WriteAllText($path, ([IO.File]::ReadAllText($path).Replace('</PropertyGroup>', "<$property>1.4.20</$property></PropertyGroup>")))
            Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_property_ambiguous'
        }
    }
    foreach ($version in @('1.4', '1.4.20.0', '1.4.20-preview', '01.4.20', '2147483648.4.20', '')) {
        Assert-VersionCase "Invalid canonical version $version is refused" {
            Write-VersionFixture -Project "<Project><PropertyGroup><Version>$version</Version><FileVersion>1.4.20.0</FileVersion><AssemblyVersion>1.4.20.0</AssemblyVersion></PropertyGroup></Project>"
            Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_format_invalid'
        }
    }
    foreach ($property in @('FileVersion', 'AssemblyVersion')) {
        Assert-VersionCase "Mismatched $property is refused" {
            Write-VersionFixture
            $path = Join-Path $fixtureRoot 'src\KeyboardWtf.csproj'
            [IO.File]::WriteAllText($path, ([IO.File]::ReadAllText($path).Replace("<$property>1.4.20.0</$property>", "<$property>1.4.20.1</$property>")))
            Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_binary_mismatch'
        }
    }
    Assert-VersionCase 'Conditional version definition is refused' {
        Write-VersionFixture -Project '<Project><PropertyGroup Condition="true"><Version>1.4.20</Version><FileVersion>1.4.20.0</FileVersion><AssemblyVersion>1.4.20.0</AssemblyVersion></PropertyGroup></Project>'
        Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_property_ambiguous'
    }
    Assert-VersionCase 'Conditional individual version is refused' {
        Write-VersionFixture -Project '<Project><PropertyGroup><Version Condition="true">1.4.20</Version><FileVersion>1.4.20.0</FileVersion><AssemblyVersion>1.4.20.0</AssemblyVersion></PropertyGroup></Project>'
        Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_property_ambiguous'
    }
    Assert-VersionCase 'Malformed project is refused' {
        Write-VersionFixture -Project '<Project>'
        Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_project_invalid'
    }
    Assert-VersionCase 'DTD is refused rather than resolved' {
        Write-VersionFixture -Project '<!DOCTYPE Project [<!ENTITY version "1.4.20">]><Project><PropertyGroup><Version>&version;</Version></PropertyGroup></Project>'
        Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_project_invalid'
    }
    foreach ($definition in @('#define MyAppVersion "1.4.19"', '#define MyAppVersion GetVersion()', '#define OtherVersion "1.4.20"', "#define MyAppVersion `"1.4.20`"`n#define MyAppVersion `"1.4.20`"", "#define MyAppVersion`n `"1.4.20`"")) {
        Assert-VersionCase 'Invalid installer version definition is refused' {
            Write-VersionFixture -Installer "$definition`n[Setup]`nAppVersion={#MyAppVersion}`nVersionInfoVersion={#MyAppVersion}`n"
            $code = if ($definition -eq '#define MyAppVersion "1.4.19"') { 'release_version_installer_mismatch' } else { 'release_version_installer_ambiguous' }
            Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } $code
        }
    }
    foreach ($setting in @('AppVersion', 'VersionInfoVersion')) {
        Assert-VersionCase "Unbound $setting is refused" {
            Write-VersionFixture
            $path = Join-Path $fixtureRoot 'installer\keyboard-wtf.iss'
            [IO.File]::WriteAllText($path, ([IO.File]::ReadAllText($path).Replace("$setting={#MyAppVersion}", "$setting=1.4.19")))
            Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_installer_binding_invalid'
        }
    }
    Assert-VersionCase 'Wrong expected version is refused' {
        Write-VersionFixture
        Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot -ExpectedVersion '1.4.19' } 'release_version_expected_mismatch'
    }
    Assert-VersionCase 'Wrong expected tag is refused' {
        Write-VersionFixture
        Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot -ExpectedTag 'v1.4.19' } 'release_version_tag_mismatch'
    }
    Assert-VersionCase 'Missing source input is refused' {
        Assert-VersionRejection { Test-PublicReleaseVersions (Join-Path $fixtureRoot 'missing') } 'release_version_input_missing'
    }
    Assert-VersionCase 'Oversized source input is refused before XML parsing' {
        Write-VersionFixture -Project (' ' * ((1MB) + 1))
        Assert-VersionRejection { Test-PublicReleaseVersions $fixtureRoot } 'release_version_input_too_large'
    }
    # Execute ONLY the exact build-script prefix through its early version gate.
    # No publish, installer, external process or packaging cleanup is run.
    $sourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $buildText = [IO.File]::ReadAllText((Join-Path $sourceRoot 'scripts\build-windows-release.ps1'))
    $tokens = $null
    $parseErrors = $null
    $buildAst = [Management.Automation.Language.Parser]::ParseInput($buildText, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw 'Release-script syntax failed.' }
    $calls = @($buildAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.InvocationOperator -eq [Management.Automation.Language.TokenKind]::Ampersand -and
        $node.Extent.Text.Contains("'scripts\release\test-public-release-versions.ps1'")
    }, $true))
    if ($calls.Count -ne 1) { throw 'Expected exactly one version preflight.' }
    $gate = $calls[0].Parent
    while ($null -ne $gate -and $gate -isnot [Management.Automation.Language.IfStatementAst]) { $gate = $gate.Parent }
    if ($null -eq $gate -or $gate.Clauses[0].Item1.Extent.Text -cne '$PaidProductionRelease') {
        throw 'Version preflight must be conditional on canonical public release.'
    }
    $effectCommands = @($buildAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -in @('Remove-Item', 'New-Item', 'dotnet')
    }, $true))
    if ($effectCommands.Where({ $_.Extent.StartOffset -lt $gate.Extent.EndOffset }).Count -ne 0) {
        throw 'Preflight must precede packaging effects.'
    }
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'scripts\release') -Force | Out-Null
    $prefixPath = Join-Path $fixtureRoot 'scripts\preflight-prefix.ps1'
    [IO.File]::WriteAllText($prefixPath, $buildText.Substring(0, $gate.Extent.EndOffset) + "`n'prefix-complete'`n")
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'scripts\release\test-public-release-versions.ps1'), [IO.File]::ReadAllText($scriptPath))
    Assert-VersionCase 'Canonical build prefix accepts consistent source before any packaging effects' {
        Write-VersionFixture
        # Prefix-only inert input: this is not a real or approved inventory.
        if ((& $prefixPath -PaidProductionRelease -PublicInventoryPolicy 'fixture-policy-not-approved.json') -cne 'prefix-complete') { throw 'Preflight prefix failed.' }
    }
    Assert-VersionCase 'Canonical build prefix refuses inconsistent source before any packaging effects' {
        Write-VersionFixture -Installer "#define MyAppVersion `"1.4.19`"`nAppVersion={#MyAppVersion}`nVersionInfoVersion={#MyAppVersion}`n"
        Assert-VersionRejection { & $prefixPath -PaidProductionRelease -PublicInventoryPolicy 'fixture-policy-not-approved.json' } 'release_version_installer_mismatch'
    }
    Assert-VersionCase 'Private candidate prefix preserves its original version-check policy' {
        # The canonical public preflight does not alter private/legacy packaging.
        if ((& $prefixPath -PersonalApiCandidate) -cne 'prefix-complete') { throw 'Candidate prefix changed.' }
    }
    Assert-VersionCase 'Mutually exclusive flavor rejection still precedes canonical preflight' {
        Assert-VersionRejection { & $prefixPath -PaidProductionRelease -PersonalApiCandidate } 'Choose only one'
    }
    [pscustomobject][ordered]@{ passed = $passed; failed = 0; fixtureScope = 'synthetic-only' } | ConvertTo-Json
}
finally {
    if ([IO.Directory]::Exists($fixtureRoot)) {
        $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        $resolved = [IO.Path]::GetFullPath($fixtureRoot)
        if ([IO.Path]::GetDirectoryName($resolved) -cne $tempParent -or
            [IO.Path]::GetFileName($resolved) -notmatch '^kwt-version-gate-[a-f0-9]{32}$') {
            throw 'Refusing unexpected fixture cleanup path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
