[CmdletBinding()]
param([Parameter(Mandatory)][string]$CredentialBriefPath, [switch]$Apply)
$ErrorActionPreference='Stop'
# Keys stay only in this process. Never dump a provider payload/exception body.
$brief=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $CredentialBriefPath).Path)
function Read-Setting([string]$name) {
  $value=[regex]::Match($brief,'(?m)^'+[regex]::Escape($name)+'\s*=\s*(\S+)').Groups[1].Value.Trim('"')
  if($value.Length -lt 6) { throw "Required secure configuration is missing: $name" }
  return $value
}
$stageKey=Read-Setting 'COMPOSIO_API_KEY_STAGING'
$prodKey=Read-Setting 'COMPOSIO_API_KEY_PROD'
if($stageKey -eq $prodKey) { throw 'Staging and production project credentials must be distinct.' }
$stageHeaders=@{'x-api-key'=$stageKey};$prodHeaders=@{'x-api-key'=$prodKey}
function Provider([string]$method,[string]$route,$headers,$body=$null) {
  try {
    $args=@{Method=$method;Uri=('https://backend.composio.dev/api/v3.1/'+$route);Headers=$headers;TimeoutSec=30}
    if($null -ne $body) {$args.ContentType='application/json';$args.Body=($body | ConvertTo-Json -Depth 15 -Compress)}
    return Invoke-RestMethod @args
  } catch { $status=[int]$_.Exception.Response.StatusCode; throw "Connector configuration request did not complete (HTTP $status). No automatic write retry." }
}
$items=@();$cursor=$null
do {
  $route='auth_configs?show_disabled=true&limit=100'
  if($cursor){$route+='&cursor='+[Uri]::EscapeDataString($cursor)}
  $page=Provider GET $route $stageHeaders;$items+=@($page.items);$cursor=$page.next_cursor
} while($cursor)
$desired=@(
  @{family='google';toolkit='googlesuper';source='ac_qb3MXPDVzUmo';prefix='GOOGLE'},
  @{family='googleforms';toolkit='googleforms';source='ac_wfjI98zgDf7o';prefix='GOOGLE'},
  @{family='youtube';toolkit='youtube';source='ac_I4sF-ed4pSTq';prefix='GOOGLE'},
  @{family='github';toolkit='github';source='ac_UAfPuGPFoNd1';prefix='GITHUB'}
)
$result=@{}
foreach($target in $desired) {
  $matches=@($items | Where-Object {$_.toolkit.slug -eq $target.toolkit})
  if($matches.Count -gt 1) {throw ('Multiple staging configs require reconciliation: '+$target.toolkit)}
  if($matches.Count -eq 1) {
    if($matches[0].is_composio_managed -or $matches[0].status -ne 'ENABLED') {throw ('Existing staging config is not enabled custom OAuth: '+$target.toolkit)}
    $result[$target.family]=$matches[0].id;continue
  }
  # Existing production config supplies scopes/callback, not a recoverable
  # secret. Only explicitly supplied credentials are used for new staging.
  $source=Provider GET ('auth_configs/'+$target.source) $prodHeaders
  if($source.toolkit.slug -ne $target.toolkit -or $source.is_composio_managed -or $source.auth_scheme -ne 'OAUTH2') {throw 'Source configuration identity does not match.'}
  $credentials=@{client_id=(Read-Setting ($target.prefix+'_CONNECTOR_OAUTH_CLIENT_ID'));client_secret=(Read-Setting ($target.prefix+'_CONNECTOR_OAUTH_CLIENT_SECRET'));
    oauth_redirect_uri=$source.credentials.oauth_redirect_uri;scopes=@($source.credentials.scopes);user_scopes=@($source.credentials.user_scopes)}
  if(!$Apply) { [pscustomobject]@{toolkit=$target.toolkit;action='Would create one missing custom staging config';scopeCount=$credentials.scopes.Count} | ConvertTo-Json -Compress;continue }
  $created=Provider POST 'auth_configs' $stageHeaders @{toolkit=@{slug=$target.toolkit};auth_config=@{type='use_custom_auth';authScheme='OAUTH2';name=('keyboard.wtf staging '+$target.toolkit);credentials=$credentials;is_enabled_for_tool_router=$false}}
  $id=$created.auth_config.id;if(!$id){$id=$created.id}
  if($id -notmatch '^ac_[A-Za-z0-9_-]{4,100}$') {throw 'Configuration acknowledgement missing. Re-list before any further create.'}
  $verify=Provider GET ('auth_configs/'+$id) $stageHeaders
  if($verify.toolkit.slug -ne $target.toolkit -or $verify.is_composio_managed -or $verify.status -ne 'ENABLED') {throw 'Staging configuration verification failed.'}
  $result[$target.family]=$id
  [pscustomobject]@{toolkit=$target.toolkit;id=$id;custom=$true;state='verified';productionModified=$false} | ConvertTo-Json -Compress
}
if($Apply) { $result | ConvertTo-Json -Compress }
