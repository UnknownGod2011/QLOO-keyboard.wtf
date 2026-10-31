param(
    [switch]$Verify,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if ($Verify -and $Apply) { throw 'Choose either -Verify or -Apply, not both.' }
$lumaProject = 'keyboard-wtf-prod'
$lumaDatabase = '(default)'
$lumaCollection = 'lumaAdmission'
$lumaField = 'expiresAt'

# This isolated operator never changes rules, wallets, other TTL policies,
# indexes, backup configuration, traffic or Luma's Enabled setting. No arguments
# is an offline plan; Verify is read-only. Apply is an explicit cloud mutation.
if (-not $Verify -and -not $Apply) {
    [pscustomobject]@{
        Mode = 'plan-only'
        Project = $lumaProject
        Database = $lumaDatabase
        CollectionGroup = $lumaCollection
        Field = $lumaField
        CloudChanged = $false
        ReleaseGate = 'Run -Verify and require ACTIVE before enabling public Voice.'
    } | ConvertTo-Json
    return
}

$lumaGcloud = Join-Path $env:LOCALAPPDATA 'Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd'
if (-not (Test-Path -LiteralPath $lumaGcloud)) { throw 'Google Cloud CLI was not found.' }

function Read-LumaTtl {
    $lumaRaw = & $lumaGcloud firestore fields ttls list "--project=$lumaProject" `
        "--database=$lumaDatabase" "--collection-group=$lumaCollection" --format=json
    if ($LASTEXITCODE -ne 0) { throw 'Reading Luma admission TTL failed.' }
    $lumaPolicies = @(($lumaRaw | Out-String | ConvertFrom-Json))
    $lumaResource = "projects/$lumaProject/databases/$lumaDatabase/collectionGroups/$lumaCollection/fields/$lumaField"
    $lumaMatches = @($lumaPolicies | Where-Object {
        $_.name -ceq $lumaResource
    })
    if ($lumaMatches.Count -gt 1) { throw 'Unexpected duplicate Luma TTL policies.' }
    if ($lumaMatches.Count -eq 0) { return 'MISSING' }
    $lumaState = [string]$lumaMatches[0].ttlConfig.state
    if ([string]::IsNullOrWhiteSpace($lumaState)) { throw 'Luma TTL state was not reported.' }
    return $lumaState
}

$lumaBefore = Read-LumaTtl
$lumaChanged = $false
if ($Apply -and $lumaBefore -cnotin @('ACTIVE', 'CREATING')) {
    & $lumaGcloud firestore fields ttls update $lumaField "--project=$lumaProject" `
        "--database=$lumaDatabase" "--collection-group=$lumaCollection" `
        --enable-ttl --async --quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Enabling Luma admission TTL failed.' }
    $lumaChanged = $true
}
$lumaAfter = Read-LumaTtl
[pscustomobject]@{
    Mode = $(if ($Apply) { 'apply' } else { 'verify-only' })
    Project = $lumaProject
    Database = $lumaDatabase
    CollectionGroup = $lumaCollection
    Field = $lumaField
    Before = $lumaBefore
    After = $lumaAfter
    CloudChanged = $lumaChanged
    RetentionPolicyVerified = ($lumaAfter -ceq 'ACTIVE')
    ReleaseGate = 'ACTIVE policy is configuration proof; verify actual TTL cleanup separately.'
} | ConvertTo-Json
