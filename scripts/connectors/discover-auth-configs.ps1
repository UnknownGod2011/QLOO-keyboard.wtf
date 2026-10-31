[CmdletBinding()]
param([Parameter(Mandatory)][string]$CredentialBriefPath)
$ErrorActionPreference='Stop'
# Read-only project discovery. Never serialize an auth config or credential.
$brief=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $CredentialBriefPath).Path)
$api='https://backend.composio.dev/api/v3.1'
function Read([string]$Key,[string]$Path,[switch]$AllowMissing){
 try {return Invoke-RestMethod -Uri ($api+'/'+$Path) -Headers @{'x-api-key'=$Key} -TimeoutSec 25}
 catch {
  $status=[int]$_.Exception.Response.StatusCode
  if($AllowMissing -and $status -eq 404){return $null}
  throw ('Read-only connector discovery failed: HTTP '+$status+'. No raw provider response is logged.')
 }
}
$projects=@{};$keys=@{};$records=@()
foreach($environment in @('PROD','STAGING')){
 $key=[regex]::Match($brief,'(?m)^COMPOSIO_API_KEY_'+$environment+'\s*=\s*(\S+)').Groups[1].Value.Trim('"')
 if($key.Length -lt 10){throw 'An authorized project credential is unavailable.'}
 $keys[$environment]=$key;$items=@();$cursor='';$complete=$false
 for($page=0;$page -lt 10;$page++){
  $path='auth_configs?show_disabled=true&limit=100'
  if($cursor){$path+='&cursor='+[Uri]::EscapeDataString($cursor)}
  $result=Read $key $path;$items+=@($result.items);$cursor=$result.next_cursor
  if(!$cursor){$complete=$true;break}
 }
 if(!$complete){throw 'Complete project pagination was not established. No config is created.'}
 $projects[$environment]=$items
 $records+=@{environment=$environment.ToLowerInvariant();count=$items.Count;paginationComplete=$true;
  configs=@($items | ForEach-Object {@{id=$_.id;toolkit=$_.toolkit.slug;status=$_.status;custom=(!$_.is_composio_managed)}})}
}
$services=@()
foreach($service in @(@{toolkit='slack';id='ac_Zc-2QYnJjs4h'},@{toolkit='discord';id='ac_OfSb8s-ajUKZ'})){
 $config=Read $keys.PROD ('auth_configs/'+$service.id)
 if($config.toolkit.slug -cne $service.toolkit -or $config.is_composio_managed -or $config.status -cne 'ENABLED'){
  throw 'The supplied production config did not match its expected custom enabled toolkit.'
 }
 $credentials=$config.credentials
 $fieldNames=@($credentials.PSObject.Properties.Name)
 $secret=[string]$credentials.client_secret
 $scopes=([string]$credentials.scopes+','+[string]$credentials.user_scopes) -split '[,\s]+' | Where-Object {$_}
 $stageMatches=@($projects.STAGING | Where-Object {$_.toolkit.slug -ceq $service.toolkit})
 $stageReference=Read $keys.STAGING ('auth_configs/'+$service.id) -AllowMissing
 $services+=@{toolkit=$service.toolkit;productionConfig=$service.id;enabled=$true;custom=$true;
  credentialFieldNames=$fieldNames;publicClientIdPresent=([string]$credentials.client_id).Length -gt 0;
  clientSecretFieldPresent=$fieldNames -contains 'client_secret';clientSecretHasObfuscationMarker=($secret -match '[*•…]|\.\.\.|(?i)redact|mask');
  scopeCount=@($scopes).Count;adminOrAuditScopes=@($scopes | Where-Object {$_ -match '^(admin\.|auditlogs:)'}).Count;
  callbackHost=if($credentials.oauth_redirect_uri){([Uri]$credentials.oauth_redirect_uri).Host}else{'unknown'};
  stagingMatches=@($stageMatches | ForEach-Object {@{id=$_.id;status=$_.status;custom=(!$_.is_composio_managed)}});
  productionConfigReadableWithStagingKey=($null -ne $stageReference);configMutated=$false}
}
$proof=[ordered]@{time=[DateTimeOffset]::UtcNow.ToString('o');readOnly=$true;projects=$records;services=$services;
 secretRecovery='GET masks customer-owned sensitive credentials. Never copy an obfuscated value as an OAuth secret.';
 cloneSupport='No clone/import/shared-project source-config field is documented in the current create/update API or SDK. Cross-project reference GET is checked above.'}
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$output=Join-Path $root '.artifacts/connectors/hosted/auth-config-discovery.json'
$null=New-Item -ItemType Directory -Force -Path (Split-Path $output -Parent)
$proof | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding utf8
$proof | ConvertTo-Json -Depth 8
