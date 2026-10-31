// Real product CSS and wrapper, isolated auth/API fixtures. No credentials,
// provider actions, application settings writes or claim of OAuth acceptance.
import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const hosted=process.argv.includes('--hosted');
const out=path.join(root,'output/playwright',hosted?'connectors-hosted-theme':'connectors-theme');
fs.mkdirSync(out,{recursive:true});
const browser=await chromium.launch({channel:'chrome',headless:true});
const errors=[],proof=[];
try {
 for(const surface of hosted?['website']:['website','settings']) {
  const context=await browser.newContext({viewport:{width:1100,height:850}});
  const page=await context.newPage();
  page.on('pageerror',error=>errors.push(error.message));
  if(!hosted)await context.route('http://127.0.0.1:4196/app.js',route=>route.fulfill({contentType:'text/javascript',body:`
   const catalog=await fetch('/__fixture-catalog').then(r=>r.json());
   export const auth={currentUser:{uid:'fixture-user',email:'fixture@example.test'}};
   export function listenForAuth(fn){queueMicrotask(()=>fn(auth.currentUser));}
   export async function googleSignIn(){} export async function googleSignOut(){}
   export async function api(url){return url.endsWith('/catalog')?catalog:{enabled:true,catalog,accounts:[{accountId:'a'.repeat(32),family:'google',label:'fixture@example.test',state:'connected',isDefault:true}]};}
  `}));
  if(surface==='website') {
   await page.goto(hosted?'https://keyboard-wtf-connectors-staging.vercel.app/connectors':'http://127.0.0.1:4196/connectors');
  } else {
   const source=fs.readFileSync(path.join(root,'src/Resources/web/settings.html'),'utf8');
   const css=source.match(/<style>([\s\S]*?)<\/style>/)[1];
   await context.route('http://127.0.0.1:4196/__theme-settings',route=>route.fulfill({contentType:'text/html',body:`<!doctype html><html><head><style>${css}</style><link rel="stylesheet" href="/connectors-panel.css"></head><body><main><section><h1>Connectors</h1><div id="panel"></div></section></main><script type="module">import {ConnectorPanel} from '/connectors-panel.js'; import {api} from '/app.js';const panel=new ConnectorPanel(document.querySelector('#panel'),{load:()=>api('/'),assets:'/connector-icons/'});await panel.refresh();</script></body></html>`}));
   await page.goto('http://127.0.0.1:4196/__theme-settings');
  }
  if(hosted)await page.getByText('Gmail',{exact:true}).waitFor();
  else {await page.getByRole('button',{name:'Manage Gmail accounts'}).waitFor();await page.getByRole('button',{name:'Manage Gmail accounts'}).click();}
  // This is a visual state test, not a refresh/auth action. Pointer up still
  // exercises :active, but must not create an unintended real API request.
  await page.evaluate(()=>document.addEventListener('click',event=>{if(event.target.closest('.connector-refresh')){event.preventDefault();event.stopImmediatePropagation();}},true));
  for(const theme of ['light','dark']) {
   await page.mouse.move(0,0);
   await page.evaluate(({theme,surface})=>{
    document.documentElement.dataset.theme=theme;
    if(surface==='settings') {
     // Settings currently ships light tokens only; exercise its native token
     // contract in dark as well, without changing Settings or adding a theme.
     const tokens=theme==='dark'?{'--bg':'#090909','--text':'#eeeeee','--text-dim':'#aaaaaa','--border':'#333333'}:{'--bg':'#ffffff','--text':'#0a0a0a','--text-dim':'#6b6b6b','--border':'#e5e5e5'};
     for(const [name,value] of Object.entries(tokens))document.documentElement.style.setProperty(name,value);
    }
   },{theme,surface});
   await assertContrast(page,'.connector-name',4.5);
   await assertContrast(page,'.connector-badge',4.5);
   if(!hosted) {await assertContrast(page,'.connector-account-identity',4.5);await assertContrast(page,'.connector-description',4.5);}
   await assertContrast(page,'.connector-search',4.5);
   for(const action of ['rest','hover','active','disabled']) {
    const button=page.getByRole('button',{name:'Refresh connector state'});
    if(action==='hover')await button.hover();
    if(action==='active')await page.mouse.down();
    if(action==='disabled') {await page.mouse.up();await button.evaluate(node=>node.disabled=true);}
    await page.evaluate(()=>Promise.all(document.getAnimations().map(animation=>animation.finished.catch(()=>{}))));
    await assertContrast(page,'.connector-refresh',4.5,`${surface} ${theme} ${action}`);
    if(action==='active')await page.mouse.up();
    if(action==='disabled')await button.evaluate(node=>node.disabled=false);
   }
   await page.screenshot({path:path.join(out,`${surface}-${theme}.png`)});
   await page.setViewportSize({width:320,height:740});
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,`${surface} ${theme} mobile overflow`);
   await page.screenshot({path:path.join(out,`${surface}-${theme}-320.png`)});
   await page.setViewportSize({width:1100,height:850});
   proof.push(`${surface} ${theme}: names, metadata, identities, descriptions, search, buttons/rest/hover/active/disabled >=4.5:1; 320px no overflow.`);
  }
  if(hosted) {
   await page.goto('https://keyboard-wtf-connectors-staging.vercel.app/desktop-login.html');
   await page.getByText('This desktop sign-in request is invalid.',{exact:false}).waitFor();
   proof.push('Existing desktop-login page/module loads and truthfully rejects a missing one-time request; no login tokens created.');
   const accountResponse=await page.goto('https://keyboard-wtf-connectors-staging.vercel.app/account');
   assert.equal(accountResponse.status(),200);
   await page.getByRole('heading',{name:'Your staging account',exact:true}).waitFor();
   await page.getByRole('button',{name:'Sign in with Google',exact:true}).waitFor({state:'visible'});
   assert.ok((await page.getByRole('note').textContent()).includes('TEST records only'));
   assert.equal(await page.locator('.buy').count(),0);
   assert.equal(await page.getByRole('link',{name:'Staging account',exact:true}).getAttribute('href'),'./account');
   proof.push('Actual hosted staging account route200, signed-out Firebase UI,TEST-only notice,no purchase buttons,local account navigation; no account action/sign-in performed.');
  }
  await context.close();
 }
 assert.deepEqual(errors,[]);
 fs.writeFileSync(path.join(out,'theme-proof.json'),JSON.stringify({testedAt:new Date().toISOString(),scope:hosted?'Actual hosted signed-out staging page; no OAuth/provider actions or installed acceptance':'Actual website wrapper/CSS + actual Settings CSS with isolated account fixture; no real OAuth or installed app proof',proof,errors},null,2));
 console.log(JSON.stringify({passed:proof.length,errors:errors.length,output:out}));
} finally {await browser.close();}

