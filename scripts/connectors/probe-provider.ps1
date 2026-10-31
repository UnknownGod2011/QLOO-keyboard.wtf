[CmdletBinding()]
param([Parameter(Mandatory)][string]$CredentialBriefPath,[switch]$InspectAuthorization)
$ErrorActionPreference='Stop'
# Bounded real staging protocol check only. No Google consent, account content,
# email, external write, AI generation or paid-product credit request.
$brief=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $CredentialBriefPath).Path)
$key=[regex]::Match($brief,'(?m)^COMPOSIO_API_KEY_STAGING\s*=\s*(\S+)').Groups[1].Value.Trim('"')
if($key.Length -lt 10){throw 'Secure staging credential is unavailable.'}
$headers=@{'x-api-key'=$key}
$api='https://backend.composio.dev/api/v3.1'
function Call([string]$method,[string]$path,$body=$null) {
 try {
  $args=@{Uri=($api+'/'+$path);Method=$method;Headers=$headers;TimeoutSec=25}
  if($null -ne $body){$args.ContentType='application/json';$args.Body=($body|ConvertTo-Json -Depth 12 -Compress)}
  Invoke-RestMethod @args
 } catch {throw ('Staging provider request did not finish: HTTP '+[int]$_.Exception.Response.StatusCode)}
}
$configs=Call 'Get' 'auth_configs?show_disabled=true&limit=100'
if($configs.next_cursor){throw 'Reconcile complete config pagination before this probe.'}
$proof=@()
foreach($toolkit in @('googlesuper','github')) {
 $config=@($configs.items|Where-Object{$_.toolkit.slug -eq $toolkit -and !$_.is_composio_managed -and $_.status -eq 'ENABLED'})
 if($config.Count -ne 1){throw 'One custom staging OAuth config is required.'}
 $seed='connector-protocol-probe-'+[guid]::NewGuid().ToString('N')
 $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($seed))).ToLowerInvariant()
 $user='kwt_staging_'+$hash
 $alias='kwt_'+[guid]::NewGuid().ToString('N')
 $id=$null;$result=$null
 try {
  $created=Call 'Post' 'connected_accounts' @{auth_config=@{id=$config[0].id};connection=@{user_id=$user;callback_url='https://keyboard-wtf-connectors-staging.vercel.app/connectors';alias=$alias}}
  $id=$created.id
  if($id -notmatch '^[A-Za-z0-9_-]{4,128}$'){throw 'Provider creation acknowledgement has no bounded reference.'}
  $live=Call 'Get' ('connected_accounts/'+$id)
  $ownerVerified=$live.user_id -eq $user
  $configVerified=$live.auth_config.id -eq $config[0].id
  $toolkitVerified=$live.toolkit.slug -eq $toolkit
  if(!$ownerVerified -or !$configVerified -or !$toolkitVerified){throw 'Independent provider owner/config/toolkit verification failed.'}
  $redirect=if($created.redirect_url){$created.redirect_url}else{$created.redirectUrl}
  if($InspectAuthorization){
   $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=(Get-Command node).Source
   $info.ArgumentList.Add((Join-Path $PSScriptRoot 'inspect-authorization.mjs'))
   $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.UseShellExecute=$false;$info.CreateNoWindow=$true
   $child=[Diagnostics.Process]::Start($info)
   $child.StandardInput.WriteLine((@{redirect=$redirect;toolkit=$toolkit}|ConvertTo-Json -Compress));$child.StandardInput.Close()
   $safeOutput=$child.StandardOutput.ReadToEnd();$null=$child.StandardError.ReadToEnd();$child.WaitForExit()
   if($safeOutput){Write-Output $safeOutput.Trim()}
   if($child.ExitCode -ne 0){throw 'Logged-out provider sign-in inspection did not complete.'}
  }
  $result=[ordered]@{toolkit=$toolkit;created=$true;ownerVerified=$ownerVerified;configVerified=$configVerified;toolkitVerified=$toolkitVerified;aliasRetained=($live.alias -eq $alias);status=$live.status;redirectHost=([Uri]$redirect).Host;credentialsGranted=$false;contentRead=$false;providerWrite=$false;removed=$false}
 } finally {
  # The fresh unique provider UID belongs only to this probe. Independently
  # confirm it on every item; never treat provider filtering as authorization.
  $owned=Call 'Get' ('connected_accounts?limit=100&user_ids='+$user)
  if($owned.next_cursor){throw 'Unexpected probe pagination; cleanup is not confirmed.'}
  foreach($item in $owned.items) {
   if($item.user_id -ne $user){throw 'Probe cleanup owner mismatch. No foreign account is removed.'}
   $null=Call 'Delete' ('connected_accounts/'+$item.id)
  }
  $remaining=Call 'Get' ('connected_accounts?limit=100&user_ids='+$user)
  if($remaining.items.Count -ne 0 -or $remaining.next_cursor){throw 'Staging probe cleanup is not independently confirmed.'}
  if($null -ne $result){$result.removed=$true}
 }
 if($null -ne $result){$proof+=($result|ConvertTo-Json -Compress)}
}
$proof | ForEach-Object {Write-Output $_}
