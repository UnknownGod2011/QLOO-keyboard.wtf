[CmdletBinding()]
param([Parameter(Mandatory)][string]$CredentialBriefPath)
$ErrorActionPreference='Stop'
$gcloud='C:/Users/Admin/AppData/Local/Google/Cloud SDK/google-cloud-sdk/bin/gcloud.cmd'
$project='keyboard-wtf-staging';$service='keyboard-wtf-connectors-staging';$region='asia-south1'
$web='https://keyboard-wtf-connectors-staging.vercel.app'
$runtime='keyboard-wtf-paid-runtime@keyboard-wtf-staging.iam.gserviceaccount.com'
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
function Cloud([string[]]$Arguments) { & $gcloud @Arguments; if($LASTEXITCODE -ne 0){throw 'Staging cloud command failed. Production is not modified.'} }
$brief=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $CredentialBriefPath).Path)
$key=[regex]::Match($brief,'(?m)^COMPOSIO_API_KEY_STAGING\s*=\s*(\S+)').Groups[1].Value.Trim('"')
if($key.Length -lt 10){throw 'Secure staging project credential is missing.'}
try {$configs=Invoke-RestMethod -Uri 'https://backend.composio.dev/api/v3.1/auth_configs?show_disabled=true&limit=100' -Headers @{'x-api-key'=$key} -TimeoutSec 25}
catch {throw 'Secure staging OAuth discovery failed. No key or raw provider response is logged.'}
if($configs.next_cursor){throw 'Reconcile complete staging config pagination before deployment.'}
$map=@{};foreach($pair in @(@{family='google';toolkit='googlesuper'},@{family='googleforms';toolkit='googleforms'},@{family='youtube';toolkit='youtube'},@{family='github';toolkit='github'})) {
 $match=@($configs.items | Where-Object {$_.toolkit.slug -eq $pair.toolkit -and !$_.is_composio_managed -and $_.status -eq 'ENABLED'})
 if($match.Count -ne 1){throw ('One verified staging OAuth configuration required: '+$pair.toolkit)}
 $map[$pair.family]=$match[0].id
}
$secret='composio-connectors-staging-key'
$existing=& $gcloud secrets describe $secret ('--project='+$project) '--format=value(name)' 2>$null
if($LASTEXITCODE -ne 0){Cloud @('secrets','create',$secret,('--project='+$project),'--replication-policy=automatic')}
$access=(& $gcloud auth print-access-token) -join ''
if($LASTEXITCODE -ne 0 -or $access.Length -lt 20){throw 'Cloud authentication is unavailable.'}
try {
 # No plaintext secret file, command-line value or key output. Exact UTF-8
 # bytes avoid a newline that would invalidate the API-key request header.
 $secretVersion=$null
 if($existing) {
  $latest=Invoke-RestMethod -Uri ('https://secretmanager.googleapis.com/v1/projects/'+$project+'/secrets/'+$secret+'/versions/latest:access') -Headers @{Authorization=('Bearer '+$access)}
  if([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($latest.payload.data)) -ceq $key){$secretVersion=($latest.name -split '/')[-1]}
 }
 if(!$secretVersion) {
  $version=Invoke-RestMethod -Method Post -Uri ('https://secretmanager.googleapis.com/v1/projects/'+$project+'/secrets/'+$secret+':addVersion') -Headers @{Authorization=('Bearer '+$access)} -ContentType 'application/json' -Body (@{payload=@{data=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($key))}} | ConvertTo-Json -Compress)
  $secretVersion=($version.name -split '/')[-1]
 }
} catch {throw 'Staging secret storage did not complete. No key or raw error is logged.'}
Cloud @('secrets','add-iam-policy-binding',$secret,('--project='+$project),('--member=serviceAccount:'+$runtime),'--role=roles/secretmanager.secretAccessor','--format=value(name)')
$source=Join-Path $root 'staging/backend/KeyboardWtf.Staging.Api'
$files=Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'} | Sort-Object FullName
$manifest=($files | ForEach-Object {$_.FullName.Substring($source.Length)+':'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}) -join "`n"
$digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifest))).ToLowerInvariant()
$vars=@('Connectors__Enabled=true','Connectors__Environment=staging',('Connectors__WebBaseUrl='+$web),('PaidSystem__WebBaseUrl='+$web),('PaidSystem__AllowedOrigins__0='+$web),('KEYBOARD_WTF_SOURCE_COMMIT=connector-source-'+$digest.Substring(0,16)))
foreach($entry in $map.GetEnumerator()) {$vars+=('Connectors__AuthConfigIds__'+$entry.Key+'='+$entry.Value)}
# Clone only existing TEST payment bindings; no live payment secret or mode.
$baseline=((& $gcloud run services describe 'keyboard-wtf-paid-api' ('--project='+$project) ('--region='+$region) '--format=json') -join "`n") | ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'Staging baseline service identity is unavailable.'}
$bindings=@(('Connectors__ApiKey='+$secret+':'+$secretVersion))
foreach($entry in $baseline.spec.template.spec.containers[0].env) {
 if($entry.valueFrom.secretKeyRef) {
   if($entry.valueFrom.secretKeyRef.name -notmatch '^razorpay-test-'){throw 'Unexpected non-test baseline secret. Stop for independent review.'}
   $testVersion=$entry.valueFrom.secretKeyRef.key
   if($testVersion -ceq 'latest') {
    try {
     # Version metadata only, not secret access. Freeze the inherited TEST
     # binding for reproducible staging service/job identity.
     $metadata=Invoke-RestMethod -Uri ('https://secretmanager.googleapis.com/v1/projects/'+$project+'/secrets/'+$entry.valueFrom.secretKeyRef.name+'/versions/latest') -Headers @{Authorization=('Bearer '+$access)} -TimeoutSec 25
     if($metadata.state -cne 'ENABLED'){throw 'The test secret version is not enabled.'}
     $testVersion=($metadata.name -split '/')[-1]
    } catch {throw 'The inherited TEST secret version could not be pinned. No value is logged.'}
   }
   if($testVersion -notmatch '^\d+$'){throw 'A pinned TEST secret version is required.'}
   $bindings+=($entry.name+'='+$entry.valueFrom.secretKeyRef.name+':'+$testVersion)
 } elseif($entry.name -in @('RAZORPAY_TEST_MONTHLY_PLAN_ID','PaidSystem__AdminEmails__0')) {$vars+=($entry.name+'='+$entry.value)}
}
Cloud @('run','deploy',$service,('--project='+$project),('--region='+$region),('--source='+$source),('--service-account='+$runtime),
 '--allow-unauthenticated','--max-instances=3','--min-instances=0','--concurrency=20','--timeout=240','--memory=512Mi',
 # Do not use gcloud's caret-delimited syntax through gcloud.cmd: cmd.exe
 # consumes the carets and silently produces an invalid environment name.
 # All reviewed values here are comma-free, so its normal delimiter is safe.
 ('--set-env-vars='+($vars -join ',')),('--set-secrets='+($bindings -join ',')),'--quiet')
$url=(Cloud @('run','services','describe',$service,('--project='+$project),('--region='+$region),'--format=value(status.url)')) -join ''
[pscustomobject]@{service=$service;project=$project;backendUrl=$url;webBaseUrl=$web;sourceSha256=$digest;secretVersion=$secretVersion;productionModified=$false} | ConvertTo-Json -Compress
