param(
    [string]$Configuration = "Release",
    [switch]$SkipSiteHashUpdate,
    [switch]$SideBySideStaging,
    [switch]$PaidProductionCandidate,
    [switch]$PersonalApiCandidate,
    [switch]$PaidProductionRelease,
    [switch]$ConnectorStagingCandidate,
    [string]$PublicInventoryPolicy
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\KeyboardWtf.csproj"
if (@($SideBySideStaging, $PaidProductionCandidate, $PaidProductionRelease, $PersonalApiCandidate, $ConnectorStagingCandidate).Where({ $_ }).Count -gt 1) {
    throw "Choose only one paid staging, paid candidate, or paid production release mode."
}
if ($PaidProductionRelease) {
    # A policy is an independently reviewed input, never generated from whatever
    # the publish directory contains. Refuse BEFORE cleanup or build effects.
    if ([string]::IsNullOrWhiteSpace($PublicInventoryPolicy)) {
        throw [IO.InvalidDataException]::new('public_inventory_policy_required')
    }
    if ($PSVersionTable.PSEdition -cne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7 -or
        [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw [IO.InvalidDataException]::new('public_inventory_policy_invalid')
    }
    & (Join-Path $root 'scripts\release\test-public-release-versions.ps1') -RepositoryRoot $root | Out-Null
} elseif (-not [string]::IsNullOrWhiteSpace($PublicInventoryPolicy)) {
    throw [IO.InvalidDataException]::new('public_inventory_policy_nonpublic')
}
$isSideBySide = $SideBySideStaging -or $PaidProductionCandidate -or $PersonalApiCandidate -or $ConnectorStagingCandidate
$publishName = if ($PersonalApiCandidate) {
    "keyboard-wtf-personal-api-candidate-win-x64"
} elseif ($PaidProductionCandidate) {
    "keyboard-wtf-paid-production-candidate-win-x64"
} elseif ($SideBySideStaging) {
    "keyboard-wtf-paid-staging-win-x64"
} elseif ($ConnectorStagingCandidate) {
    "keyboard-wtf-connectors-staging-candidate-win-x64"
} else {
    "keyboard-wtf-win-x64"
}
$publishDir = Join-Path $root "dist\release\$publishName"
if ($PaidProductionRelease) {
    try {
        $publicInventoryPolicyPath = [IO.Path]::GetFullPath($PublicInventoryPolicy)
        $publicPolicyFile = Get-Item -LiteralPath $publicInventoryPolicyPath -Force
        if ($publicPolicyFile -isnot [IO.FileInfo] -or $publicPolicyFile.Length -le 0 -or
            $publicPolicyFile.Length -gt 1MB) {
            throw [IO.InvalidDataException]::new('public_inventory_policy_invalid')
        }
        $publicPolicyAncestor = $publicPolicyFile
        while ($null -ne $publicPolicyAncestor) {
            if (($publicPolicyAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [IO.InvalidDataException]::new('public_inventory_policy_invalid')
            }
            $publicPolicyAncestor = if ($publicPolicyAncestor -is [IO.FileInfo]) {
                $publicPolicyAncestor.Directory
            } else { $publicPolicyAncestor.Parent }
        }
        $publicInventoryPolicyPath = $publicPolicyFile.FullName
    }
    catch { throw [IO.InvalidDataException]::new('public_inventory_policy_invalid') }
    $publicPublishPath = [IO.Path]::GetFullPath($publishDir)
    if ($publicInventoryPolicyPath.Equals($publicPublishPath, [StringComparison]::OrdinalIgnoreCase) -or
        $publicInventoryPolicyPath.StartsWith($publicPublishPath + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw [IO.InvalidDataException]::new('public_inventory_policy_inside_publish')
    }
}
$installerScript = Join-Path $root "installer\keyboard-wtf.iss"
$installerDir = Join-Path $root "dist\installer"
$siteIndex = Join-Path $root "site\index.html"
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$iscc = $isccCandidates | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 was not found in a supported installation path."
}

$dirty = @(git -C $root status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) {
    throw "Could not verify the release source state."
}
if ($dirty.Count -ne 0) {
    throw "Release packaging requires a clean committed worktree."
}

if (Test-Path -LiteralPath $publishDir) {
    $releaseParent = [IO.Path]::GetFullPath((Join-Path $root 'dist\release'))
    $resolvedPublish = (Resolve-Path -LiteralPath $publishDir).Path
    if (-not $resolvedPublish.StartsWith($releaseParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish cleanup target escaped release directory."
    }
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

dotnet restore $project `
    --runtime win-x64 `
    --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "Locked win-x64 restore failed with exit code $LASTEXITCODE"
}

$publishArguments = @(
    "publish", $project,
    "--configuration", $Configuration,
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "--output", $publishDir,
    "-p:PublishSingleFile=true",
    "-p:PublishTrimmed=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)
if ($PersonalApiCandidate) {
    $publishArguments += "-p:KeyboardWtfPersonalApiCandidate=true"
} elseif ($PaidProductionCandidate) {
    $publishArguments += "-p:KeyboardWtfPaidProductionCandidate=true"
} elseif ($PaidProductionRelease) {
    $publishArguments += "-p:KeyboardWtfPaidProduction=true"
} elseif ($ConnectorStagingCandidate) {
    $publishArguments += "-p:KeyboardWtfConnectorStagingCandidate=true"
}
& dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if ($PaidProductionRelease) {
    . (Join-Path $root 'scripts\release\test-public-package-inventory.ps1')
    $publicInventoryReceipt = Test-PublicPackageInventory -PackageDirectory $publishDir -PolicyPath $publicInventoryPolicyPath
    if ($null -eq $publicInventoryReceipt -or
        $publicInventoryReceipt.policyInventoryMatched -isnot [bool] -or
        -not $publicInventoryReceipt.policyInventoryMatched) {
        throw [IO.InvalidDataException]::new('public_inventory_receipt_invalid')
    }
    Write-Host ('Public inventory matched: files={0}; bytes={1}; policy_sha256={2}' -f
        $publicInventoryReceipt.fileCount, $publicInventoryReceipt.totalBytes, $publicInventoryReceipt.policySha256)
    # This is observed contamination containment only. Validation releases its
    # read locks; approved policy authority, byte pinning through ISCC, signing,
    # legal clearance and actual installer inclusion remain separate gates.
}

$isccArguments = @()
if ($PersonalApiCandidate) {
    $isccArguments += @(
        '/DMyAppName=keyboard.wtf Personal API Candidate',
        '/DMyAppId={{84684681-8F17-4B43-9220-919485814D2F}',
        '/DMyDefaultDirName={localappdata}\Programs\keyboard.wtf Personal API Candidate',
        '/DMyOutputBaseFilename=keyboard-wtf-personal-api-candidate-setup',
        '/DMySourceDir=..\dist\release\keyboard-wtf-personal-api-candidate-win-x64',
        '/DMyRunValueName=keyboard.wtf Personal API Candidate',
        '/DMyDefaultGroupName=keyboard.wtf Personal API Candidate',
        '/DMyShortcutName=keyboard.wtf Personal API Candidate',
        '/DMyUserDataDir=keyboard.wtf-personal-api-candidate',
        '/DMySkipStartup',
        '/DMyPreserveNativeHostRegistration'
    )
}
if ($SideBySideStaging) {
    $isccArguments += @(
        '/DMyAppName=keyboard.wtf Paid Staging',
        '/DMyAppId={{C966E45B-AC9B-4192-AB9C-55D0CC33D9D4}',
        '/DMyDefaultDirName={localappdata}\Programs\keyboard.wtf Paid Staging',
        '/DMyOutputBaseFilename=keyboard-wtf-paid-staging-setup',
        '/DMySourceDir=..\dist\release\keyboard-wtf-paid-staging-win-x64',
        '/DMyRunValueName=keyboard.wtf Paid Staging',
        '/DMyDefaultGroupName=keyboard.wtf Paid Staging',
        '/DMyShortcutName=keyboard.wtf Paid Staging',
        '/DMyUserDataDir=keyboard.wtf-paid-staging',
        '/DMySkipStartup',
        '/DMyPreserveNativeHostRegistration'
    )
}
if ($PaidProductionCandidate) {
    $isccArguments += @(
        '/DMyAppName=keyboard.wtf Paid Production Candidate',
        '/DMyAppId={{C7905F92-81A1-4824-9E31-26A5E532B50F}',
        '/DMyDefaultDirName={localappdata}\Programs\keyboard.wtf Paid Production Candidate',
        '/DMyOutputBaseFilename=keyboard-wtf-paid-production-candidate-setup',
        '/DMySourceDir=..\dist\release\keyboard-wtf-paid-production-candidate-win-x64',
        '/DMyRunValueName=keyboard.wtf Paid Production Candidate',
        '/DMyDefaultGroupName=keyboard.wtf Paid Production Candidate',
        '/DMyShortcutName=keyboard.wtf Paid Production Candidate',
        '/DMyUserDataDir=keyboard.wtf-paid-production-candidate',
        '/DMySkipStartup',
        '/DMyPreserveNativeHostRegistration'
    )
}
if ($ConnectorStagingCandidate) {
    $isccArguments += @(
        '/DMyAppName=keyboard.wtf Connectors Staging Candidate',
        '/DMyAppId={{0F5D5DB4-9C8B-458F-8928-4F2275099417}',
        '/DMyDefaultDirName={localappdata}\Programs\keyboard.wtf Connectors Staging Candidate',
        '/DMyOutputBaseFilename=keyboard-wtf-connectors-staging-candidate-setup',
        '/DMySourceDir=..\dist\release\keyboard-wtf-connectors-staging-candidate-win-x64',
        '/DMyRunValueName=keyboard.wtf Connectors Staging Candidate',
        '/DMyDefaultGroupName=keyboard.wtf Connectors Staging Candidate',
        '/DMyShortcutName=keyboard.wtf Connectors Staging Candidate',
        '/DMyUserDataDir=keyboard.wtf-connectors-staging-candidate',
        '/DMySkipStartup',
        '/DMyPreserveNativeHostRegistration'
    )
}
& $iscc @isccArguments $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$installerName = if ($PersonalApiCandidate) {
    "keyboard-wtf-personal-api-candidate-setup.exe"
} elseif ($PaidProductionCandidate) {
    "keyboard-wtf-paid-production-candidate-setup.exe"
} elseif ($SideBySideStaging) {
    "keyboard-wtf-paid-staging-setup.exe"
} elseif ($ConnectorStagingCandidate) {
    "keyboard-wtf-connectors-staging-candidate-setup.exe"
} else {
    "keyboard-wtf-setup.exe"
}
$installer = Join-Path $installerDir $installerName
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
$sizeMb = [Math]::Round((Get-Item -LiteralPath $installer).Length / 1MB, 1)
# The legacy BYOK website still consumes this inline hash. Paid production
# releases use production/site/config.js plus the signed release manifest and
# must never rewrite the legacy surface as a packaging side effect.
if (-not $SkipSiteHashUpdate -and -not $isSideBySide -and -not $PaidProductionRelease) {
    $siteHtml = Get-Content -LiteralPath $siteIndex -Raw
    $updatedSiteHtml = $siteHtml -replace 'SHA256 [A-Fa-f0-9]{64}', "SHA256 $($hash.Hash)"
    if ($updatedSiteHtml -eq $siteHtml -and $siteHtml -notmatch "SHA256 $($hash.Hash)") {
        throw "Could not update the website installer hash"
    }
    [System.IO.File]::WriteAllText(
        $siteIndex,
        $updatedSiteHtml,
        [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Built: $installer"
Write-Host "Size: $sizeMb MB"
Write-Host "SHA256: $($hash.Hash)"
