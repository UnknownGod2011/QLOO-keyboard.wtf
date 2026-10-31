[CmdletBinding()]
param([switch]$Apply,[switch]$Verify)
$ErrorActionPreference='Stop'
if($Apply -and $Verify){throw 'Choose Apply or Verify, not both.'}
$project='keyboard-wtf-staging';$region='asia-south1'
$service='keyboard-wtf-connectors-staging';$job='keyboard-wtf-connectors-oauth-cleanup'
$scheduler='keyboard-wtf-connectors-oauth-cleanup-quarterhour'
$runtime='keyboard-wtf-paid-runtime@keyboard-wtf-staging.iam.gserviceaccount.com'
$invoker='keyboard-wtf-paid-scheduler@keyboard-wtf-staging.iam.gserviceaccount.com'
$gcloud='C:/Users/Admin/AppData/Local/Google/Cloud SDK/google-cloud-sdk/bin/gcloud.cmd'
$resource='projects/'+$project+'/databases/(default)'
$api='https://firestore.googleapis.com/v1/'+$resource
$retention=@{connectorAttempts='deleteAfter';connectorSlots='expiresAt';connectorAccounts='deleteAfter';connectorTasks='deleteAfter';connectorOperations='deleteAfter';connectorAdmission='deleteAfter'}
if(!$Apply -and !$Verify){
 [pscustomobject]@{mode='plan';project=$project;service=$service;job=$job;scheduler=$scheduler;ttl=$retention;productionModified=$false} | ConvertTo-Json -Depth 4
 return
}
function Cloud([string[]]$Arguments){
 $answer=& $gcloud @Arguments
 if($LASTEXITCODE -ne 0){throw 'Connector staging cloud command failed. No production resource is targeted.'}
 return $answer
}
function CloudJson([string[]]$Arguments){return ((Cloud ($Arguments+@('--format=json'))) -join "`n") | ConvertFrom-Json}
$access=(Cloud @('auth','print-access-token')) -join ''
$headers=@{Authorization=('Bearer '+$access);'x-goog-user-project'=$project}
function Rest([string]$Method,[string]$Uri,$Body=$null){
 try {
  $arguments=@{Method=$Method;Uri=$Uri;Headers=$headers;TimeoutSec=40}
  if($null -ne $Body){$arguments.ContentType='application/json';$arguments.Body=$Body | ConvertTo-Json -Depth 20 -Compress}
  return Invoke-RestMethod @arguments
 } catch {throw ('Connector staging administrative request failed: HTTP '+[int]$_.Exception.Response.StatusCode)}
}
function Field([string]$Collection,[string]$Name){return Rest 'Get' ($api+'/collectionGroups/'+$Collection+'/fields/'+$Name)}
$deployed=CloudJson @('run','services','describe',$service,('--project='+$project),('--region='+$region))
$container=$deployed.spec.template.spec.containers[0]
$environment=@{};foreach($entry in $container.env){if($null -ne $entry.value){$environment[$entry.name]=$entry.value}}
if($environment.Connectors__Enabled -cne 'true' -or $environment.Connectors__Environment -cne 'staging' -or
 $environment.Connectors__WebBaseUrl -cne 'https://keyboard-wtf-connectors-staging.vercel.app' -or
 $deployed.spec.template.spec.serviceAccountName -cne $runtime){throw 'The isolated deployed connector identity is not verified.'}
$revision=CloudJson @('run','revisions','describe',$deployed.status.latestReadyRevisionName,('--project='+$project),('--region='+$region))
$image=$revision.status.imageDigest
if($image -notmatch '^asia-south1-docker.pkg.dev/keyboard-wtf-staging/.+@sha256:[a-f0-9]{64}$'){throw 'A pinned staging image is required.'}

# A pending flow must retain its provider cleanup reference. Refuse legacy or
# unsafe records rather than enabling TTL that might erase the only reference.
$query=@{structuredQuery=@{from=@(@{collectionId='connectorAttempts';allDescendants=$true});select=@{fields=@(@{fieldPath='state'},@{fieldPath='cleanupRequired'},@{fieldPath='deleteAfter'})};limit=301}}
$attempts=@((Rest 'Post' ($api+'/documents:runQuery') $query) | Where-Object {$_.document})
if($attempts.Count -gt 300 -or @($attempts | Where-Object {
 !$_.document.fields.cleanupRequired -or ($_.document.fields.cleanupRequired.booleanValue -and $_.document.fields.deleteAfter.timestampValue)
}).Count -gt 0){throw 'Resolve legacy/pending OAuth retention before enabling cleanup TTL. No records were changed.'}

