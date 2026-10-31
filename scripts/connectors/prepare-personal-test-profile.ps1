[CmdletBinding()]
param([Parameter(Mandatory)][string]$CredentialDocumentPath,
      [Parameter(Mandatory)][string]$AssemblyPath,
      [switch]$Configure)
$ErrorActionPreference='Stop'
# Authorized PC test tooling only. No credential is printed, logged, returned,
# placed in argv/environment, or sent outside the existing Gemini validator.
# The user explicitly supplied this local credential document for API-mode tests.
$profile=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'keyboard.wtf-connectors-staging-candidate'
$vaultPath=Join-Path $profile 'personal-api-v1.dat'
if($Configure -and (Test-Path -LiteralPath $vaultPath)){throw 'Candidate credential store already exists; use its Settings, never overwrite it.'}
if($Configure -and (Get-Process -Name 'keyboard.wtf' -ErrorAction SilentlyContinue | Where-Object {$_.Path -like '*\keyboard.wtf Connectors Staging Candidate\*'})){
 throw 'Stop the candidate before preparing its isolated credential store.'
}
$assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$validatorType=$assembly.GetType('KeyboardWtf.Services.Ai.Backends.PersonalGeminiKeyValidator',$true)
$validator=$validatorType.GetConstructors($flags)[0].Invoke([object[]]@($null,$null))
$validationType=$assembly.GetType('KeyboardWtf.Services.Ai.Backends.PersonalKeyValidation',$true)
$archive=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $CredentialDocumentPath).Path)
$phase='document'
try {
 $part=$archive.GetEntry('word/document.xml')
 if(!$part -or $part.Length -gt 1MB){throw 'Expected bounded Word credential document.'}
 $reader=[IO.StreamReader]::new($part.Open())
 try {$xml=[xml]$reader.ReadToEnd()}finally{$reader.Dispose()}
 $ns=[Xml.XmlNamespaceManager]::new($xml.NameTable)
 $ns.AddNamespace('w','http://schemas.openxmlformats.org/wordprocessingml/2006/main')
 $text=($xml.SelectNodes('//w:p',$ns) | ForEach-Object {($_.SelectNodes('.//w:t',$ns) | ForEach-Object {$_.InnerText}) -join ''}) -join "`n"
 # Do not hardcode one provider key prefix. Use standalone opaque values in
 # this explicitly authorized key document; the existing validator, not format,
 # establishes model eligibility before any credential is saved or enabled.
 $keys=@([regex]::Matches($text,'(?m)^\s*([A-Za-z0-9_.=-]{16,512})\s*$') | ForEach-Object {$_.Groups[1].Value} | Select-Object -Unique)
 if($keys.Count -eq 0 -or $keys.Count -gt 10){throw 'Expected between one and ten distinct Gemini credentials; no value printed.'}
 $phase='validation'
 $accepted=$null;$checked=0;$statuses=@()
 foreach($key in $keys){
  $checked++
  $phase='validation-invoke'
  $operation=$validatorType.GetMethod('ValidateAsync',$flags).Invoke($validator,[object[]]@([string]$key,([Threading.CancellationToken]::None).PSObject.BaseObject))
  $phase='validation-await'
  $result=$operation.GetAwaiter().GetResult()
  $status=$result.GetType().GetProperty('Status').GetValue($result).ToString()
  $statuses+=$status
  if($status -ceq 'AuthenticationValidated'){$accepted=$key;break}
 }
 if(!$accepted){throw ('No eligible text-model credential confirmed. Safe statuses: '+($statuses -join ', '))}
 if($Configure){
  $phase='vault-constructor'
  $vaultType=$assembly.GetType('KeyboardWtf.Services.Ai.Backends.PersonalApiVault',$true)
  $vault=$vaultType.GetConstructors($flags)[0].Invoke([object[]]@([string]$vaultPath,$null,$null,$null))
  $phase='vault-view'
  $view=$vaultType.GetMethod('GetView',$flags).Invoke($vault,$null)
  $revision=$view.GetType().GetProperty('Revision').GetValue($view)
  $validated=[Enum]::Parse($validationType,'AuthenticationValidated')
  $phase='vault-add'
  $view=$vaultType.GetMethod('Add',$flags).Invoke($vault,[object[]]@([long]$revision,[string]'Connector API test',[string]$accepted,$validated.PSObject.BaseObject))
  $revision=$view.GetType().GetProperty('Revision').GetValue($view)
  $policyType=$assembly.GetType('KeyboardWtf.Services.Ai.Backends.AiBackendPolicy',$true)
  $policy=[Enum]::Parse($policyType,'PersonalOnly')
  $phase='vault-policy'
  $null=$vaultType.GetMethod('SetPolicy',$flags).Invoke($vault,[object[]]@([long]$revision,$policy.PSObject.BaseObject))
  $configuration=$vaultType.GetMethod('ReadConfiguration',$flags).Invoke($vault,$null)
  if($configuration.GetType().GetProperty('Policy').GetValue($configuration).ToString() -cne 'PersonalOnly'){throw 'Isolated policy did not verify.'}
 }
 [pscustomobject]@{eligibleTextCredential=$true;credentialsChecked=$checked;configured=[bool]$Configure;
  protectedByExistingDpapiVault=[bool]$Configure;policy=if($Configure){'PersonalOnly'}else{'unchanged'};
  generationOrLiveTested=$false;managedAiRequests=0;productionProfileModified=$false} | ConvertTo-Json -Compress
}catch{
 $types=@();$cause=$_.Exception
 for($depth=0;$cause -and $depth -lt 4;$depth++){$types+=$cause.GetType().Name;$cause=$cause.InnerException}
 throw ('Safe credential preparation failed at '+$phase+' ('+($types -join '/')+'). No values or raw exception are logged; existing stores were not overwritten.')
}
finally{$validator.Dispose();$archive.Dispose();$keys=$null;$accepted=$null;$text=$null;$xml=$null}
