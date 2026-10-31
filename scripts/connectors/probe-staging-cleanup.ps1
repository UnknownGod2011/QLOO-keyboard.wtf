[CmdletBinding()]
param([Parameter(Mandatory)][string]$CredentialBriefPath)
$ErrorActionPreference='Stop'
# Only a fresh synthetic staging owner with an INITIATED, unconsented grant.
# This does not mint Firebase tokens, impersonate Google login, read content,
# purchase anything, or prove end-user OAuth/task acceptance.
$project='keyboard-wtf-staging';$region='asia-south1';$job='keyboard-wtf-connectors-oauth-cleanup'
$gcloud='C:/Users/Admin/AppData/Local/Google/Cloud SDK/google-cloud-sdk/bin/gcloud.cmd'
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$output=Join-Path $root '.artifacts/connectors/hosted/cleanup-execution.json'
function Cloud([string[]]$Arguments){
 $answer=& $gcloud @Arguments
 if($LASTEXITCODE -ne 0){throw 'Isolated staging cleanup command did not complete.'}
 return $answer
}
$access=(Cloud @('auth','print-access-token')) -join ''
$cloudHeaders=@{Authorization=('Bearer '+$access);'x-goog-user-project'=$project}
$brief=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $CredentialBriefPath).Path)
$key=[regex]::Match($brief,'(?m)^COMPOSIO_API_KEY_STAGING\s*=\s*(\S+)').Groups[1].Value.Trim('"')
if($key.Length -lt 10){throw 'Secure staging credential is unavailable.'}
$providerHeaders=@{'x-api-key'=$key};$providerApi='https://backend.composio.dev/api/v3.1'
$db='projects/'+$project+'/databases/(default)';$firestore='https://firestore.googleapis.com/v1/'+$db
function Request([string]$Method,[string]$Uri,$Headers,$Body=$null,[switch]$AllowMissing){
 try {
  $args=@{Method=$Method;Uri=$Uri;Headers=$Headers;TimeoutSec=40}
  if($null -ne $Body){$args.ContentType='application/json';$args.Body=$Body | ConvertTo-Json -Depth 20 -Compress}
  return Invoke-RestMethod @args
 } catch {
  $status=[int]$_.Exception.Response.StatusCode
  if($AllowMissing -and $status -eq 404){return $null}
  throw ('Synthetic staging check failed: HTTP '+$status+'. No raw response is logged.')
 }
}
function Provider([string]$Method,[string]$Path,$Body=$null,[switch]$AllowMissing){
 return Request $Method ($providerApi+'/'+$Path) $providerHeaders $Body -AllowMissing:$AllowMissing
}
function StringValue([string]$Value){return @{stringValue=$Value}}
$seed='connector-cleanup-probe-'+[guid]::NewGuid().ToString('N')
$uid=$seed;$attempt=[guid]::NewGuid().ToString('N');$requestId=[guid]::NewGuid().ToString('N')
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($uid))).ToLowerInvariant()
$providerUser='kwt_staging_'+$hash;$accountName=$db+'/documents/accounts/'+$uid
$attemptName=$accountName+'/connectorAttempts/'+$attempt
$proof=[ordered]@{time=[DateTimeOffset]::UtcNow.ToString('o');project=$project;job=$job;synthetic=$true;
 noFirebaseLogin=$true;consentGranted=$false;contentRead=$false;providerWrite=$false;productionModified=$false;
 providerInitiated=$false;jobCompleted=$false;providerAbsent=$false;attemptCleaned=$false;fixtureRemoved=$false;ttlDeletion='Not asserted by this operational cleanup test.'}
