// Generated staging derivative of the accepted account renderer, isolated data.
// No sign-in, purchase, cancellation, deletion, provider action or AI request.
import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..'),stage=resolve(root,'.artifacts/connectors/staging-web');
const config=await readFile(resolve(stage,'config.js'),'utf8');
assert.ok(config.includes('keyboard-wtf-staging')&&config.includes('keyboard-wtf-connectors-staging-aac6362p6a-el.a.run.app'));
const appFixture=`
 export const auth={currentUser:{uid:'account-fixture-user',email:'account-fixture@example.test'}};
 export function listenForAuth(callback){queueMicrotask(()=>callback(auth.currentUser));}
 export async function loadAccount(){return {email:'account-fixture@example.test',displayName:'Test fixture',planId:'monthly_test',planDisplayName:'Monthly staging plan',availableCreditMicros:700000000,subscriptionStatus:'active',paymentsEnabled:true,recentUsage:[],recentPurchases:[],devices:[]};}
 export async function loadCatalog(){return {products:[{kind:'subscription',sku:'test',displayName:'Test product',amountMinor:9900,currency:'INR',creditMicros:500000000}]};}
 export const formatCredits=value=>String(Number(value||0)/1000000),formatMoney=value=>String(value);
 export const getPendingPurchaseIds=()=>[],trackServerPendingPurchases=()=>{};
 export const googleSignIn=async()=>{},googleSignOut=async()=>{},chooseAnotherGoogleAccount=async()=>{};
 export const loadUsagePage=async()=>({items:[]}),loadPurchasePage=async()=>({items:[]});
 const forbidden=()=>{throw Error('Unrequested mutation in account fixture');};
 export const revokeDevice=forbidden,revokeAllDesktopSessions=forbidden,deleteAccountPermanently=forbidden,beginCheckout=forbidden,waitForAuthoritativePurchase=forbidden,cancelSubscriptionAtCycleEnd=forbidden;`;
const browser=await chromium.launch({channel:'chrome',headless:true}),page=await browser.newPage({viewport:{width:1100,height:850}});
const errors=[],proof=[];page.on('pageerror',error=>errors.push(error.message));
try {
 await page.route('http://127.0.0.1:4196/**',async route=>{
  const url=new URL(route.request().url());
  if(url.pathname==='/app.js')return route.fulfill({contentType:'text/javascript',body:appFixture});
  const relative=url.pathname==='/account'?'account.html':url.pathname.slice(1);
  if(!/^(account\.(html|js)|styles\.css|theme\.css|favicon\.svg|luma\/theme-bootstrap\.js)$/.test(relative))return route.abort();
  return route.fulfill({contentType:relative.endsWith('.js')?'text/javascript':relative.endsWith('.css')?'text/css':relative.endsWith('.svg')?'image/svg+xml':'text/html',body:await readFile(resolve(stage,relative))});
 });
 await page.goto('http://127.0.0.1:4196/account');
 await page.getByRole('heading',{name:'Your staging account',exact:true}).waitFor();
 await page.getByText('account-fixture@example.test',{exact:true}).waitFor();
 assert.equal(await page.locator('#balance').textContent(),'100.0%');
 assert.equal(await page.locator('.buy').count(),0,'No purchase control despite fixture paymentsEnabled');
 assert.equal(await page.locator('a[href="./connectors"]').count()>0,true);
 assert.ok((await page.getByRole('note').textContent()).includes('does not show or change your real subscription'));
 assert.equal(await page.locator('script[src*="site-enhancements"],link[href*="luma-ui"]').count(),0);
 proof.push('Existing account renderer loads stage fixture, truthful TEST label,100% remaining,zero purchase controls,local connector navigation,no missing Luma dependencies.');
 const output=resolve(root,'output/playwright/connectors-stage-account');await mkdir(output,{recursive:true});
 for(const theme of ['light','dark']){
  await page.evaluate(value=>document.documentElement.dataset.theme=value,theme);
  for(const width of [1100,320]){
   await page.setViewportSize({width,height:850});
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,`${theme} ${width} overflow`);
   await page.screenshot({path:resolve(output,`${theme}-${width}.png`)});
  }
  proof.push(`${theme}: desktop and320px no overflow.`);
 }
 assert.deepEqual(errors,[]);
 await writeFile(resolve(output,'proof.json'),JSON.stringify({time:new Date().toISOString(),fixtureOnly:true,proof,errors},null,2));
 console.log(JSON.stringify({passed:proof.length,errors:errors.length}));
}finally{await browser.close();}