async function assertContrast(page,selector,minimum,stage='') {
 const values=await page.locator(selector).evaluateAll(nodes=>nodes.filter(n=>n.getBoundingClientRect().width>0).map(node=>{
  const style=getComputedStyle(node),rgba=value=>{const numbers=(value.match(/[\d.]+/g)||[]).map(Number);return {rgb:numbers.slice(0,3).map(n=>n*(value.startsWith('color(srgb ')?255:1)),alpha:numbers[3]??1};};
  let parent=node;const layers=[];
  while(parent){layers.push(rgba(getComputedStyle(parent).backgroundColor));parent=parent.parentElement;}
  const background=layers.reverse().reduce((below,layer)=>below.map((n,index)=>n*(1-layer.alpha)+(layer.rgb[index]??0)*layer.alpha),[255,255,255]);
  return {text:rgba(style.color).rgb,background};
 }));
 assert.ok(values.length,`No visible ${selector}`);
 const luminance=rgb=>rgb.map(n=>n/255).map(n=>n<=.04045?n/12.92:((n+.055)/1.055)**2.4).reduce((sum,n,index)=>sum+n*[.2126,.7152,.0722][index],0);
 for(const value of values){const a=luminance(value.text),b=luminance(value.background),ratio=(Math.max(a,b)+.05)/(Math.min(a,b)+.05);assert.ok(ratio>=minimum,`${stage} ${selector} contrast ${ratio.toFixed(2)} < ${minimum} ${JSON.stringify(value)}`);}
}
