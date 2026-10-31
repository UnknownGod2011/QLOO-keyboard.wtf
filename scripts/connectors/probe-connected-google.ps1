[CmdletBinding()]
param([switch]$Read,[switch]$Diagnostic)
$ErrorActionPreference='Stop'
# Bounded read-only staging acceptance probe. No fake Firebase session, OAuth,
# account mutation, sends, AI calls, purchases or permanent content capture.
# This deliberately labels provider-adapter evidence separately from backend/
# installed/Jarvis E2E; it cannot substitute for their authenticated paths.
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$project='keyboard-wtf-staging'
$gcloud='C:/Users/Admin/AppData/Local/Google/Cloud SDK/google-cloud-sdk/bin/gcloud.cmd'
function Cloud([string[]]$Arguments) {
 $answer=& $gcloud @Arguments
 if($LASTEXITCODE -ne 0){throw 'Staging credential lookup did not complete.'}
 return ($answer -join '').Trim()
}
function Request([string]$Method,[string]$Uri,$Headers,$Body=$null) {
 try {
  $args=@{Method=$Method;Uri=$Uri;Headers=$Headers;TimeoutSec=40;SkipHttpErrorCheck=$true}
  if($null -ne $Body){$args.ContentType='application/json';$args.Body=$Body | ConvertTo-Json -Depth 15 -Compress}
  $response=Invoke-WebRequest @args
  if($response.StatusCode -notin (200,201)){return @{http=[int]$response.StatusCode;data=$null;requestId=$null}}
  $requestId=($response.Headers['x-request-id'] -join '')
  if($requestId -notmatch '^[A-Za-z0-9_-]{8,150}$'){$requestId=$null}
  return @{http=[int]$response.StatusCode;data=($response.Content | ConvertFrom-Json);requestId=$requestId}
 }catch{throw 'Staging probe transport failed. No raw response or credentials are logged.'}
}
$access=Cloud @('auth','print-access-token')
$cloudHeaders=@{Authorization=('Bearer '+$access);'x-goog-user-project'=$project}
$firestore='https://firestore.googleapis.com/v1/projects/'+$project+'/databases/(default)'
$query=Request 'Post' ($firestore+'/documents:runQuery') $cloudHeaders @{structuredQuery=@{
 from=@(@{collectionId='connectorAccounts';allDescendants=$true});limit=51
 select=@{fields=@(@{fieldPath='family'},@{fieldPath='state'},@{fieldPath='providerId'},@{fieldPath='isDefault'})}}}
if($query.http -ne 200){throw 'Read-only connection journal lookup was rejected.'}
$documents=@($query.data | Where-Object {$null -ne $_.document})
if($documents.Count -ge 51){throw 'Connection journal inventory may be truncated. No account was guessed or content call made.'}
$accounts=@($documents | Where-Object {$_.document.fields.family.stringValue -ceq 'google' -and $_.document.fields.state.stringValue -ceq 'connected'})
if($accounts.Count -ne 1){throw 'Probe requires exactly one accepted staging Google connection. No account was guessed.'}
$doc=$accounts[0].document
$parts=$doc.name -split '/'
if($parts[-4] -cne 'accounts' -or $parts[-2] -cne 'connectorAccounts'){throw 'Unexpected connection owner path.'}
$uid=$parts[-3]
$uidHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($uid))).ToLowerInvariant()
$providerId=$doc.fields.providerId.stringValue
if($providerId -notmatch '^[A-Za-z0-9_-]{4,128}$'){throw 'Invalid provider account reference.'}
$key=Cloud @('secrets','versions','access','1','--secret=composio-connectors-staging-key',('--project='+$project))
$headers=@{'x-api-key'=$key}
$api='https://backend.composio.dev/api/v3.1'
$owned=Request 'Get' ($api+'/connected_accounts/'+$providerId) $headers
if($owned.http -ne 200 -or $owned.data.user_id -cne ('kwt_staging_'+$uidHash) -or $owned.data.status -cne 'ACTIVE' -or $owned.data.is_disabled -eq $true -or $owned.data.toolkit.slug -cne 'googlesuper' -or $owned.data.auth_config.id -cne 'ac_bHRNzkSvozqr'){
 throw 'Provider ACTIVE/config/toolkit/UID ownership was not independently verified. No content call was made.'
}
$proof=[ordered]@{time=[DateTimeOffset]::UtcNow.ToString('o');project=$project;scope='Read-only provider adapter probe, NOT authenticated backend/installed Jarvis acceptance';
 accountUidSha256=$uidHash;journalInventoryCount=$documents.Count;journalInventoryExhaustive=$true;acceptedAccountCount=$accounts.Count;providerActive=$true;providerOwnerBound=$true;adoptedAccount=$true;
 productionModified=$false;aiCalls=0;modelCredits=0;externalWrites=0;reads=@()}
if($Read) {
 $now=[DateTimeOffset]::UtcNow
 $requests=@(
  @{capability='mail_search';endpoint='https://www.googleapis.com/gmail/v1/users/me/messages?q=in%3Ainbox&maxResults=2';array='messages'},
  @{capability='calendar_list';endpoint=('https://www.googleapis.com/calendar/v3/calendars/primary/events?singleEvents=true&maxResults=2&timeMin='+[Uri]::EscapeDataString($now.ToString('o'))+'&timeMax='+[Uri]::EscapeDataString($now.AddDays(1).ToString('o')));array='items'},
  @{capability='drive_search';endpoint='https://www.googleapis.com/drive/v3/files?pageSize=2&fields=files(id)&q=trashed%20%3D%20false%20and%20name%20contains%20%27keyboard%27';array='files'}
 )
 if($Diagnostic){$requests=@($requests[0])}
 foreach($request in $requests) {
  $timer=[Diagnostics.Stopwatch]::StartNew()
  $answer=Request 'Post' ($api+'/tools/execute/proxy') $headers @{connected_account_id=$providerId;endpoint=$request.endpoint;method='GET';body=$null}
  $upstream=if($answer.data.status){[int]$answer.data.status}else{$null}
  $count=if($upstream -eq 200){@($answer.data.data.($request.array)).Where({$null -ne $_}).Count}else{0}
  # Only bounded machine-readable diagnostic codes/service names; never the
  # raw provider message, body, token, headers or private resource identifiers.
  $providerFailure=$answer.data.data.error
  $reason=@($providerFailure.details | Where-Object {$_.'@type' -ceq 'type.googleapis.com/google.rpc.ErrorInfo'}) | Select-Object -First 1
  $legacyFailure=@($providerFailure.errors | Where-Object {$null -ne $_}) | Select-Object -First 1
  $safeReason=if($reason.reason -match '^[A-Z_]{1,80}$'){$reason.reason}elseif($legacyFailure.reason -match '^[A-Za-z_]{1,80}$'){$legacyFailure.reason}else{$null}
  $safeService=if($reason.metadata.service -match '^[a-z0-9.-]{1,100}\.googleapis\.com$'){$reason.metadata.service}else{$null}
  $proof.reads+=@{capability=$request.capability;proxyHttp=$answer.http;providerHttp=$upstream;success=($answer.http -eq 200 -and $upstream -eq 200);resultCount=$count;elapsedMs=$timer.ElapsedMilliseconds;providerRequestId=$answer.requestId;errorReason=$safeReason;service=$safeService}
 }
}
$output=Join-Path $root $(if($Diagnostic){'.artifacts/connectors/hosted/accepted-google-diagnostic.json'}else{'.artifacts/connectors/hosted/accepted-google-read.json'})
$null=New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force
$proof | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding utf8
$proof | ConvertTo-Json -Depth 8
