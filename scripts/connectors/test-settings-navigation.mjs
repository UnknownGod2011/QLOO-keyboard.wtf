// Existing Settings navigation source + actual connector module, isolated API.
// No installed settings mutation, credentials, provider actions or AI requests.
import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const source=await readFile(resolve(root,'src/Resources/web/settings.html'),'utf8');
const start=source.indexOf('(function () {',source.indexOf('// ── Scroll-spy'));
const end=source.indexOf('})();',start)+5;
assert.ok(start>0&&end>start,'Actual Settings navigation owner found');
const navigation=source.slice(start,end);
const catalog=await (await fetch('http://127.0.0.1:4196/__fixture-catalog')).json();
const browser=await chromium.launch({channel:'chrome',headless:true});
const proofs=[],errors=[];
try {
 for(const initialHash of ['account-billing','connectors']) {
  const context=await browser.newContext({viewport:{width:1200,height:900}}),page=await context.newPage();
  let loads=0,connected=true;
  page.on('pageerror',error=>errors.push(error.message));
  await context.route('http://127.0.0.1:4196/**',async route=>{
   const url=new URL(route.request().url());
   if(url.pathname==='/__settings-navigation')return route.fulfill({contentType:'text/html',body:`<!doctype html><html><head><style>.main{height:700px;overflow:auto}section{min-height:700px}</style><link rel="stylesheet" href="/connectors-panel.css"></head><body><nav id="sidebar-nav"><a href="#account-billing">Account & Billing</a><a href="#connectors">Connectors</a></nav><div class="main" id="main-scroll"><section id="account-billing"><span id="paidAccountIdentity" data-connector-identity="${'a'.repeat(64)}">Same owner</span></section><section id="connectors"><div id="settings-connectors-panel"></div></section></div><script>${navigation}</script><script type="module" src="/connectors-settings.js"></script></body></html>`});
   const modules=new Map([['/connectors-settings.js','connectors-settings.js'],['/connectors/connectors-panel.js','connectors-panel.js']]);
   if(modules.has(url.pathname))return route.fulfill({contentType:'text/javascript',body:await readFile(resolve(root,'production/site',modules.get(url.pathname)),'utf8')});
   if(/^\/connectors\/icons\/[a-z][a-z0-9_-]+\.svg$/.test(url.pathname))return route.fulfill({contentType:'image/svg+xml',body:await readFile(resolve(root,'production/site/connector-icons',basename(url.pathname)),'utf8')});
   if(url.pathname==='/api/settings')return route.fulfill({json:{connectorIdentity:'a'.repeat(64)}});
   if(url.pathname==='/api/connectors'){++loads;return route.fulfill({json:{enabled:true,catalog,accounts:connected?[{accountId:'b'.repeat(32),family:'google',label:'navigation-fixture@example.test',state:'connected',isDefault:true}]:[]}});}
   return route.continue();
  });
  try {
   await page.goto(`http://127.0.0.1:4196/__settings-navigation#${initialHash}`);
   if(initialHash==='account-billing'){
    await page.waitForFunction(()=>document.getElementById('settings-connectors-panel')?.classList.contains('connector-panel'));
    assert.equal(loads,0,'No connector lookup for settled account view');
    await page.getByRole('link',{name:'Connectors',exact:true}).click();
   }
   await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor({timeout:3000});
   assert.equal(loads,1,'One lookup on initial connector navigation');
   assert.equal(await page.locator('.connector-row').count(),12);
   await page.getByRole('link',{name:'Account & Billing',exact:true}).click();
   connected=false; // Represents server-side disconnect on another device.
   await page.getByRole('link',{name:'Connectors',exact:true}).click();
   await page.getByRole('button',{name:'Connect Gmail',exact:true}).waitFor({timeout:3000});
   assert.equal(loads,2,'One fresh same-owner lookup on connector re-entry');
   assert.equal(await page.locator('.connector-symbol.connected').count(),0);
   // A closed page's named navigation listener must not survive destruction.
   await page.evaluate(()=>window.dispatchEvent(new PageTransitionEvent('pagehide',{persisted:false})));
   await page.getByRole('link',{name:'Account & Billing',exact:true}).click();
   await page.getByRole('link',{name:'Connectors',exact:true}).click();
   await page.waitForTimeout(100);
   assert.equal(loads,2,'Destroyed connector module performs no navigation work');
   proofs.push(`${initialHash}: native pushState navigation loads once; same-owner re-entry reconciles once; destroy clears listener.`);
  }finally{await context.close();}
 }
 assert.deepEqual(errors,[]);
 const output=resolve(root,'.artifacts/connectors/ui');await mkdir(output,{recursive:true});
 await writeFile(resolve(output,'settings-navigation.json'),JSON.stringify({time:new Date().toISOString(),fixtureOnly:true,proofs,errors},null,2));
 console.log(JSON.stringify({passed:proofs.length,errors:errors.length}));
}finally{await browser.close();}
