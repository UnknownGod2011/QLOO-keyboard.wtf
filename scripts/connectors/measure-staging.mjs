// Read-only measurements. No login, provider execution, model or paid request.
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = resolve(root, '.artifacts/connectors/hosted');
const site = 'https://keyboard-wtf-connectors-staging.vercel.app';
const api = 'https://keyboard-wtf-connectors-staging-aac6362p6a-el.a.run.app';
const samples = [];
for (let index = 0; index < 3; index++) {
  const start = performance.now();
  const response = await fetch(`${api}/v1/connectors/catalog`, { signal: AbortSignal.timeout(20000) });
  const headersMs = performance.now() - start;
  const body = await response.json();
  if (!response.ok || body.length !== 12) throw new Error('The staging catalog was not verified.');
  samples.push({ headersMs: Math.round(headersMs), completeMs: Math.round(performance.now() - start) });
}
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();
const errors = []; let apiRequests = 0;
page.on('pageerror', value => errors.push(value.message));
page.on('request', request => { if (request.url().startsWith(api)) apiRequests++; });
try {
  const start = performance.now();
  await page.goto(`${site}/connectors`, { waitUntil: 'domcontentloaded', timeout: 30000 });
  await page.getByText('Gmail', { exact: true }).first().waitFor();
  const visibleCatalogMs = Math.round(performance.now() - start);
  const navigation = await page.evaluate(() => {
    const entry = performance.getEntriesByType('navigation')[0];
    return { responseHeadersMs: Math.round(entry.responseStart), documentCompleteMs: Math.round(entry.responseEnd),
      domContentLoadedMs: Math.round(entry.domContentLoadedEventEnd) };
  });
  const before = apiRequests, search = [];
  for (const query of ['Gmail', 'GitHub', 'no matching service']) {
    const started = performance.now();
    await page.getByRole('searchbox').fill(query);
    if (query === 'no matching service') await page.getByText('No matching connectors.').waitFor();
    else await page.getByText(query, { exact: true }).first().waitFor();
    search.push({ query, inputToRenderMs: Math.round(performance.now() - started) });
  }
  if (apiRequests !== before || errors.length) throw new Error('Local search made a cloud request or the page failed.');
  await mkdir(out, { recursive: true });
  const proof = { time: new Date().toISOString(), surface: 'Actual hosted staging, signed out', catalog: samples,
    page: { ...navigation, visibleCatalogMs }, search, searchCloudRequests: apiRequests - before, pageErrors: errors.length,
    limitations: ['Includes network/browser automation; not a provider inference measurement.',
      'Authenticated connection lookup, actual Settings load, Jarvis planning and provider execution still need real consent/runtime acceptance.'],
    modelCalls: 0, purchases: 0, productionModified: false };
  await writeFile(resolve(out, 'performance.json'), JSON.stringify(proof, null, 2));
  console.log(JSON.stringify(proof));
} finally { await context.close(); await browser.close(); }
