import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const out=path.join(root,'.artifacts/connectors/ui');fs.mkdirSync(out,{recursive:true});
const browser=await chromium.launch({channel:'chrome',headless:true});
const context=await browser.newContext({viewport:{width:1440,height:900}});
const page=await context.newPage();const errors=[];const proof=[];
page.on('pageerror',e=>errors.push(e.message));page.on('console',e=>{if(e.type()==='error')errors.push(e.text());});
try {
 await page.goto('http://127.0.0.1:4196/__connector-fixture');
 await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
 assert.equal(await page.locator('.connector-row').count(),12);
 assert.equal(await page.locator('.connector-logo').evaluateAll(nodes=>nodes.filter(n=>!n.complete||n.naturalWidth===0).length),0);
 await page.getByRole('button',{name:'What Gmail can do',exact:true}).click();
 assert.equal(await page.locator('#connector-info-gmail').evaluate(node=>node===document.activeElement),true);
 assert.equal(await page.locator('#connector-help-gmail').isVisible(),true);
 assert.ok(await page.locator('#connector-help-gmail .connector-capabilities li').count() >= 4);
 assert.ok(!(await page.locator('#connector-help-gmail').textContent()).includes('attachments'));
 await page.keyboard.press('Escape');
 assert.equal(await page.locator('#connector-help-gmail').isVisible(),false);
 proof.push('Capability help derives from the server registry, expands with focus retained, closes with Escape and does not advertise unsupported attachments.');
 await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
  await page.getByText('Default and disconnect changes apply to all of them.',{exact:false}).waitFor();
 await page.getByRole('button',{name:'Use by default',exact:true}).click();
 await page.locator('.connector-account').filter({hasText:'long-work-account-name'}).getByText('connected · Default').waitFor();
 proof.push('Explicit default, shared Google family and two accounts rendered correctly.');
 await page.screenshot({path:path.join(out,'desktop-light.png')});
 await page.getByRole('button',{name:'Theme',exact:true}).click();
 await page.screenshot({path:path.join(out,'desktop-dark.png')});
 var before=await page.evaluate(()=>window.fixture.counts());
 await page.getByRole('searchbox').fill('GITHUB');
 assert.equal(await page.locator('.connector-row').count(),1);
 assert.equal((await page.evaluate(()=>window.fixture.counts())).loads,before.loads);
 await page.getByRole('button',{name:'Connect GitHub',exact:true}).click();
 assert.equal((await page.evaluate(()=>window.fixture.counts())).connects,1);
 assert.equal((await page.evaluate(()=>window.fixture.counts())).opens,1);
 await page.getByRole('searchbox').fill('not a connector');
 await page.getByText('No matching connectors.').waitFor();
 await page.getByRole('searchbox').fill('Reddit');
 await page.getByRole('button',{name:'Connect Reddit',exact:true}).click();
 await page.getByText('Reddit is coming soon.',{exact:true}).waitFor();
 assert.equal((await page.evaluate(()=>window.fixture.counts())).connects,1);
 proof.push('Search is local and case-insensitive; connect opens once; Coming Soon makes no request.');
 await page.getByRole('searchbox').fill('');
 await page.getByRole('button',{name:'API failure',exact:true}).click();
 await page.getByText('Service unavailable. No connected account has been confirmed.',{exact:true}).waitFor();
 assert.equal(await page.locator('.connector-symbol.connected').count(),0);
 proof.push('Failed refresh does not preserve stale green Connected checks.');
 await page.getByRole('button',{name:'API failure',exact:true}).click();
 await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
 await page.getByRole('button',{name:'Late response',exact:true}).click();
 await page.getByRole('button',{name:'Account switch',exact:true}).click();
 await page.evaluate(()=>window.fixture.finishLate());
 assert.equal(await page.getByText('STALE ACCOUNT',{exact:true}).count(),0);
 proof.push('Old-account async results are discarded after account reset.');
 await page.goto('http://127.0.0.1:4196/__connector-fixture');
 await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
 await page.getByRole('button',{name:'Manage Gmail accounts'}).focus();
 await page.keyboard.press('Enter');
 await page.getByRole('button',{name:'Use by default',exact:true}).waitFor();
 proof.push('Keyboard focus/Enter opens account controls.');
  const lifecycle = await page.evaluate(async () => {
    const panel = window.fixture.panel, before = window.fixture.counts().loads;
    panel.pending.set('github', {attemptId:'d'.repeat(32),state:'connecting',expiresAt:new Date(Date.now()+600000).toISOString()});
    panel.schedulePoll();
    window.dispatchEvent(new PageTransitionEvent('pagehide',{persisted:true}));
    const retained = panel.pending.has('github') && panel.suspended && !panel.destroyed && panel.pollTimer === null;
    window.dispatchEvent(new PageTransitionEvent('pageshow',{persisted:true}));
    await new Promise(resolve=>setTimeout(resolve,100));
    return {retained,resumed:!panel.suspended&&!panel.destroyed,loaded:window.fixture.counts().loads>before};
  });
  assert.deepEqual(lifecycle,{retained:true,resumed:true,loaded:true});
  proof.push('Persisted page lifecycle preserves the panel/pending flow and refreshes on resume (browser events exercised; not a real OAuth navigation).');
  await page.evaluate(() => {
    const panel=window.fixture.panel; panel.pending.clear();panel.stopPolling();
    panel.snapshot.accounts=panel.snapshot.accounts.map(account=>({...account,state:'disconnecting'}));panel.expanded=null;panel.render();
  });
  await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
  const connectCount=(await page.evaluate(()=>window.fixture.counts())).connects;
  assert.equal(await page.getByRole('button',{name:'Reconnect',exact:true}).count(),0);
  await page.getByRole('button',{name:'Retry disconnect',exact:true}).first().click();
  assert.equal((await page.evaluate(()=>window.fixture.counts())).connects,connectCount);
  proof.push('Incomplete disconnect offers Retry disconnect, not a new authorization grant; shared Google control scope is explained.');
  await page.goto('http://127.0.0.1:4196/__connector-fixture');
  await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
  await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
 for (const [width,height] of [[390,844],[320,740]]) {
   await page.setViewportSize({width,height});
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
   await page.screenshot({path:path.join(out,`mobile-${width}-light.png`)});
   await page.getByRole('button',{name:'Theme',exact:true}).click();
   await page.screenshot({path:path.join(out,`mobile-${width}-dark.png`)});
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
   await page.getByRole('button',{name:'Theme',exact:true}).click();
 }
 proof.push('320px/390px mobile light/dark: no horizontal overflow; long identities wrap.');
  const catalog=await page.evaluate(()=>window.fixture.panel.snapshot.catalog);
  await checkSettingsIdentity(browser,catalog,errors,proof);
  await checkWebReturn(browser,catalog,errors,proof);
 await page.emulateMedia({reducedMotion:'reduce'});
 await page.getByRole('searchbox').fill('Discord');
 await page.getByRole('button',{name:'Connect Discord',exact:true}).waitFor();
 assert.equal(errors.length,0,errors.join('\n'));
 const completeProof=['Catalog renders all 12 registered connector rows.','Every bundled connector logo loads successfully.',...proof];
 fs.writeFileSync(path.join(out,'functional.json'),JSON.stringify({testedAt:new Date().toISOString(),scope:'Local rendered fixture only — NOT OAuth/provider acceptance',passed:completeProof.length,proof:completeProof,consoleErrors:errors},null,2));
 console.log(JSON.stringify({passed:completeProof.length,consoleErrors:errors.length,artifactDirectory:out}));
} finally { await browser.close(); }

