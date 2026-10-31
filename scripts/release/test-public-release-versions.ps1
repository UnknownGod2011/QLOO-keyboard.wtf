param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$ExpectedVersion,
    [string]$ExpectedTag
)

function Test-PublicReleaseVersions {
    <#
    .SYNOPSIS
    Read-only consistency check for the canonical public source versions.
    .DESCRIPTION
    Does not evaluate MSBuild/ISPP, build, run the installer, inspect keys, or
    access Git/network state. ExpectedTag checks supplied release metadata,
    not whether that tag exists or points to the current commit.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [string]$ExpectedVersion,
        [string]$ExpectedTag
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $projectPath = Join-Path $root 'src\KeyboardWtf.csproj'
    $installerPath = Join-Path $root 'installer\keyboard-wtf.iss'
    foreach ($path in @($projectPath, $installerPath)) {
        if (-not [IO.File]::Exists($path)) {
            throw [IO.FileNotFoundException]::new('release_version_input_missing')
        }
        if ([IO.FileInfo]::new($path).Length -gt 1MB) {
            throw [IO.InvalidDataException]::new('release_version_input_too_large')
        }
    }

    $reader = $null
    $xml = [Xml.XmlDocument]::new()
    $xml.XmlResolver = $null
    try {
        $readerSettings = [Xml.XmlReaderSettings]::new()
        $readerSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $readerSettings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($projectPath, $readerSettings)
        $xml.Load($reader)
    }
    catch {
        throw [IO.InvalidDataException]::new('release_version_project_invalid')
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
    }

    $versions = @{}
    foreach ($name in @('Version', 'FileVersion', 'AssemblyVersion')) {
        $nodes = @($xml.SelectNodes(
            "/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$name']"))
        if ($nodes.Count -ne 1 -or
            $nodes[0].HasAttribute('Condition') -or
            $nodes[0].ParentNode.HasAttribute('Condition')) {
            throw [IO.InvalidDataException]::new("release_version_property_ambiguous:$name")
        }
        $versions[$name] = $nodes[0].InnerText.Trim()
    }

    $version = $versions.Version
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw [IO.InvalidDataException]::new('release_version_format_invalid')
    }
    $parsedVersion = $null
    if (-not [Version]::TryParse($version, [ref]$parsedVersion)) {
        throw [IO.InvalidDataException]::new('release_version_format_invalid')
    }
    $binaryVersion = "$version.0"
    if ($versions.FileVersion -cne $binaryVersion -or
        $versions.AssemblyVersion -cne $binaryVersion) {
        throw [IO.InvalidDataException]::new('release_version_binary_mismatch')
    }

    $installer = [IO.File]::ReadAllText($installerPath)
    $definitions = [Text.RegularExpressions.Regex]::Matches(
        $installer, '(?m)^[ \t]*#define[ \t]+MyAppVersion(?:[ \t]|\r?$)')
    $literalDefinitions = [Text.RegularExpressions.Regex]::Matches(
        $installer, '(?m)^[ \t]*#define[ \t]+MyAppVersion[ \t]+"([^"\r\n]+)"[ \t]*\r?$')
    if ($definitions.Count -ne 1 -or $literalDefinitions.Count -ne 1) {
        throw [IO.InvalidDataException]::new('release_version_installer_ambiguous')
    }
    $installerVersion = $literalDefinitions[0].Groups[1].Value
    if ($installerVersion -cne $version) {
        throw [IO.InvalidDataException]::new('release_version_installer_mismatch')
    }
    foreach ($setting in @('AppVersion', 'VersionInfoVersion')) {
        $bindings = [Text.RegularExpressions.Regex]::Matches(
            $installer, "(?m)^[ \t]*$setting[ \t]*=[ \t]*\{#MyAppVersion\}[ \t]*\r?$")
        $allBindings = [Text.RegularExpressions.Regex]::Matches(
            $installer, "(?m)^[ \t]*$setting[ \t]*=")
        if ($bindings.Count -ne 1 -or $allBindings.Count -ne 1) {
            throw [IO.InvalidDataException]::new("release_version_installer_binding_invalid:$setting")
        }
    }

    if (-not [string]::IsNullOrEmpty($ExpectedVersion) -and $ExpectedVersion -cne $version) {
        throw [IO.InvalidDataException]::new('release_version_expected_mismatch')
    }
    $tag = "v$version"
    if (-not [string]::IsNullOrEmpty($ExpectedTag) -and $ExpectedTag -cne $tag) {
        throw [IO.InvalidDataException]::new('release_version_tag_mismatch')
    }

    [pscustomobject][ordered]@{
        passed = $true
        version = $version
        fileVersion = $versions.FileVersion
        assemblyVersion = $versions.AssemblyVersion
        installerVersion = $installerVersion
        releaseTag = $tag
    }
}

# Dot-sourcing exposes only the function; it performs no validation or effects.
if ($MyInvocation.InvocationName -ne '.') {
    Test-PublicReleaseVersions -RepositoryRoot $RepositoryRoot `
        -ExpectedVersion $ExpectedVersion -ExpectedTag $ExpectedTag |
        ConvertTo-Json
}
