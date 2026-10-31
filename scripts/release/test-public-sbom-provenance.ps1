param(
    [string]$SbomPath,
    [string]$InventoryReceiptPath,
    [string]$ProvenancePath,
    [string]$ExpectedSourceCommit,
    [string]$ExpectedVersion
)

function Test-PublicSbomProvenance {
    <#
    .SYNOPSIS
    Read-only consistency check for the CURRENT keyboard.wtf SBOM generator profile.
    .DESCRIPTION
    Reads only three supplied JSON files. It does not inspect a package directory,
    authenticate provenance, validate all CycloneDX schemas, build, sign or publish.
    An inventory receipt is prior unauthenticated evidence, not current package bytes.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][AllowNull()][string]$SbomPath,
        [AllowEmptyString()][AllowNull()][string]$InventoryReceiptPath,
        [AllowEmptyString()][AllowNull()][string]$ProvenancePath,
        [AllowEmptyString()][AllowNull()][string]$ExpectedSourceCommit,
        [AllowEmptyString()][AllowNull()][string]$ExpectedVersion
    )

    # Unsupported runtimes fail before paths/files or JSON types are accessed.
    # Dot-sourcing on Windows PowerShell 5.1 only defines this function.
    if ($PSVersionTable.PSEdition -cne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7 -or
        [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw [IO.InvalidDataException]::new('sbom_provenance_runtime_unsupported')
    }

    $maximumFiles = 2000
    $maximumLibraries = 2000
    $maximumFileBytes = 512MB
    $maximumTotalBytes = 2GB
    $streams = [Collections.Generic.List[IO.FileStream]]::new()
    $documents = [Collections.Generic.List[object]]::new()

    function Assert-SbomFields {
        param($Element, [string[]]$Fields)
        if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if ($Fields -cnotcontains $property.Name -or -not $seen.Add($property.Name)) {
                throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
            }
        }
        if ($seen.Count -ne $Fields.Count) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
    }

    function Get-SbomString {
        param($Element, [int]$Maximum = 1024)
        if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        $value = $Element.GetString()
        if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -gt $Maximum -or
            $value -cne $value.Trim() -or [Text.RegularExpressions.Regex]::IsMatch($value, '[\x00-\x1f\x7f]')) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        return $value
    }

    function Get-SbomInteger {
        param($Element, [long]$Minimum = 0, [long]$Maximum = [long]::MaxValue)
        $value = 0L
        if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            -not $Element.TryGetInt64([ref]$value) -or $value -lt $Minimum -or $value -gt $Maximum) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        return $value
    }

    function Get-SbomDecimalString {
        param($Element, [long]$Maximum)
        $text = Get-SbomString $Element 20
        $value = 0L
        if (-not [Text.RegularExpressions.Regex]::IsMatch($text, '\A(?:0|[1-9][0-9]*)\z') -or
            -not [long]::TryParse($text, [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$value) -or $value -gt $Maximum) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        return $value
    }

    function Get-SbomHash {
        param($Element)
        $value = Get-SbomString $Element 64
        if (-not [Text.RegularExpressions.Regex]::IsMatch($value, '\A[0-9a-fA-F]{64}\z')) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        return $value.ToLowerInvariant()
    }

    function Assert-SbomBoolean {
        param($Element, [bool]$Expected)
        if ($Element.ValueKind -notin @([System.Text.Json.JsonValueKind]::True, [System.Text.Json.JsonValueKind]::False) -or
            $Element.GetBoolean() -ne $Expected) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
    }

    function Assert-SbomArray {
        param($Element, [int]$Minimum, [int]$Maximum)
        if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or
            $Element.GetArrayLength() -lt $Minimum -or $Element.GetArrayLength() -gt $Maximum) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
    }

    function Assert-SbomUtc {
        param($Element)
        $text = Get-SbomString $Element 40
        $date = [DateTimeOffset]::MinValue
        if (-not [Text.RegularExpressions.Regex]::IsMatch($text,
                '\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)\z') -or
            -not [DateTimeOffset]::TryParse($text, [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref]$date) -or $date.Offset -ne [TimeSpan]::Zero) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
    }

    function Assert-SbomRelativePath {
        param([string]$Path)
        if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Length -gt 1024 -or
            $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.EndsWith('/')) {
            throw [IO.InvalidDataException]::new('sbom_provenance_path_invalid')
        }
        $segments = $Path.Split('/')
        if ($segments.Count -gt 16) { throw [IO.InvalidDataException]::new('sbom_provenance_path_invalid') }
        foreach ($segment in $segments) {
            if ([string]::IsNullOrEmpty($segment) -or $segment.Length -gt 255 -or
                $segment -cne $segment.Trim() -or $segment.EndsWith('.') -or $segment -in @('.', '..') -or
                [Text.RegularExpressions.Regex]::IsMatch($segment, '[<>:"\\|?*\x00-\x1f\x7f]') -or
                [Text.RegularExpressions.Regex]::IsMatch($segment,
                    '\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:[ ]*\.|\z)',
                    [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                throw [IO.InvalidDataException]::new('sbom_provenance_path_invalid')
            }
        }
    }

    function Assert-SbomInputPath {
        param([string]$Path)
        if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Length -lt 4 -or $Path.Length -gt 32767 -or
            -not [Text.RegularExpressions.Regex]::IsMatch($Path, '\A[A-Za-z]:\\') -or
            $Path.Contains('/') -or $Path.Substring(2).Contains(':') -or
            [Text.RegularExpressions.Regex]::IsMatch($Path, '[*?"<>|\x00-\x1f\x7f]') -or
            -not $Path.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
            throw [IO.InvalidDataException]::new('sbom_provenance_input_invalid')
        }
        foreach ($segment in $Path.Substring(3).Split('\')) {
            if ([string]::IsNullOrEmpty($segment) -or $segment.Length -gt 255 -or
                $segment -cne $segment.Trim() -or $segment.EndsWith('.') -or $segment -in @('.', '..') -or
                [Text.RegularExpressions.Regex]::IsMatch($segment,
                    '\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:[ ]*\.|\z)',
                    [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                throw [IO.InvalidDataException]::new('sbom_provenance_input_invalid')
            }
        }
        if (-not [string]::Equals($Path, [IO.Path]::GetFullPath($Path), [StringComparison]::OrdinalIgnoreCase)) {
            throw [IO.InvalidDataException]::new('sbom_provenance_input_invalid')
        }
        $pathToCheck = $Path
        $leaf = $true
        while ($null -ne $pathToCheck) {
            $attributes = [IO.File]::GetAttributes($pathToCheck)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                ((($attributes -band [IO.FileAttributes]::Directory) -ne 0) -eq $leaf)) {
                throw [IO.InvalidDataException]::new('sbom_provenance_input_invalid')
            }
            $leaf = $false
            $pathToCheck = [IO.Path]::GetDirectoryName($pathToCheck)
        }
    }

    function Read-SbomDocument {
        param([string]$Path, [long]$Maximum)
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $streams.Add($stream)
        if ($stream.Length -le 0 -or $stream.Length -gt $Maximum) {
            throw [IO.InvalidDataException]::new('sbom_provenance_input_size_invalid')
        }
        $memory = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(16384)
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                if ($memory.Length + $read -gt $Maximum) {
                    throw [IO.InvalidDataException]::new('sbom_provenance_input_size_invalid')
                }
                $memory.Write($buffer, 0, $read)
            }
            $bytes = $memory.ToArray()
        }
        finally { $memory.Dispose() }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) { $text = $text.Substring(1) }
        $options = [System.Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 8
        $options.AllowTrailingCommas = $false
        $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
        try { $document = [System.Text.Json.JsonDocument]::Parse($text, $options) }
        catch { throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid') }
        $documents.Add($document)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        return [pscustomobject]@{ Element = $document.RootElement; Hash = $hash }
    }

    function Get-SbomNamedProperties {
        param($Element, [string[]]$Names)
        Assert-SbomArray $Element $Names.Count $Names.Count
        $values = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateArray()) {
            Assert-SbomFields $property @('name', 'value')
            $name = Get-SbomString ($property.GetProperty('name')) 80
            if ($Names -cnotcontains $name -or $values.ContainsKey($name)) {
                throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
            }
            $values.Add($name, $property.GetProperty('value'))
        }
        return ,$values
    }

    try {
        $parsedVersion = $null
        if (-not [Text.RegularExpressions.Regex]::IsMatch($ExpectedSourceCommit, '\A[0-9a-fA-F]{40}\z') -or
            -not [Text.RegularExpressions.Regex]::IsMatch($ExpectedVersion, '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z') -or
            -not [Version]::TryParse($ExpectedVersion, [ref]$parsedVersion)) {
            throw [IO.InvalidDataException]::new('sbom_provenance_expected_invalid')
        }
        $paths = @($SbomPath, $InventoryReceiptPath, $ProvenancePath)
        $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $paths) {
            Assert-SbomInputPath $path
            if (-not $identities.Add($path)) { throw [IO.InvalidDataException]::new('sbom_provenance_input_invalid') }
        }
        $sbomRead = Read-SbomDocument $SbomPath 2MB
        $inventoryRead = Read-SbomDocument $InventoryReceiptPath 1MB
        $provenanceRead = Read-SbomDocument $ProvenancePath 16KB
        $sbom = $sbomRead.Element
        $inventory = $inventoryRead.Element
        $provenance = $provenanceRead.Element

        $falseFlags = @('policyAuthorityVerified', 'publicReleaseApproved', 'publicFlavorProven',
            'authenticodeVerified', 'licensingCleared', 'installerInclusionProven', 'contentSecretFreeProven',
            'concurrentWriterConfinementProven', 'postValidationBytesPinned')
        Assert-SbomFields $inventory (@('policyInventoryMatched', 'policySha256', 'fileCount', 'totalBytes', 'files') +
            $falseFlags + @('observationScope'))
        Assert-SbomBoolean ($inventory.GetProperty('policyInventoryMatched')) $true
        foreach ($flag in $falseFlags) { Assert-SbomBoolean ($inventory.GetProperty($flag)) $false }
        $policyHash = Get-SbomHash ($inventory.GetProperty('policySha256'))
        if ((Get-SbomString ($inventory.GetProperty('observationScope'))) -cne
            'read-locked bytes during this validation only; not pinned through packaging') {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        $fileCount = Get-SbomInteger ($inventory.GetProperty('fileCount')) 1 $maximumFiles
        $totalBytes = Get-SbomInteger ($inventory.GetProperty('totalBytes')) 0 $maximumTotalBytes
        $entries = $inventory.GetProperty('files')
        Assert-SbomArray $entries 1 $maximumFiles
        $observed = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $computedTotal = 0L
        foreach ($entry in $entries.EnumerateArray()) {
            Assert-SbomFields $entry @('path', 'sizeBytes', 'sha256', 'expectedHashPresent')
            $path = Get-SbomString ($entry.GetProperty('path'))
            Assert-SbomRelativePath $path
            $size = Get-SbomInteger ($entry.GetProperty('sizeBytes')) 0 $maximumFileBytes
            $hash = Get-SbomHash ($entry.GetProperty('sha256'))
            if ($entry.GetProperty('expectedHashPresent').ValueKind -notin
                @([System.Text.Json.JsonValueKind]::True, [System.Text.Json.JsonValueKind]::False) -or $observed.ContainsKey($path)) {
                throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
            }
            $computedTotal += $size
            if ($computedTotal -gt $maximumTotalBytes) { throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid') }
            $observed.Add($path, [pscustomobject]@{ Size = $size; Hash = $hash })
        }
        if ($fileCount -ne $observed.Count -or $totalBytes -ne $computedTotal) {
            throw [IO.InvalidDataException]::new('sbom_provenance_file_mismatch')
        }

        Assert-SbomFields $provenance @('sourceCommit', 'version', 'buildDateUtc', 'installer', 'installerSha256', 'signingKeyId')
        $source = Get-SbomString ($provenance.GetProperty('sourceCommit')) 40
        $version = Get-SbomString ($provenance.GetProperty('version')) 32
        if (-not [Text.RegularExpressions.Regex]::IsMatch($source, '\A[0-9a-fA-F]{40}\z') -or
            $source.ToLowerInvariant() -cne $ExpectedSourceCommit.ToLowerInvariant() -or $version -cne $ExpectedVersion) {
            throw [IO.InvalidDataException]::new('sbom_provenance_declaration_mismatch')
        }
        Assert-SbomUtc ($provenance.GetProperty('buildDateUtc'))
        if ((Get-SbomString ($provenance.GetProperty('installer')) 64) -cne 'keyboard-wtf-setup.exe') {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        $installerHash = Get-SbomHash ($provenance.GetProperty('installerSha256'))
        $keyId = Get-SbomString ($provenance.GetProperty('signingKeyId')) 80
        if (-not [Text.RegularExpressions.Regex]::IsMatch($keyId, '\A[A-Za-z0-9._-]{1,80}\z')) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }

        Assert-SbomFields $sbom @('bomFormat', 'specVersion', 'serialNumber', 'version', 'metadata', 'components')
        if ((Get-SbomString ($sbom.GetProperty('bomFormat')) 16) -cne 'CycloneDX' -or
            (Get-SbomString ($sbom.GetProperty('specVersion')) 8) -cne '1.5' -or
            (Get-SbomInteger ($sbom.GetProperty('version')) 1 1) -ne 1) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        $serial = Get-SbomString ($sbom.GetProperty('serialNumber')) 45
        $serialGuid = [Guid]::Empty
        if (-not $serial.StartsWith('urn:uuid:', [StringComparison]::Ordinal) -or
            -not [Guid]::TryParseExact($serial.Substring(9), 'D', [ref]$serialGuid)) {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        $metadata = $sbom.GetProperty('metadata')
        Assert-SbomFields $metadata @('timestamp', 'tools', 'component')
        Assert-SbomUtc ($metadata.GetProperty('timestamp'))
        $tools = $metadata.GetProperty('tools')
        Assert-SbomFields $tools @('components')
        $toolEntries = $tools.GetProperty('components')
        Assert-SbomArray $toolEntries 1 1
        $tool = $toolEntries[0]
        Assert-SbomFields $tool @('type', 'name', 'version')
        if ((Get-SbomString ($tool.GetProperty('type')) 16) -cne 'application' -or
            (Get-SbomString ($tool.GetProperty('name')) 80) -cne 'keyboard.wtf SBOM generator' -or
            (Get-SbomString ($tool.GetProperty('version')) 16) -cne '1') {
            throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
        }
        $application = $metadata.GetProperty('component')
        Assert-SbomFields $application @('type', 'name', 'version', 'properties')
        if ((Get-SbomString ($application.GetProperty('type')) 16) -cne 'application' -or
            (Get-SbomString ($application.GetProperty('name')) 80) -cne 'keyboard.wtf' -or
            (Get-SbomString ($application.GetProperty('version')) 32) -cne $ExpectedVersion) {
            throw [IO.InvalidDataException]::new('sbom_provenance_declaration_mismatch')
        }
        $properties = Get-SbomNamedProperties ($application.GetProperty('properties')) `
            @('keyboard.wtf:source-commit', 'keyboard.wtf:target', 'keyboard.wtf:packaged-file-count')
        $sbomSource = Get-SbomString $properties['keyboard.wtf:source-commit'] 40
        if (-not [Text.RegularExpressions.Regex]::IsMatch($sbomSource, '\A[0-9a-fA-F]{40}\z') -or
            $sbomSource.ToLowerInvariant() -cne $source.ToLowerInvariant() -or
            (Get-SbomString $properties['keyboard.wtf:target'] 16) -cne 'win-x64') {
            throw [IO.InvalidDataException]::new('sbom_provenance_declaration_mismatch')
        }
        if ((Get-SbomDecimalString $properties['keyboard.wtf:packaged-file-count'] $maximumFiles) -ne $fileCount) {
            throw [IO.InvalidDataException]::new('sbom_provenance_file_mismatch')
        }

        $components = $sbom.GetProperty('components')
        Assert-SbomArray $components 1 ($maximumFiles + $maximumLibraries)
        $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $libraries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($component in $components.EnumerateArray()) {
            if ($component.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
            }
            $type = Get-SbomString ($component.GetProperty('type')) 16
            if ($type -ceq 'file') {
                Assert-SbomFields $component @('type', 'name', 'hashes', 'properties')
                $name = Get-SbomString ($component.GetProperty('name'))
                Assert-SbomRelativePath $name
                if ($files.ContainsKey($name) -or $files.Count -ge $maximumFiles) {
                    throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
                }
                $hashes = $component.GetProperty('hashes')
                Assert-SbomArray $hashes 1 1
                $hashEntry = $hashes[0]
                Assert-SbomFields $hashEntry @('alg', 'content')
                if ((Get-SbomString ($hashEntry.GetProperty('alg')) 16) -cne 'SHA-256') {
                    throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
                }
                $hash = Get-SbomHash ($hashEntry.GetProperty('content'))
                $fileProperties = Get-SbomNamedProperties ($component.GetProperty('properties')) @('keyboard.wtf:file-size')
                $size = Get-SbomDecimalString $fileProperties['keyboard.wtf:file-size'] $maximumFileBytes
                $files.Add($name, [pscustomobject]@{ Size = $size; Hash = $hash })
            }
            elseif ($type -ceq 'library') {
                Assert-SbomFields $component @('type', 'group', 'name', 'version', 'purl')
                $name = Get-SbomString ($component.GetProperty('name')) 255
                $libraryVersion = Get-SbomString ($component.GetProperty('version')) 255
                $purl = Get-SbomString ($component.GetProperty('purl')) 2048
                if ((Get-SbomString ($component.GetProperty('group')) 16) -cne 'nuget' -or
                    $purl -cne ('pkg:nuget/' + [Uri]::EscapeDataString($name) + '@' + [Uri]::EscapeDataString($libraryVersion)) -or
                    -not $libraries.Add($name + '@' + $libraryVersion) -or $libraries.Count -gt $maximumLibraries) {
                    throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid')
                }
            }
            else { throw [IO.InvalidDataException]::new('sbom_provenance_schema_invalid') }
        }
        if ($files.Count -ne $observed.Count) { throw [IO.InvalidDataException]::new('sbom_provenance_file_mismatch') }
        foreach ($name in $files.Keys) {
            if (-not $observed.ContainsKey($name) -or $files[$name].Size -ne $observed[$name].Size -or
                $files[$name].Hash -cne $observed[$name].Hash) {
                throw [IO.InvalidDataException]::new('sbom_provenance_file_mismatch')
            }
        }

        [pscustomobject][ordered]@{
            consistencyMatched = $true
            validatedProfile = 'keyboard-wtf-current-generator-CycloneDX-1.5'
            sourceCommitDeclaration = $source.ToLowerInvariant()
            versionDeclaration = $version
            fileCount = $fileCount
            totalBytes = $totalBytes
            libraryCount = $libraries.Count
            sbomSha256 = $sbomRead.Hash
            inventoryReceiptSha256 = $inventoryRead.Hash
            provenanceSha256 = $provenanceRead.Hash
            inventoryPolicySha256Declaration = $policyHash
            installerSha256Declaration = $installerHash
            signingKeyIdDeclaration = $keyId
            provenanceAuthenticated = $false
            sourceAttested = $false
            fullCycloneDxValidated = $false
            dependencyCompletenessProven = $false
            policyAuthorityVerified = $false
            publicFlavorProven = $false
            authenticodeVerified = $false
            licensingCleared = $false
            installerInclusionProven = $false
            currentPackageBytesVerified = $false
            concurrentWriterConfinementProven = $false
            postValidationBytesPinned = $false
            publicReleaseApproved = $false
            observationScope = 'three read-locked JSON inputs; prior inventory receipt is unauthenticated, not current package verification'
        }
    }
    catch {
        if ($_.Exception -is [IO.InvalidDataException] -and
            [Text.RegularExpressions.Regex]::IsMatch($_.Exception.Message, '\Asbom_provenance_[a-z_]+\z')) { throw }
        throw [IO.InvalidDataException]::new('sbom_provenance_input_invalid')
    }
    finally {
        foreach ($document in $documents) { $document.Dispose() }
        foreach ($stream in $streams) { $stream.Dispose() }
    }
}

# Dot-sourcing exposes the function without validation, IO or JSON type lookup.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        Test-PublicSbomProvenance -SbomPath $SbomPath -InventoryReceiptPath $InventoryReceiptPath `
            -ProvenancePath $ProvenancePath -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedVersion $ExpectedVersion |
            ConvertTo-Json -Depth 6
    }
    catch {
        $code = if ($_.Exception -is [IO.InvalidDataException] -and
            [Text.RegularExpressions.Regex]::IsMatch($_.Exception.Message, '\Asbom_provenance_[a-z_]+\z')) {
            $_.Exception.Message
        } else { 'sbom_provenance_input_invalid' }
        [Console]::Error.WriteLine($code)
        exit 1
    }
}
