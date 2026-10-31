// Actual installed candidate's loopback Settings, no mocks/sign-in/actions/AI.
// Never persist settings responses, credentials, provider IDs or customer bodies.
import { mkdir, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const proofTag=process.argv[2]||'';
if(proofTag&&!/^[a-f0-9]{7,40}-reviewed$/.test(proofTag))throw new Error('Use a source-specific reviewed receipt tag');
const output=resolve(root,'.artifacts/connectors/pc-installation',proofTag);
if(proofTag&&['settings-render.json','installed-connectors.png'].some(file=>existsSync(resolve(output,file))))
 throw new Error('Source-specific proof already exists; do not overwrite an accepted receipt');
await mkdir(output,{recursive:true});
const browser=await chromium.launch({channel:'chrome',headless:true});
const page=await browser.newPage({viewport:{width:1380,height:1000}});
const errors=[];page.on('pageerror',error=>errors.push(error.name));
const started=Date.now(),calls=[];
page.on('response',response=>{const u=new URL(response.url());if(u.hostname==='localhost'&&['/api/settings','/api/connectors','/api/personal-ai/state'].includes(u.pathname))calls.push({path:u.pathname,status:response.status(),elapsedMs:Date.now()-started});});
const checks=[];const check=(ok,name)=>{if(!ok)throw new Error(name);checks.push(name);};
try {
 // Real Settings has intentional live polling; network-idle is not a readiness
 // contract. Wait for the actual controls/state instead of an idle network.
 const response=await page.goto('http://localhost:8090/#connectors',{waitUntil:'domcontentloaded'});
 check(response.status()===200,'Actual installed Settings served');
 await page.locator('#connector-staging-banner').waitFor();
 check((await page.locator('#connector-staging-banner').textContent()).includes('Connector staging'),'Compiled staging label displayed');
 check(await page.getByRole('link',{name:'Connectors',exact:true}).count()===1,'Native sidebar has one Connectors section');
 check(await page.locator('#settings-connectors-panel').isVisible(),'Actual connector panel visible');
 await page.waitForFunction(()=>document.querySelector('#settings-connectors-panel')?.textContent?.trim().length>0);
 check(await page.locator('#startWithWindows').isDisabled(),'Candidate cannot change public startup');
 await page.waitForFunction(()=>document.getElementById('personalPolicy')?.value==='1');
 const policy='PersonalOnly';
 check(await page.locator('#personalPolicy').inputValue()==='1','Actual Settings confirms PersonalOnly');
 check(errors.length===0,'No installed Settings page errors');
 const earlyRows=await page.locator('#settings-connectors-panel .connector-row').count();
 let coldLoadReady=false;
 try {await page.waitForFunction(()=>document.querySelectorAll('#settings-connectors-panel .connector-row').length>0,{},{timeout:15000});coldLoadReady=true;}catch{}
 const beforeRefresh=await page.locator('#settings-connectors-panel .connector-row').count();
 const coldState=await page.evaluate(()=>({hidden:document.hidden,hash:location.hash,rectCount:document.getElementById('settings-connectors-panel').getClientRects().length,
   hasIdentity:document.getElementById('paidAccountIdentity')?.dataset.connectorIdentity?.length===64,
   signInRequired:/sign in/i.test(document.getElementById('settings-connectors-panel')?.textContent||'')}));
 if(!coldLoadReady){
   await page.getByRole('button',{name:'Refresh connector state',exact:true}).click();
   await page.waitForFunction(()=>document.querySelectorAll('#settings-connectors-panel .connector-row').length>0,{},{timeout:15000});
 }
 check(coldLoadReady,'Authenticated catalog loads automatically without manual Refresh');
 // Capture only the connector section, excluding credentials/account billing UI.
 await page.locator('#connectors').screenshot({path:resolve(output,'installed-connectors.png')});
 const state=await page.evaluate(async()=>{
   const settings=await (await fetch('/api/settings',{cache:'no-store'})).json();
   return {signedIn:settings.paidAccount?.signedIn===true,hasIdentity:settings.connectorIdentity?.length===64,
     panelText:document.getElementById('settings-connectors-panel')?.textContent||'',
     catalogRows:document.querySelectorAll('#settings-connectors-panel .connector-row').length,
     connectedRows:document.querySelectorAll('#settings-connectors-panel .connector-symbol.connected').length,
     jarvisShortcut:document.getElementById('hotkeyJarvis')?.value||''};
 });
 check(state.signedIn&&state.hasIdentity,'Actual staging desktop account authenticated');
 check(state.catalogRows===12&&state.connectedRows>0,'Authoritative installed catalog and connected accounts rendered');
 const proof={time:new Date().toISOString(),checks,errors,policy,
   signedIn:state.signedIn,hasIdentity:state.hasIdentity,earlyRows,beforeRefresh,coldLoadReady,coldState,
   catalogRows:state.catalogRows,connectedRows:state.connectedRows,jarvisShortcut:state.jarvisShortcut,
   truthfulSignInRequired:/sign in/i.test(state.panelText),calls,
   authenticInstalledPage:true,oauthOrConnectorActionsExecuted:false,aiCalls:0,
   authenticatedTaskAcceptance:false};
 await writeFile(resolve(output,'settings-render.json'),JSON.stringify(proof,null,2));
 console.log(JSON.stringify(proof));
}catch(error){
 // Retain a failed readiness observation as well as successful source-specific
 // proof. No panel text, account details, settings bodies or tokens are saved.
 await writeFile(resolve(output,`settings-failure-${Date.now()}.json`),JSON.stringify({
   time:new Date().toISOString(),checks,errors,calls,errorCategory:error.name,
   automaticCatalogReadinessFailed:error.message==='Authenticated catalog loads automatically without manual Refresh',
   authenticInstalledPage:true,aiCalls:0,customerContentPersisted:false
 },null,2));
 throw error;
}finally{await page.close();await browser.close();}