$id=$null;$fixtureCreated=$false
try {
 $configs=Provider 'Get' 'auth_configs?show_disabled=true&limit=100'
 if($configs.next_cursor){throw 'Complete staging config discovery is required.'}
 $config=@($configs.items | Where-Object {$_.toolkit.slug -ceq 'googlesuper' -and !$_.is_composio_managed -and $_.status -ceq 'ENABLED'})
 if($config.Count -ne 1){throw 'One custom staging Google config is required.'}
 $created=Provider 'Post' 'connected_accounts' @{auth_config=@{id=$config[0].id};connection=@{user_id=$providerUser;callback_url='https://keyboard-wtf-connectors-staging.vercel.app/connectors';alias=('kwt_'+$attempt)}}
 $id=$created.id
 if($id -notmatch '^[A-Za-z0-9_-]{4,128}$'){throw 'No bounded provider reference was acknowledged.'}
 $owned=Provider 'Get' ('connected_accounts/'+$id)
 if($owned.user_id -cne $providerUser -or $owned.auth_config.id -cne $config[0].id -or $owned.toolkit.slug -cne 'googlesuper' -or $owned.status -cne 'INITIATED'){
  throw 'Fresh unconsented probe ownership was not verified.'
 }
 $proof.providerInitiated=$true
 # Firestore preconditions make creation fail if either unique fixture exists.
 $fields=@{family=(StringValue 'google');stateHash=(StringValue ('a'*64));requestId=(StringValue $requestId);state=(StringValue 'connecting');
  expiresAt=@{timestampValue=[DateTimeOffset]::UtcNow.AddMinutes(-2).ToString('o')};deleteAfter=@{nullValue=$null};cleanupRequired=@{booleanValue=$true};
  providerId=(StringValue $id);redirectUrl=(StringValue '');accountId=(StringValue '')}
 $writes=@(@{update=@{name=$accountName;fields=@{status=(StringValue 'active');connectorProbe=@{booleanValue=$true}}};currentDocument=@{exists=$false}},
  @{update=@{name=$attemptName;fields=$fields};currentDocument=@{exists=$false}})
 $null=Request 'Post' ($firestore+'/documents:commit') $cloudHeaders @{writes=$writes}
 $fixtureCreated=$true
 $null=Cloud @('run','jobs','execute',$job,('--project='+$project),('--region='+$region),'--wait','--quiet')
 $proof.jobCompleted=$true
 $live=Provider 'Get' ('connected_accounts/'+$id) -AllowMissing
 $remaining=Provider 'Get' ('connected_accounts?limit=100&user_ids='+$providerUser)
 if($null -ne $live -or $remaining.items.Count -ne 0 -or $remaining.next_cursor){throw 'Provider credential removal was not independently verified.'}
 $proof.providerAbsent=$true
 $saved=Request 'Get' ($firestore+'/documents/accounts/'+$uid+'/connectorAttempts/'+$attempt) $cloudHeaders
 if($saved.fields.cleanupRequired.booleanValue -ne $false -or $saved.fields.state.stringValue -cne 'expired' -or !$saved.fields.deleteAfter.timestampValue){throw 'Cleanup journal did not reach a terminal TTL-eligible state.'}
 $proof.attemptCleaned=$true
} finally {
 # Remove only this exact synthetic provider owner/fixture, never real users.
 # Recovery is explicit and recorded, rather than leaving orphan credentials.
 if($id){
  $owned=Provider 'Get' ('connected_accounts/'+$id) -AllowMissing
  if($owned){
   if($owned.user_id -cne $providerUser){throw 'Recovery owner mismatch. No foreign credential was removed.'}
   $null=Provider 'Delete' ('connected_accounts/'+$id)
   if($null -ne (Provider 'Get' ('connected_accounts/'+$id) -AllowMissing)){throw 'Synthetic provider recovery remains pending.'}
  }
 }
 if($fixtureCreated){
  $saved=Request 'Get' ($firestore+'/documents/accounts/'+$uid+'/connectorAttempts/'+$attempt) $cloudHeaders
  $parent=Request 'Get' ($firestore+'/documents/accounts/'+$uid) $cloudHeaders
  if($parent.fields.connectorProbe.booleanValue -ne $true -or $saved.fields.providerId.stringValue -cne $id){throw 'Synthetic fixture cleanup ownership failed.'}
  $null=Request 'Post' ($firestore+'/documents:commit') $cloudHeaders @{writes=@(
   @{delete=$attemptName;currentDocument=@{updateTime=$saved.updateTime}},@{delete=$accountName;currentDocument=@{updateTime=$parent.updateTime}})}
  $proof.fixtureRemoved=$true
 }
 $null=New-Item -ItemType Directory -Force -Path (Split-Path $output -Parent)
 $proof | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding utf8
 $proof | ConvertTo-Json -Depth 5
}
