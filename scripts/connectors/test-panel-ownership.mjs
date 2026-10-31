// Shared product panel against isolated in-memory accounts and deferred I/O.
// No real login, Settings state, provider request or financial action.
import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const out = path.join(root, 'output/playwright/connector-panel-ownership');
fs.mkdirSync(out, { recursive:true });
const errors = [], proof = [];
const browser = await chromium.launch({ channel:'chrome', headless:true });
const settingsCss = fs.readFileSync(path.join(root, 'src/Resources/web/settings.html'), 'utf8').match(/<style>([\s\S]*?)<\/style>/)[1];

function fixture(surface, theme) {
  return `<!doctype html><html data-theme="${theme}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    ${surface === 'website' ? '<link rel="stylesheet" href="/styles.css"><link rel="stylesheet" href="/theme.css">' : `<style>${settingsCss}</style>`}
    <link rel="stylesheet" href="/connectors-panel.css"><style>html{--bg:${theme === 'dark' ? '#090909' : '#fff'};--text:${theme === 'dark' ? '#eee' : '#111'};--text-dim:${theme === 'dark' ? '#aaa' : '#666'};--border:${theme === 'dark' ? '#333' : '#ddd'}}body{display:block;height:auto;overflow:auto}.connector-page{margin:20px auto}</style>
    </head><body><main class="connector-page"><p>Isolated connector ownership fixture — NOT real accounts.</p><div id="panel"></div></main><script type="module">
    import {ConnectorPanel} from '/connectors-panel.js';
    const item=(id,name,family=id)=>({id,name,family,icon:id,searchTerms:[id,name],description:'Fixture capability',capabilities:['Read supported fixture metadata'],availability:'available'});
    const catalog=[item('gmail','Gmail','google'),item('googlecalendar','Google Calendar','google'),item('github','GitHub'),...Array.from({length:9},(_,i)=>item('fixture'+i,'Fixture service '+i))];
    let accounts=[{accountId:'a'.repeat(32),family:'google',label:'first@example.test',state:'connected',isDefault:false},{accountId:'b'.repeat(32),family:'google',label:'second@example.test',state:'connected',isDefault:true}];
    const counts={loads:0,disconnects:0,defaults:0,connects:0}, queues=new Map();
    const wait=async name=>{const gate=queues.get(name)?.shift();if(gate)await gate.promise;};
    const panel=new ConnectorPanel(document.getElementById('panel'),{
      assets:'/connector-icons/',load:async()=>{++counts.loads;await wait('load');return {enabled:true,catalog,accounts:structuredClone(accounts)};},
      disconnect:async id=>{++counts.disconnects;await wait('disconnect');accounts=accounts.filter(account=>account.accountId!==id);},
      setDefault:async id=>{++counts.defaults;await wait('default');accounts=accounts.map(account=>({...account,isDefault:account.accountId===id}));},
      connect:async()=>{++counts.connects;await wait('connect');return {state:'connecting',attemptId:'c'.repeat(32),expiresAt:new Date(Date.now()+600000).toISOString()};},
      attempt:async()=>({state:'connecting'}),open:async()=>{}
    });
    const releases=new Map();
    window.fixture={panel,counts,defer:(name,key=name)=>{let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});const gate={promise};queues.set(name,[...(queues.get(name)||[]),gate]);releases.set(key,{resolve,reject});},release:key=>releases.get(key).resolve(),reject:key=>releases.get(key).reject(Error('Fixture action failed.')),
      switchAccount:()=>{panel.reset();accounts=[{accountId:'d'.repeat(32),family:'google',label:'new-owner@example.test',state:'connected',isDefault:false}];return panel.refresh();},
      uncertainDisconnect:failRefresh=>{panel.transport.disconnect=async id=>{++counts.disconnects;accounts=accounts.map(account=>({...account,state:'disconnecting',isDefault:false}));if(failRefresh)panel.transport.load=async()=>{++counts.loads;throw Error('Fixture state lookup unavailable.');};throw Error('Fixture disconnect response unavailable.');};}};
    await panel.refresh();</script></body></html>`;
}

async function openFixture(surface='website', theme='light', width=1100) {
  const context=await browser.newContext({viewport:{width,height:850}}), page=await context.newPage();
  page.on('pageerror',error=>errors.push(error.message));
  await context.route('**/*',route=>{
    const url=new URL(route.request().url());
    if(url.hostname!=='127.0.0.1') throw Error('Unexpected external fixture request.');
    if(url.pathname==='/index') return route.fulfill({contentType:'text/html',body:fixture(surface,theme)});
    const relative=url.pathname.slice(1), file=path.resolve(root,'production/site',relative);
    if(!file.startsWith(path.join(root,'production/site')+path.sep)||!fs.existsSync(file))
      return route.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"/>'});
    return route.fulfill({contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'image/svg+xml',body:fs.readFileSync(file)});
  });
  await page.goto('http://127.0.0.1:4198/index');
  await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();
  return {context,page};
}