$index=Field 'connectorAttempts' 'cleanupRequired'
$group=@($index.indexConfig.indexes | Where-Object {$_.queryScope -ceq 'COLLECTION_GROUP' -and $_.fields[0].order -ceq 'ASCENDING'})
if($Apply -and $group.Count -eq 0){
 # Preserve inherited/existing collection indexes; add only this group index.
 $indexes=@($index.indexConfig.indexes | ForEach-Object {@{queryScope=$_.queryScope;fields=$_.fields}})
 $indexes+=@{queryScope='COLLECTION_GROUP';fields=@(@{fieldPath='cleanupRequired';order='ASCENDING'})}
 $null=Rest 'Patch' ($api+'/collectionGroups/connectorAttempts/fields/cleanupRequired?updateMask=indexConfig') @{name=$index.name;indexConfig=@{indexes=$indexes}}
}
$policies=@()
foreach($entry in $retention.GetEnumerator()){
 $field=Field $entry.Key $entry.Value
 if($Apply -and $field.ttlConfig.state -cnotin @('ACTIVE','CREATING')){
  $null=Cloud @('firestore','fields','ttls','update',$entry.Value,('--project='+$project),'--database=(default)',('--collection-group='+$entry.Key),'--enable-ttl','--async','--quiet')
  $field=Field $entry.Key $entry.Value
 }
 $policies+=@{collection=$entry.Key;field=$entry.Value;state=if($field.ttlConfig.state){$field.ttlConfig.state}else{'MISSING'}}
}

if($Apply){
 $vars=@();$secrets=@()
 foreach($entry in $container.env){
  if($entry.valueFrom.secretKeyRef){
   $binding=$entry.valueFrom.secretKeyRef
   if($binding.name -notmatch '^(?:razorpay-test-.+|composio-connectors-staging-key)$' -or $binding.key -notmatch '^\d+$'){
    throw 'Cleanup must reuse exact pinned staging secret bindings, never production or latest.'
   }
   $secrets+=($entry.name+'='+$binding.name+':'+$binding.key)
  } elseif($null -ne $entry.value){
   if($entry.value.Contains(',')){throw 'Unexpected comma in reviewed staging environment.'}
   $vars+=($entry.name+'='+$entry.value)
  }
 }
 $null=Cloud @('run','jobs','deploy',$job,('--project='+$project),('--region='+$region),('--image='+$image),('--service-account='+$runtime),
  '--args=--maintenance-job=connector-oauth','--tasks=1','--parallelism=1','--max-retries=0','--task-timeout=600','--memory=512Mi',
  ('--set-env-vars='+($vars -join ',')),('--set-secrets='+($secrets -join ',')),'--quiet')
 $null=Cloud @('run','jobs','add-iam-policy-binding',$job,('--project='+$project),('--region='+$region),('--member=serviceAccount:'+$invoker),'--role=roles/run.invoker','--quiet')
 $existing=& $gcloud scheduler jobs describe $scheduler ('--project='+$project) ('--location='+$region) '--format=value(name)' 2>$null
 $verb=if($LASTEXITCODE -eq 0){'update'}else{'create'}
 $headerArgument=if($verb -ceq 'update'){'--update-headers=Content-Type=application/json'}else{'--headers=Content-Type=application/json'}
 $null=Cloud @('scheduler','jobs',$verb,'http',$scheduler,('--project='+$project),('--location='+$region),'--schedule=*/15 * * * *','--time-zone=Etc/UTC',
  ('--uri=https://run.googleapis.com/v2/projects/'+$project+'/locations/'+$region+'/jobs/'+$job+':run'),'--http-method=POST',
  ('--oauth-service-account-email='+$invoker),$headerArgument,'--message-body={}','--max-retry-attempts=0','--quiet')
}
$index=Field 'connectorAttempts' 'cleanupRequired'
$group=@($index.indexConfig.indexes | Where-Object {$_.queryScope -ceq 'COLLECTION_GROUP' -and $_.fields[0].order -ceq 'ASCENDING'})
$installed=CloudJson @('run','jobs','describe',$job,('--project='+$project),('--region='+$region))
$timer=CloudJson @('scheduler','jobs','describe',$scheduler,('--project='+$project),('--location='+$region))
$proof=[ordered]@{time=[DateTimeOffset]::UtcNow.ToString('o');project=$project;backendRevision=$deployed.status.latestReadyRevisionName;image=$image;
 indexState=if($group.Count){$group[0].state}else{'MISSING'};ttl=$policies;job=$job;jobImage=$installed.spec.template.spec.template.spec.containers[0].image;
 scheduler=$scheduler;schedulerState=$timer.state;schedulerTarget=$timer.httpTarget.uri;productionModified=$false;
 executionProof='Configuration only; separately verify a real cleanup execution and eventual Firestore TTL deletion.'}
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$output=Join-Path $root '.artifacts/connectors/hosted/cleanup-configuration.json'
$null=New-Item -ItemType Directory -Force -Path (Split-Path $output -Parent)
$proof | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding utf8
$proof | ConvertTo-Json -Depth 8
