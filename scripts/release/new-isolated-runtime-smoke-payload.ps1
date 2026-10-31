# Data-only CI tooling. Dot-sourcing defines a function; it executes no app.
function New-IsolatedRuntimeSmokePayload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SourceDirectory)
    if ($PSVersionTable.PSEdition -cne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7 -or
        [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'smoke_copy_runtime_unsupported' }
    $source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
    if (-not [IO.Directory]::Exists($source) -or $source.Length -le 3) { throw 'smoke_copy_source_invalid' }
    # Observed ancestor checks, NOT race-free namespace confinement.
    for ($ancestor = $source; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'smoke_copy_reparse' }
    }
    $files = [Collections.Generic.List[object]]::new()
    $queue = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue.Enqueue($source)
    $entries = 0
    $total = 0L
    while ($queue.Count -gt 0) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($queue.Dequeue())) {
            if (++$entries -gt 4096) { throw 'smoke_copy_tree_limit' }
            $relative = [IO.Path]::GetRelativePath($source, $entry)
            $segments = $relative.Split('\')
            if ($segments.Count -gt 16 -or -not $seen.Add($relative)) { throw 'smoke_copy_identity_invalid' }
            foreach ($segment in $segments) {
                if ($segment -in @('.keyboard.wtf-update-reservation', '.keyboard.wtf-update-activity')) { throw 'smoke_copy_reserved_marker' }
                if ($segment -in @('.', '..') -or $segment -match '[<>:"/|?*\x00-\x1f]' -or
                    $segment -match '[. ]$' -or $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]) *(\.|$)') { throw 'smoke_copy_identity_invalid' }
            }
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'smoke_copy_reparse' }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $queue.Enqueue($entry); continue }
            $length = [IO.FileInfo]::new($entry).Length
            $total += $length
            if ($length -gt 512MB -or $total -gt 2GB -or $files.Count -ge 2000) { throw 'smoke_copy_size_limit' }
            $files.Add([pscustomobject]@{Path=$entry;Relative=$relative;Length=$length})
        }
    }
    if (-not ($files.Relative -ccontains 'keyboard.wtf.exe')) { throw 'smoke_copy_executable_missing' }
    $copy = Join-Path ([IO.Path]::GetTempPath()) ('kwt-runtime-smoke-copy-' + [Guid]::NewGuid().ToString('N'))
    if ([IO.Directory]::Exists($copy) -or [IO.File]::Exists($copy)) { throw 'smoke_copy_destination_exists' }
    [IO.Directory]::CreateDirectory($copy) | Out-Null
    # Partial/complete copies are deliberately retained on failure/success.
    # Source read locks protect each copied file, NOT the entire tree through CI.
    foreach ($file in $files) {
        $input = $null
        $output = $null
        try {
            if (([IO.File]::GetAttributes($file.Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'smoke_copy_reparse' }
            $input = [IO.FileStream]::new($file.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            if ($input.Length -ne $file.Length) { throw 'smoke_copy_source_changed' }
            $destination = Join-Path $copy $file.Relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            $output = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $input.CopyTo($output)
            if ($output.Length -ne $file.Length) { throw 'smoke_copy_length_mismatch' }
        }
        finally { if ($output) { $output.Dispose() }; if ($input) { $input.Dispose() } }
    }
    [pscustomobject]@{
        SourceDirectory=$source;CopyDirectory=$copy;Executable=(Join-Path $copy 'keyboard.wtf.exe')
        FileCount=$files.Count;TotalBytes=$total;PackageApproved=$false;SourceNamespacePinned=$false
    }
}
