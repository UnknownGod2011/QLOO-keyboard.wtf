[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
# Local reproducible inputs only. This script never calls a provider, cloud CLI,
# credentials, install or deployment. The root owner reviews/deploys separately.
$connectorRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $connectorRoot '.artifacts/connectors/deployments'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!$output.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Export only to a new directory under this connector checkout deployment artifacts.'
}
if (Test-Path -LiteralPath $output) { throw 'Existing exported source and receipts are protected.' }
Push-Location -LiteralPath $connectorRoot
try {
    $resolved = (& git rev-parse ($SourceCommit + '^{commit}')).Trim()
    if ($LASTEXITCODE -ne 0 -or $resolved -cne $SourceCommit) { throw 'The exact reviewed commit is unavailable.' }
    if ((& git rev-parse HEAD).Trim() -cne $SourceCommit -or (& git status --porcelain)) {
        throw 'Export only the reviewed clean connector checkpoint.'
    }
    $apiPath = 'staging/backend/KeyboardWtf.Staging.Api'
    $sharedPath = 'src/Services/Connectors/ConnectorEvidenceTraits.cs'
    $tree = (& git rev-parse ($SourceCommit + ':' + $apiPath)).Trim()
    $sharedBlob = (& git rev-parse ($SourceCommit + ':' + $sharedPath)).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The shared evidence source is missing from the reviewed commit.' }
    New-Item -ItemType Directory -Path $output | Out-Null
    $archive = Join-Path $output 'reviewed-source.zip'
    & git archive --format=zip ('--output=' + $archive) $SourceCommit $apiPath $sharedPath
    if ($LASTEXITCODE -ne 0) { throw 'The exact committed source export failed.' }
    $exportedTree = Join-Path $output 'committed-tree'
    Expand-Archive -LiteralPath $archive -DestinationPath $exportedTree
    $source = Join-Path $output 'source'
    New-Item -ItemType Directory -Path $source | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $exportedTree $apiPath) -Force |
        Copy-Item -Destination $source -Recurse
    $shared = Join-Path $exportedTree $sharedPath
    $packagedShared = Join-Path $source 'Connectors/ConnectorEvidenceTraits.cs'
    Copy-Item -LiteralPath $shared -Destination $packagedShared
    if ((Get-FileHash -LiteralPath $shared).Hash -cne (Get-FileHash -LiteralPath $packagedShared).Hash) {
        throw 'The generated shared source copy differs from the committed source.'
    }
    $manifest = @(Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($source.Length + 1).Replace('\', '/');
            bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    $proof = [ordered]@{ timeUtc = [DateTimeOffset]::UtcNow; sourceCommit = $SourceCommit; backendTree = $tree;
        sharedSourcePath = $sharedPath; sharedBlob = $sharedBlob; sharedSha256 = (Get-FileHash -LiteralPath $packagedShared).Hash;
        archiveSha256 = (Get-FileHash -LiteralPath $archive).Hash; sourceDirectory = $source;
        deploymentPerformed = $false; externalMutationPerformed = $false; files = $manifest }
    [IO.File]::WriteAllText((Join-Path $output 'source-proof.json'), ($proof | ConvertTo-Json -Depth 8))
    Write-Output ('Reviewed local backend inputs: ' + $source)
} finally { Pop-Location }
