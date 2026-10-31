param(
    [string]$PackageDirectory,
    [string]$PolicyPath
)

function Test-PublicPackageInventory {
    <#
    .SYNOPSIS
    Read-only comparison against an independently supplied exact-file policy.
    .DESCRIPTION
    PowerShell 7/Core on Windows is required. No policy is generated or approved,
    package is built, binary is executed, or input is modified. A passing result
    covers this read-locked observation only, not later packaging or publication.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][AllowNull()][string]$PackageDirectory,
        [AllowEmptyString()][AllowNull()][string]$PolicyPath
    )

    # Keep this before path resolution, filesystem access, or JSON type lookup.
    # The file can be dot-sourced by Windows PowerShell without loading JSON types.
    if ($PSVersionTable.PSEdition -cne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7 -or
        [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw [IO.InvalidDataException]::new('package_inventory_runtime_unsupported')
    }

    $maximumPolicyBytes = 1MB
    $maximumFiles = 2000
    $maximumTreeEntries = 4096
    $maximumFileBytes = 512MB
    $maximumTotalBytes = 2GB
    $document = $null
    $policyStream = $null
    $streams = [Collections.Generic.List[IO.FileStream]]::new()

    function Assert-InventoryObjectFields {
        param($Element, [string[]]$Fields)
        if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw [IO.InvalidDataException]::new('package_inventory_policy_invalid')
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if ($Fields -cnotcontains $property.Name -or -not $seen.Add($property.Name)) {
                throw [IO.InvalidDataException]::new('package_inventory_policy_invalid')
            }
        }
        if ($seen.Count -ne $Fields.Count) {
            throw [IO.InvalidDataException]::new('package_inventory_policy_invalid')
        }
    }

    function Assert-InventoryRelativePath {
        param([string]$Path)
        if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Length -gt 1024 -or
            $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.EndsWith('/')) {
            throw [IO.InvalidDataException]::new('package_inventory_path_invalid')
        }
        $segments = $Path.Split('/')
        if ($segments.Count -gt 16) {
            throw [IO.InvalidDataException]::new('package_inventory_path_invalid')
        }
        foreach ($segment in $segments) {
            if ([string]::IsNullOrEmpty($segment) -or $segment.Length -gt 255 -or
                $segment -cne $segment.Trim() -or $segment.EndsWith('.') -or
                $segment -in @('.', '..') -or
                [Text.RegularExpressions.Regex]::IsMatch($segment, '[<>:"\\|?*\x00-\x1f\x7f]') -or
                [Text.RegularExpressions.Regex]::IsMatch($segment,
                    '\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:[ ]*\.|\z)',
                    [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                throw [IO.InvalidDataException]::new('package_inventory_path_invalid')
            }
            # Installation protocol state is never a distributable artifact,
            # even if an independently supplied policy accidentally lists it.
            if ($segment -in @('.keyboard.wtf-update-reservation', '.keyboard.wtf-update-activity')) {
                throw [IO.InvalidDataException]::new('package_inventory_runtime_artifact')
            }
        }
    }

    function Assert-InventoryNoReparseAncestors {
        param([string]$Path, [bool]$File)
        $item = if ($File) { [IO.FileInfo]::new($Path) } else { [IO.DirectoryInfo]::new($Path) }
        if (-not $item.Exists) { throw [IO.InvalidDataException]::new('package_inventory_input_missing') }
        while ($null -ne $item) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [IO.InvalidDataException]::new('package_inventory_reparse_invalid')
            }
            $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
        }
    }

    function Get-InventoryHash {
        param([IO.Stream]$Stream)
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '').ToLowerInvariant()
        }
        finally { $sha.Dispose() }
    }

    try {
        if ([string]::IsNullOrWhiteSpace($PackageDirectory) -or [string]::IsNullOrWhiteSpace($PolicyPath)) {
            throw [IO.InvalidDataException]::new('package_inventory_input_invalid')
        }
        $root = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
        $policy = [IO.Path]::GetFullPath($PolicyPath)
        if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\', '/')) {
            throw [IO.InvalidDataException]::new('package_inventory_input_invalid')
        }
        Assert-InventoryNoReparseAncestors $root $false
        Assert-InventoryNoReparseAncestors $policy $true
        $policyStream = [IO.FileStream]::new($policy, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($policyStream.Length -le 0 -or $policyStream.Length -gt $maximumPolicyBytes) {
            throw [IO.InvalidDataException]::new('package_inventory_policy_size_invalid')
        }
        $memory = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(16384)
            while (($read = $policyStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                if ($memory.Length + $read -gt $maximumPolicyBytes) {
                    throw [IO.InvalidDataException]::new('package_inventory_policy_size_invalid')
                }
                $memory.Write($buffer, 0, $read)
            }
            $policyBytes = $memory.ToArray()
        }
        finally { $memory.Dispose() }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($policyBytes)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) { $text = $text.Substring(1) }
        $options = [System.Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 4
        $options.AllowTrailingCommas = $false
        $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
        try { $document = [System.Text.Json.JsonDocument]::Parse($text, $options) }
        catch { throw [IO.InvalidDataException]::new('package_inventory_policy_invalid') }
        $element = $document.RootElement
        Assert-InventoryObjectFields $element @('schemaVersion', 'files')
        $schema = $element.GetProperty('schemaVersion')
        $schemaVersion = 0
        if ($schema.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            -not $schema.TryGetInt32([ref]$schemaVersion) -or $schemaVersion -ne 1) {
            throw [IO.InvalidDataException]::new('package_inventory_policy_invalid')
        }
        $fileElements = $element.GetProperty('files')
        if ($fileElements.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or
            $fileElements.GetArrayLength() -le 0 -or $fileElements.GetArrayLength() -gt $maximumFiles) {
            throw [IO.InvalidDataException]::new('package_inventory_policy_invalid')
        }
        $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $allowedDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $fileElements.EnumerateArray()) {
            Assert-InventoryObjectFields $entry @('path', 'required', 'minBytes', 'maxBytes', 'sha256')
            $pathValue = $entry.GetProperty('path')
            $requiredValue = $entry.GetProperty('required')
            $minimum = $entry.GetProperty('minBytes')
            $maximum = $entry.GetProperty('maxBytes')
            $hashValue = $entry.GetProperty('sha256')
            $minimumBytes = 0L
            $maximumBytes = 0L
            if ($pathValue.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                $requiredValue.ValueKind -notin @([System.Text.Json.JsonValueKind]::True, [System.Text.Json.JsonValueKind]::False) -or
                $minimum.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                $maximum.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                -not $minimum.TryGetInt64([ref]$minimumBytes) -or -not $maximum.TryGetInt64([ref]$maximumBytes) -or
                $minimumBytes -lt 0 -or $maximumBytes -le 0 -or $maximumBytes -gt $maximumFileBytes -or
                $minimumBytes -gt $maximumBytes -or
                $hashValue.ValueKind -notin @([System.Text.Json.JsonValueKind]::String, [System.Text.Json.JsonValueKind]::Null)) {
                throw [IO.InvalidDataException]::new('package_inventory_policy_invalid')
            }
            $relative = $pathValue.GetString()
            Assert-InventoryRelativePath $relative
            $hash = if ($hashValue.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) { $null } else { $hashValue.GetString() }
            if ($null -ne $hash -and -not [Text.RegularExpressions.Regex]::IsMatch($hash, '\A[0-9a-fA-F]{64}\z')) {
                throw [IO.InvalidDataException]::new('package_inventory_policy_invalid')
            }
            if ($expected.ContainsKey($relative)) {
                throw [IO.InvalidDataException]::new('package_inventory_policy_invalid')
            }
            $expected.Add($relative, [pscustomobject]@{
                path = $relative; required = $requiredValue.GetBoolean()
                minBytes = $minimumBytes; maxBytes = $maximumBytes; sha256 = $hash
            })
            $separator = $relative.LastIndexOf('/')
            while ($separator -ge 0) {
                $parent = $relative.Substring(0, $separator)
                [void]$allowedDirectories.Add($parent)
                $separator = $parent.LastIndexOf('/')
            }
        }
        # Validate the entire exact set BEFORE opening any package file.
        # Empty directories are allowed only as parents of explicitly listed files.
        $found = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
        $pending.Push([IO.DirectoryInfo]::new($root))
        $treeEntries = 0
        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()
            if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [IO.InvalidDataException]::new('package_inventory_reparse_invalid')
            }
            foreach ($item in $directory.EnumerateFileSystemInfos()) {
                $treeEntries++
                if ($treeEntries -gt $maximumTreeEntries) {
                    throw [IO.InvalidDataException]::new('package_inventory_tree_limit')
                }
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw [IO.InvalidDataException]::new('package_inventory_reparse_invalid')
                }
                $relative = $item.FullName.Substring($root.Length + 1).Replace('\', '/')
                Assert-InventoryRelativePath $relative
                if ($item -is [IO.DirectoryInfo]) {
                    if (-not $allowedDirectories.Contains($relative)) {
                        throw [IO.InvalidDataException]::new('package_inventory_extra_entry')
                    }
                    $pending.Push($item)
                }
                else {
                    if (-not $expected.ContainsKey($relative) -or $found.ContainsKey($relative)) {
                        throw [IO.InvalidDataException]::new('package_inventory_extra_entry')
                    }
                    $found.Add($relative, $item)
                    if ($found.Count -gt $maximumFiles) {
                        throw [IO.InvalidDataException]::new('package_inventory_tree_limit')
                    }
                }
            }
        }
        foreach ($entry in $expected.Values) {
            if ($entry.required -and -not $found.ContainsKey($entry.path)) {
                throw [IO.InvalidDataException]::new('package_inventory_required_missing')
            }
        }
        if ($found.Count -eq 0) { throw [IO.InvalidDataException]::new('package_inventory_empty') }
        $paths = [Collections.Generic.List[string]]::new()
        foreach ($path in $found.Keys) { $paths.Add($path) }
        $paths.Sort([StringComparer]::Ordinal)
        $observed = [Collections.Generic.List[object]]::new()
        $totalBytes = 0L
        foreach ($relative in $paths) {
            $stream = [IO.FileStream]::new($found[$relative].FullName, [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::Read, 65536, [IO.FileOptions]::SequentialScan)
            $streams.Add($stream)
            $entry = $expected[$relative]
            if ($stream.Length -lt $entry.minBytes -or $stream.Length -gt $entry.maxBytes) {
                throw [IO.InvalidDataException]::new('package_inventory_file_size_invalid')
            }
            $totalBytes += $stream.Length
            if ($totalBytes -gt $maximumTotalBytes) {
                throw [IO.InvalidDataException]::new('package_inventory_total_size_invalid')
            }
            $hash = Get-InventoryHash $stream
            if ($null -ne $entry.sha256 -and $hash -cne $entry.sha256.ToLowerInvariant()) {
                throw [IO.InvalidDataException]::new('package_inventory_hash_mismatch')
            }
            $observed.Add([pscustomobject][ordered]@{
                path = $relative; sizeBytes = $stream.Length; sha256 = $hash
                expectedHashPresent = $null -ne $entry.sha256
            })
        }
        $policyStream.Position = 0
        [pscustomobject][ordered]@{
            policyInventoryMatched = $true
            policySha256 = Get-InventoryHash $policyStream
            fileCount = $observed.Count
            totalBytes = $totalBytes
            files = $observed.ToArray()
            policyAuthorityVerified = $false
            publicReleaseApproved = $false
            publicFlavorProven = $false
            authenticodeVerified = $false
            licensingCleared = $false
            installerInclusionProven = $false
            contentSecretFreeProven = $false
            concurrentWriterConfinementProven = $false
            postValidationBytesPinned = $false
            observationScope = 'read-locked bytes during this validation only; not pinned through packaging'
        }
    }
    catch {
        if ($_.Exception -is [IO.InvalidDataException] -and
            [Text.RegularExpressions.Regex]::IsMatch($_.Exception.Message, '\Apackage_inventory_[a-z_]+\z')) {
            throw
        }
        throw [IO.InvalidDataException]::new('package_inventory_input_invalid')
    }
    finally {
        foreach ($stream in $streams) { $stream.Dispose() }
        if ($null -ne $document) { $document.Dispose() }
        if ($null -ne $policyStream) { $policyStream.Dispose() }
    }
}

# Dot-sourcing exposes the function only, with no input validation or effects.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        Test-PublicPackageInventory -PackageDirectory $PackageDirectory -PolicyPath $PolicyPath |
            ConvertTo-Json -Depth 8
    }
    catch {
        $code = if ($_.Exception -is [IO.InvalidDataException] -and
            [Text.RegularExpressions.Regex]::IsMatch($_.Exception.Message, '\Apackage_inventory_[a-z_]+\z')) {
            $_.Exception.Message
        } else { 'package_inventory_input_invalid' }
        [Console]::Error.WriteLine($code)
        exit 1
    }
}