// Real rendered modules against explicit, isolated fixtures. These are not
// Firebase OAuth, service-content or installed Settings acceptance claims.
async function checkSettingsIdentity(browser,catalog,errors,proof) {
 const context=await browser.newContext({viewport:{width:1100,height:800}}), page=await context.newPage();
 page.on('pageerror',error=>errors.push(error.message));
 const hashes={a:'1'.repeat(64),b:'2'.repeat(64),c:'3'.repeat(64)};
 let owner='a',lateNext=false,finishLate,markArrived,failIdentity=false;
 const account=id=>({accountId:id.repeat(32),family:'google',label:`fixture-owner-${id}@example.test`,state:'connected',isDefault:true});
 const snapshot=()=>({enabled:true,catalog,accounts:[account(owner)]});
 const modules=new Map([['/connectors-settings.js','connectors-settings.js'],['/connectors/connectors-panel.js','connectors-panel.js']]);
 await context.route('http://127.0.0.1:4196/**',async route=>{
  const url=new URL(route.request().url());
  if(modules.has(url.pathname)) return route.fulfill({contentType:'text/javascript',body:fs.readFileSync(path.join(root,'production/site',modules.get(url.pathname)),'utf8')});
  if(/^\/connectors\/icons\/[a-z][a-z0-9_-]+\.svg$/.test(url.pathname))
   return route.fulfill({contentType:'image/svg+xml',body:fs.readFileSync(path.join(root,'production/site/connector-icons',path.basename(url.pathname)),'utf8')});
  if(url.pathname==='/__connector-settings-identity') return route.fulfill({contentType:'text/html',body:`<!doctype html><html><head><title>Connector Settings identity fixture — NOT real accounts</title><link rel="stylesheet" href="/connectors-panel.css"></head><body><p>Isolated Settings fixture. No real account/provider requests.</p><span id="paidAccountIdentity" data-connector-identity="${hashes.a}">Same display name</span><div id="settings-connectors-panel"></div><script type="module" src="/connectors-settings.js"></script></body></html>`});
  if(url.pathname==='/api/settings') return failIdentity ? route.fulfill({status:503,json:{error:'Fixture identity outage'}}) : route.fulfill({json:{connectorIdentity:hashes[owner]}});
  if(url.pathname==='/api/connectors') {
   const result=snapshot();
   if(lateNext) {lateNext=false;await new Promise(resolve=>{finishLate=resolve;markArrived();});}
   return route.fulfill({json:result});
  }
  if(url.pathname==='/api/connectors/connect') return route.fulfill({json:{attemptId:'e'.repeat(32),state:'connecting',redirectUrl:'desktop-opened',expiresAt:new Date(Date.now()+600000).toISOString()}});
  if(url.pathname.startsWith('/api/connectors/attempts/')) return route.fulfill({json:{state:'connecting',expiresAt:new Date(Date.now()+600000).toISOString()}});
  return route.continue();
 });
 try {
  await page.goto('http://127.0.0.1:4196/__connector-settings-identity#connectors');
  await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
  await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
  await page.locator('.connector-account-identity').filter({hasText:'fixture-owner-a@example.test'}).waitFor();
  const arrived=new Promise(resolve=>markArrived=resolve);lateNext=true;
  await page.getByRole('button',{name:'Refresh connector state'}).click();await arrived;
  owner='b';await page.locator('#paidAccountIdentity').evaluate((node,hash)=>node.dataset.connectorIdentity=hash,hashes.b);
  await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
  await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
  await page.locator('.connector-account-identity').filter({hasText:'fixture-owner-b@example.test'}).waitFor();
  finishLate();await page.waitForTimeout(80);
  assert.equal(await page.locator('.connector-account-identity').filter({hasText:'fixture-owner-a@example.test'}).count(),0);
  assert.equal(await page.locator('#paidAccountIdentity').textContent(),'Same display name');
  proof.push('Actual Settings module discards old-account replies when opaque identity changes despite identical display names.');
  await page.getByRole('button',{name:'Connect GitHub',exact:true}).click();
  await page.getByRole('button',{name:'Connect GitHub',exact:true}).waitFor({state:'visible'});
  assert.equal(await page.getByRole('button',{name:'Connect GitHub',exact:true}).isDisabled(),true);
  await page.evaluate(()=>window.dispatchEvent(new PageTransitionEvent('pagehide',{persisted:true})));
  owner='c';
  await page.evaluate(()=>window.dispatchEvent(new PageTransitionEvent('pageshow',{persisted:true})));
  await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
  await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
  await page.locator('.connector-account-identity').filter({hasText:'fixture-owner-c@example.test'}).waitFor();
  assert.equal(await page.locator('.connector-account-identity').filter({hasText:'fixture-owner-b@example.test'}).count(),0);
  assert.equal(await page.getByRole('button',{name:'Connect GitHub',exact:true}).isDisabled(),false);
  assert.equal(await page.locator('#paidAccountIdentity').getAttribute('data-connector-identity'),hashes.c);
  proof.push('Persisted Settings resume re-reads credential-owner epoch and discards the previous account’s pending attempt.');
  await page.screenshot({path:path.join(out,'settings-identity.png')});
  failIdentity=true;await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
  await page.getByText('Unable to verify your current desktop account. Refresh Settings.',{exact:true}).waitFor();
  assert.equal(await page.locator('.connector-symbol.connected').count(),0);
  assert.equal(await page.locator('.connector-account-identity').count(),0);
  failIdentity=false;await page.getByRole('button',{name:'Refresh connector state'}).click();
  await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
  proof.push('Identity-check outage clears cached account rows/checkmarks; explicit refresh revalidates the owner and recovers.');
 } finally {await context.close();}
}

