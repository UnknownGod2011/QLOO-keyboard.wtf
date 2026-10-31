// Actual hosted unauthenticated/rendered checks. NOT OAuth or provider-write proof.
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const output = resolve(root, '.artifacts/connectors/hosted');
await mkdir(output, { recursive: true });
const site = 'https://keyboard-wtf-connectors-staging.vercel.app';
const api = 'https://keyboard-wtf-connectors-staging-aac6362p6a-el.a.run.app';
const started = Date.now(), checks = [];
const assert = (condition, label) => { if (!condition) throw new Error(label); checks.push(label); };
const catalog = await fetch(`${api}/v1/connectors/catalog`);
assert(catalog.ok && catalog.headers.get('cache-control') === 'no-store', 'Hosted catalog available and noncacheable');
const items = await catalog.json();
assert(items.length === 12 && items.find(value => value.id === 'gmail')?.availability === 'available', 'Staging Google auth configuration active');
assert(['reddit', 'linkedin', 'googlemeet'].every(id => items.find(value => value.id === id)?.availability === 'coming_soon'), 'Unverified services remain Coming soon');
for (const path of ['/', '/tasks/' + 'a'.repeat(32), '/attempts/' + 'a'.repeat(32)]) {
  const response = await fetch(`${api}/v1/connectors${path}`);
  assert(response.status === 401, `Unauthenticated connector ${path} rejected`);
}
const options = await fetch(`${api}/v1/connectors/connect`, { method: 'OPTIONS', headers: {
  Origin: site, 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'authorization,content-type'
} });
assert(options.headers.get('access-control-allow-origin') === site, 'Staging origin explicitly accepted');
const denied = await fetch(`${api}/v1/connectors/connect`, { method: 'OPTIONS', headers: {
  Origin: 'https://unrelated.example', 'Access-Control-Request-Method': 'POST'
} });
assert(!denied.headers.get('access-control-allow-origin'), 'Unrelated web origin not authorized');
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: 'light' });
const page = await context.newPage(), errors = [];
page.on('pageerror', error => errors.push(error.message));
page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
try {
  const response = await page.goto(`${site}/connectors`, { waitUntil: 'networkidle' });
  assert(response.status() === 200, 'Canonical staging page is accessible');
  const headers = response.headers();
  assert(headers['referrer-policy'] === 'no-referrer', 'OAuth return query cannot leak through Referer');
  assert(headers['x-content-type-options'] === 'nosniff' && headers['x-frame-options'] === 'DENY', 'Connector page rejects MIME sniffing and framing');
  assert(headers['cache-control']?.includes('no-store'), 'Account-management page is noncacheable at the HTTP layer');
  await page.getByRole('button', { name: 'Sign in with Google', exact: true }).waitFor({ state: 'visible', timeout: 20000 });
  await page.getByText('Gmail', { exact: true }).first().waitFor({ state: 'visible' });
  assert(!(await page.getByRole('button', { name: 'Sign out', exact: true }).isVisible()), 'Signed-out state hides the sign-out control');
  assert(await page.getByText('Isolated connector staging', { exact: false }).count() === 1, 'Visible staging notice');
  assert(await page.locator('img').count() >= 12, 'Bundled service logos rendered');
  assert(errors.length === 0, 'No hosted browser console/page errors');
  await page.screenshot({ path: resolve(output, 'staging-signed-out.png'), fullPage: true });
  const config = await fetch(`${site}/config.js`).then(value => value.text());
  assert(config.includes('keyboard-wtf-staging') && !config.includes('keyboard-wtf-prod'), 'Hosted Firebase identity is staging only');
  assert(!config.includes('COMPOSIO') && !config.includes('clientSecret'), 'No provider credential in public config');
  const alias = await fetch(`${site}/settings/connectors`);
  assert(alias.ok && new URL(alias.url).pathname === '/connectors', 'Settings connector URL alias reaches the canonical management page');
  await writeFile(resolve(output, 'unauthenticated.json'), JSON.stringify({ time: new Date().toISOString(),
    checks, errors, elapsedMs: Date.now() - started, realOAuth: false, realProviderExecution: false, productionModified: false }, null, 2));
  console.log(JSON.stringify({ passed: checks.length, errors: errors.length, realOAuth: false, report: resolve(output, 'unauthenticated.json') }));
} finally { await context.close(); await browser.close(); }
