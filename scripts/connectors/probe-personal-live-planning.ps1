[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
# Authorized diagnostic tooling only: reuse the actual Personal adapter/vault
# and exact broker schemas. No app/microphone/player, Managed client, tool
# execution, key rotation, retry or provider/customer payload logging.
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$candidateVault=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'keyboard.wtf-connectors-staging-candidate/personal-api-v1.dat'
if(!(Test-Path -LiteralPath $candidateVault)){throw 'Candidate Personal vault missing. No other profile is used.'}
$code=@'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
public static class ConnectorPersonalLiveProbe {
 static readonly BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static readonly BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 public static async Task<string> RunAsync(string assemblyPath,string vaultPath) {
  var p=new Dictionary<string,object>{["time"]=DateTimeOffset.UtcNow.ToString("o"),
   ["scope"]="Personal Live adapter and broker proposal ONLY; not installed mic/audio/UI or connector execution",
   ["owner"]="PersonalOnly",["physicalAttempts"]=0,["inputTurns"]=0,["connectorToolCallsExecuted"]=0,
   ["managedRequests"]=0,["modelCredits"]=0,["microphoneStarted"]=false,["setupComplete"]=false,
   ["connectorResolveProposed"]=false,["gmailSelected"]=false,["outcome"]="not_started",
   ["receivedFrames"]=0,["receivedBytes"]=0,["personalProviderUsagePossible"]=false};
  var timer=Stopwatch.StartNew();string phase="vault";IDisposable lease=null,transport=null;ClientWebSocket socket=null;
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
  try {
   var a=Assembly.LoadFrom(assemblyPath);
   var v=a.GetType("KeyboardWtf.Services.Ai.Backends.PersonalApiVault",true);
   var vault=v.GetConstructors(I).Single().Invoke(new object[]{vaultPath,null,null,null});
   var config=v.GetMethod("ReadConfiguration",I).Invoke(vault,null);
   if(config.GetType().GetProperty("Policy").GetValue(config).ToString()!="PersonalOnly")throw new InvalidOperationException();
   lease=(IDisposable)v.GetMethod("Acquire",I).Invoke(vault,new[]{config});
   var t=a.GetType("KeyboardWtf.Services.Ai.Backends.PersonalGeminiLiveTransport",true);
   transport=(IDisposable)t.GetConstructors(I).Single().Invoke(new object[]{vault,null});
   var model=(string)a.GetType("KeyboardWtf.Services.Ai.Backends.PersonalGeminiModels",true).GetField("Live",S).GetRawConstantValue();
   phase="schema";
   var tools=a.GetType("KeyboardWtf.Services.GeminiLiveConversationService",true).GetMethod("BuildTools",S).Invoke(null,null);
   var catalog=JsonSerializer.SerializeToElement(tools);
   var brokers=catalog[0].GetProperty("functionDeclarations").EnumerateArray()
    .Where(x=>x.GetProperty("name").GetString().StartsWith("connector_",StringComparison.Ordinal)).Select(x=>x.Clone()).ToArray();
   var schema=JsonSerializer.Serialize(brokers);
   if(brokers.Length!=3||schema.Length>=2400)throw new InvalidOperationException();
   p["model"]=model;p["connectorToolCount"]=brokers.Length;p["connectorSchemaCharacters"]=schema.Length;
   phase="prepare";
   var original=new {setup=new {model="models/"+model,
    generationConfig=new {responseModalities=new[]{"AUDIO"},speechConfig=new {voiceConfig=new {prebuiltVoiceConfig=new {voiceName="Kore"}}}},
    systemInstruction=new {parts=new[]{new {text="Use the provided connected-service broker for requests about connected services. Do not claim completed actions without results."}}},
    outputAudioTranscription=new {},tools=new[]{new {functionDeclarations=brokers}}}};
   var prepared=t.GetMethod("PrepareSetup",S).Invoke(null,new object[]{original});
   using(var setupDeadline=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token)) {
    setupDeadline.CancelAfter(TimeSpan.FromSeconds(12));phase="connect";p["physicalAttempts"]=1;
    socket=await (Task<ClientWebSocket>)t.GetMethod("ConnectAsync",I).Invoke(transport,new object[]{lease,model,setupDeadline.Token});
    phase="setup";await Send(socket,JsonSerializer.Serialize(prepared),setupDeadline.Token);
    while(true){var msg=await Read(socket,setupDeadline.Token,p);if(msg==null)throw new IOException();
     if(msg.Value.TryGetProperty("setupComplete",out _)){p["setupComplete"]=true;p["setupMs"]=timer.ElapsedMilliseconds;break;}}
   }
   phase="proposal";
   await Send(socket,JsonSerializer.Serialize(new {clientContent=new {turns=new[]{new {role="user",parts=new[]{new {text="Find recent invoices in my connected Gmail."}}}},turnComplete=true}}),deadline.Token);
   p["inputTurns"]=1;p["personalProviderUsagePossible"]=true;
   while(true){var msg=await Read(socket,deadline.Token,p);if(msg==null){p["outcome"]="provider_closed";break;}
    if(msg.Value.TryGetProperty("toolCall",out var call)) {
     var proposals=call.GetProperty("functionCalls").EnumerateArray().Where(x=>x.GetProperty("name").GetString()=="connector_resolve").ToArray();
     bool resolved=proposals.Length==1;
     bool gmail=resolved&&proposals[0].GetProperty("args").TryGetProperty("connector_id",out var id)&&id.GetString()=="gmail";
     p["connectorResolveProposed"]=resolved;p["gmailSelected"]=gmail;p["proposalMs"]=timer.ElapsedMilliseconds;
     p["outcome"]=resolved&&gmail?"broker_proposal_verified":"unexpected_tool_proposal";break;
    }
    if(msg.Value.TryGetProperty("serverContent",out var content)&&content.TryGetProperty("turnComplete",out var done)&&done.GetBoolean()){
     p["outcome"]="completed_without_broker";break;}
   }
  }catch(Exception e){while(e is TargetInvocationException&&e.InnerException!=null)e=e.InnerException;
   p["outcome"]=e is OperationCanceledException?"deadline":"canary_failed";p["failurePhase"]=phase;p["failureType"]=e.GetType().Name;
  }finally{socket?.Abort();socket?.Dispose();transport?.Dispose();lease?.Dispose();p["elapsedMs"]=timer.ElapsedMilliseconds;}
  return JsonSerializer.Serialize(p);
 }
 static Task Send(ClientWebSocket s,string json,CancellationToken ct)=>s.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),WebSocketMessageType.Text,true,ct);
 static async Task<JsonElement?> Read(ClientWebSocket s,CancellationToken ct,Dictionary<string,object> p) {
  var buffer=new byte[8192];using var body=new MemoryStream();WebSocketReceiveResult r;
  do {r=await s.ReceiveAsync(new ArraySegment<byte>(buffer),ct);
   if(r.MessageType==WebSocketMessageType.Close){p["providerCloseCode"]=(int?)r.CloseStatus;return null;}
   // The production receiver decodes JSON carried in Text or Binary frames.
   // Preserve that behavior while adding only diagnostic byte bounds here.
   if((r.MessageType!=WebSocketMessageType.Text&&r.MessageType!=WebSocketMessageType.Binary)||body.Length+r.Count>262144)throw new InvalidDataException();
   p["receivedBytes"]=(int)p["receivedBytes"]+r.Count;if((int)p["receivedBytes"]>2097152)throw new InvalidDataException();
   body.Write(buffer,0,r.Count);
  }while(!r.EndOfMessage);
  p["receivedFrames"]=(int)p["receivedFrames"]+1;
  using var doc=JsonDocument.Parse(body.ToArray());return doc.RootElement.Clone();
 }
}
'@
Add-Type -TypeDefinition $code
$result=[ConnectorPersonalLiveProbe]::RunAsync((Resolve-Path -LiteralPath $AssemblyPath).Path,$candidateVault).GetAwaiter().GetResult()
$proof=$result|ConvertFrom-Json
$output=Join-Path $root '.artifacts/connectors/hosted/personal-live-planning.json'
$null=New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force
$proof|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $output -Encoding utf8
$proof|ConvertTo-Json -Depth 6
if($proof.outcome -cne 'broker_proposal_verified'){exit 1}