try {
  for(const surface of ['website','settings']) for(const theme of ['light','dark']) for(const width of [1100,320]) {
    const {context,page}=await openFixture(surface,theme,width);
    try {
      await page.getByRole('button',{name:'Manage Gmail accounts'}).focus();await page.keyboard.press('Enter');
      assert.equal(await page.evaluate(()=>document.activeElement.dataset.connectorFocus),'control-gmail');
      await page.getByRole('button',{name:'What GitHub can do'}).focus();
      const before=await page.evaluate(()=>{const list=window.fixture.panel.list;list.scrollTop=80;return list.scrollTop;});
      await page.evaluate(()=>window.fixture.panel.refresh());
      assert.equal(await page.evaluate(()=>document.activeElement.id),'connector-info-github');
      assert.equal(await page.evaluate(()=>window.fixture.panel.list.scrollTop),before);
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
      proof.push(`${surface}/${theme}/${width}: keyboard management and refresh preserve focus/scroll; no horizontal overflow.`);
      if(width===320) {
        await page.evaluate(()=>window.fixture.panel.list.scrollTop=0);
        await page.screenshot({path:path.join(out,`${surface}-${theme}-320.png`)});
      }
    } finally {await context.close();}
  }
  for(const operation of ['disconnect','default']) {
    const {context,page}=await openFixture();
    try {
      await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
      await page.evaluate(op=>{window.fixture.defer(op);window.fixture.defer('load');},operation);
      const name=operation==='disconnect'?'Disconnect':'Use by default';
      await page.getByRole('button',{name,exact:true}).first().evaluate(button=>{button.click();button.click();});
      assert.equal(await page.evaluate(op=>window.fixture.counts[op==='disconnect'?'disconnects':'defaults'],operation),1);
      assert.equal(await page.locator('.connector-manage button:not(:disabled)').count(),0);
      assert.equal(await page.getByRole('button',{name:'Manage Google Calendar accounts'}).isDisabled(),true);
      await page.evaluate(op=>window.fixture.release(op),operation);
      await page.waitForFunction(()=>window.fixture.counts.loads===2);
      assert.equal(await page.evaluate(()=>window.fixture.panel.mutating.has('google')),true);
      assert.equal(await page.locator('.connector-manage button:not(:disabled)').count(),0);
      await page.evaluate(()=>window.fixture.release('load'));
      await page.waitForFunction(()=>!window.fixture.panel.mutating.has('google'));
      assert.equal(await page.getByRole('button',{name:'Manage Gmail accounts'}).isDisabled(),false);
      proof.push(`${operation}: duplicate clicks dispatch once; family controls stay disabled until authoritative refresh finishes.`);
    } finally {await context.close();}
  }
  for(const failRefresh of [false,true]) {
    const {context,page}=await openFixture();
    try {
      await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
      await page.evaluate(fail=>{window.fixture.uncertainDisconnect(fail);if(!fail)window.fixture.defer('load');},failRefresh);
      await page.getByRole('button',{name:'Disconnect',exact:true}).first().click();
      await page.waitForFunction(()=>window.fixture.counts.loads===2);
      assert.equal(await page.locator('.connector-symbol.connected').count(),0);
      if(!failRefresh) {
        assert.equal(await page.evaluate(()=>window.fixture.panel.mutating.has('google')),true);
        assert.equal(await page.evaluate(()=>window.fixture.panel.snapshot.accounts[0].state),'unavailable');
        await page.evaluate(()=>window.fixture.release('load'));
      }
      await page.waitForFunction(()=>!window.fixture.panel.mutating.has('google'));
      assert.equal(await page.getByText('Fixture disconnect response unavailable.',{exact:true}).isVisible(),true);
      assert.equal(await page.locator('.connector-symbol.connected').count(),0);
      assert.equal(await page.evaluate(()=>window.fixture.counts.disconnects),1);
      assert.equal(await page.evaluate(()=>window.fixture.panel.snapshot.accounts[0].state),failRefresh?'unavailable':'disconnecting');
      if(failRefresh)assert.ok(await page.getByRole('button',{name:'Refresh',exact:true}).count()>0);
      else {assert.ok(await page.getByRole('button',{name:'Retry disconnect',exact:true}).count()>0);assert.equal(await page.getByRole('button',{name:'Reconnect',exact:true}).count(),0);}
      await page.screenshot({path:path.join(out,`uncertain-disconnect-${failRefresh?'unavailable':'verified-pending'}.png`)});
      proof.push(`Ambiguous disconnect/${failRefresh?'refresh unavailable':'verified pending'}: stale Connected checks clear immediately, one mutation only, authoritative/uncertain state shown.`);
    } finally {await context.close();}
  }
  {
    const {context,page}=await openFixture();
    try {
      await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
      await page.evaluate(()=>{window.fixture.uncertainDisconnect(false);window.fixture.defer('load','old-lookup');});
      await page.getByRole('button',{name:'Disconnect',exact:true}).first().click();
      await page.waitForFunction(()=>window.fixture.counts.loads===2);
      await page.evaluate(()=>window.fixture.switchAccount());
      await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
      await page.evaluate(()=>window.fixture.defer('default','new-default'));
      await page.getByRole('button',{name:'Use by default',exact:true}).click();
      await page.evaluate(()=>window.fixture.release('old-lookup'));
      await page.waitForTimeout(30);
      assert.equal(await page.evaluate(()=>window.fixture.panel.mutating.has('google')),true);
      assert.equal(await page.getByText('Fixture disconnect response unavailable.',{exact:true}).count(),0);
      assert.equal(await page.getByText('new-owner@example.test',{exact:false}).count(),1);
      await page.evaluate(()=>window.fixture.release('new-default'));
      await page.waitForFunction(()=>!window.fixture.panel.mutating.has('google'));
      proof.push('Account switch during failed-action reconciliation rejects the old lookup/error and cannot release a newer mutation owner.');
    } finally {await context.close();}
  }
  {
    const {context,page}=await openFixture();
    try {
      await page.evaluate(()=>window.fixture.defer('connect'));
      await page.getByRole('button',{name:'Connect GitHub',exact:true}).evaluate(button=>button.click());
      await page.waitForFunction(()=>window.fixture.counts.connects===1);
      assert.equal(await page.getByRole('button',{name:'Connect GitHub',exact:true}).isDisabled(),true);
      assert.equal(await page.locator('.connector-row').filter({hasText:'GitHub'}).locator('.connector-badge').textContent(),'Connecting…');
      await page.evaluate(()=>window.fixture.release('connect'));
      await page.waitForFunction(()=>window.fixture.panel.pending.has('github'));
      assert.equal(await page.evaluate(()=>window.fixture.counts.connects),1);
      proof.push('Connect shows its actual pending state during creation and disables duplicate clicks.');
    } finally {await context.close();}
  }
  {
    const {context,page}=await openFixture();
    try {
      await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
      await page.evaluate(()=>window.fixture.defer('default','old'));
      await page.getByRole('button',{name:'Use by default',exact:true}).click();
      await page.evaluate(()=>window.fixture.switchAccount());
      await page.getByRole('button',{name:'Manage Gmail accounts'}).click();
      await page.evaluate(()=>window.fixture.defer('default','new'));
      await page.getByRole('button',{name:'Use by default',exact:true}).click();
      const before=await page.evaluate(()=>window.fixture.counts.loads);
      await page.evaluate(()=>window.fixture.release('old'));
      await page.waitForTimeout(30);
      assert.equal(await page.evaluate(()=>window.fixture.counts.loads),before);
      assert.equal(await page.evaluate(()=>window.fixture.panel.mutating.has('google')),true);
      assert.equal(await page.getByRole('button',{name:'Manage Gmail accounts'}).isDisabled(),true);
      await page.evaluate(()=>window.fixture.reject('new'));
      await page.getByText('Fixture action failed.',{exact:true}).waitFor();
      assert.equal(await page.evaluate(()=>window.fixture.panel.mutating.has('google')),false);
      assert.equal(await page.getByRole('button',{name:'Manage Gmail accounts'}).isDisabled(),false);
      assert.equal(await page.getByText('first@example.test',{exact:false}).count(),0);
      proof.push('Old-generation mutation completion cannot refresh or release a newer account owner; current failure releases controls with truthful error.');
    } finally {await context.close();}
  }
  assert.deepEqual(errors,[]);
  const receipt={testedAt:new Date().toISOString(),scope:'Actual shared product module/CSS with isolated in-memory accounts; NOT installed, OAuth or provider acceptance',passed:proof.length,proof,errors};
  fs.writeFileSync(path.join(out,'proof.json'),JSON.stringify(receipt,null,2));
  console.log(JSON.stringify({passed:proof.length,errors:errors.length,output:out}));
} finally {await browser.close();}