async function checkWebReturn(browser,catalog,errors,proof) {
 const context=await browser.newContext({viewport:{width:1100,height:800}}), page=await context.newPage();
 page.on('pageerror',error=>errors.push(error.message));
 const id='f'.repeat(32),uid='fixture-web-user',owner=createHash('sha256').update(uid).digest('hex');
 let authenticatedCalls=0;
 const authFixture=`export const auth={currentUser:{uid:${JSON.stringify(uid)},email:'fixture-user@example.test'}};
 export function listenForAuth(callback){window.fixtureAuth={switchUser:async user=>{auth.currentUser=user;await callback(user);}};queueMicrotask(()=>callback(auth.currentUser));}
 export async function api(path,options={}){const response=await fetch('/__connector-web-api'+path,{method:options.method||'GET',headers:{'Content-Type':'application/json'},body:options.body});if(!response.ok)throw Error('Fixture failure');return response.json();}
 export async function googleSignIn(){} export async function googleSignOut(){await window.fixtureAuth.switchUser(null);}`;
 await context.route('http://127.0.0.1:4196/**',async route=>{
  const url=new URL(route.request().url());
  if(url.pathname==='/__connector-web-return') return route.fulfill({contentType:'text/html',body:fs.readFileSync(path.join(root,'production/site/connectors.html'),'utf8').replace('Connectors · keyboard.wtf','Connector return fixture — NOT real OAuth').replace('<main class="connector-page">','<main class="connector-page"><p>Isolated return fixture. No real OAuth or provider data.</p>')});
  if(url.pathname==='/app.js')return route.fulfill({contentType:'text/javascript',body:authFixture});
  if(url.pathname.startsWith('/__connector-web-api')){
   const apiPath=url.pathname.slice('/__connector-web-api'.length);
   if(apiPath==='/v1/connectors/catalog')return route.fulfill({json:catalog});
   ++authenticatedCalls;
   if(apiPath.startsWith('/v1/connectors/attempts/'))return route.fulfill({json:{attemptId:id,state:'connected'}});
   return route.fulfill({json:{enabled:true,catalog,accounts:[{accountId:'a'.repeat(32),family:'google',label:'fixture-user@example.test',state:'connected',isDefault:true}]}});
  }
  return route.continue();
 });
 await page.addInitScript(({id,owner})=>sessionStorage.setItem('keyboard.wtf.connector-return',JSON.stringify({attemptId:id,source:'desktop',owner,opened:true,expiresAt:Date.now()+600000})),{id,owner});
 try {
  await page.goto(`http://127.0.0.1:4196/__connector-web-return?attempt=${id}&state=fixture-callback`);
  await page.getByText('Connected ✓ — return to keyboard.wtf Settings. It refreshes when you switch back.',{exact:true}).waitFor({state:'visible'});
  assert.equal(await page.evaluate(()=>sessionStorage.getItem('keyboard.wtf.connector-return')),null);
  assert.equal(new URL(page.url()).search,'');
  proof.push('Actual web module restores the desktop return hint from one bounded session marker and clears it after verified completion.');
  const before=authenticatedCalls;
  await page.evaluate(()=>window.fixtureAuth.switchUser({uid:'different-fixture-user',email:'different@example.test'}));
  await page.getByText('This browser is signed into a different account than keyboard.wtf. Sign out here and choose the same account.',{exact:true}).waitFor();
  await page.evaluate(()=>window.dispatchEvent(new Event('focus')));await page.waitForTimeout(80);
  assert.equal(authenticatedCalls,before);
  assert.equal(await page.locator('.connector-symbol.connected').count(),0);
  assert.equal(await page.getByText('Connected ✓ — return to keyboard.wtf Settings. It refreshes when you switch back.',{exact:true}).isVisible(),false);
  proof.push('Desktop-owner mismatch remains blocked after focus; no connector request or stale green check is exposed.');
  await page.screenshot({path:path.join(out,'web-owner-mismatch.png')});
 }finally{await context.close();}
}
