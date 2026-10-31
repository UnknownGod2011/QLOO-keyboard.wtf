// Generated staging output only; never edits or links the production site project.
import { readFile, writeFile, mkdir, copyFile, readdir } from 'node:fs/promises';
import { resolve, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const output = join(root, '.artifacts/connectors/staging-web');
const site = join(root, 'production/site');
const required = (name) => {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Set ${name} for the independent QLOO connector deployment.`);
  return value;
};
const requiredHttpsBase = (name) => {
  const value = required(name);
  let url;
  try { url = new URL(value); }
  catch { throw new Error(`${name} must be a valid HTTPS URL.`); }
  const loopback = ['localhost', '127.0.0.1', '::1'].includes(url.hostname);
  if ((url.protocol !== 'https:' && !(loopback && url.protocol === 'http:'))
    || /keyboard[-.]?wtf/i.test(url.hostname))
    throw new Error(`${name} must use a QLOO-owned HTTPS endpoint (or loopback for local development).`);
  return url.toString().replace(/\/?$/, '/');
};
const webBase = requiredHttpsBase('QLOO_CONNECTOR_WEB_URL');
const backend = requiredHttpsBase('QLOO_CONNECTOR_BACKEND_URL');
const stagingHost = new URL(webBase).origin;
const firebase = {
  apiKey: required('QLOO_FIREBASE_API_KEY'),
  authDomain: required('QLOO_FIREBASE_AUTH_DOMAIN'),
  projectId: required('QLOO_FIREBASE_PROJECT_ID'),
  storageBucket: required('QLOO_FIREBASE_STORAGE_BUCKET'),
  messagingSenderId: required('QLOO_FIREBASE_MESSAGING_SENDER_ID'),
  appId: required('QLOO_FIREBASE_APP_ID'),
};
const files = ['connectors.html', 'connectors.js', 'connectors-panel.js', 'connectors-panel.css',
  'app.js', 'styles.css', 'theme.css', 'favicon.svg', 'luma/theme-bootstrap.js',
  'desktop-login.html', 'desktop-login.js', 'account.html', 'account.js'];
await mkdir(output, { recursive: true });
const manifest = {};
for (const relative of [...files, ...(await readdir(join(site, 'connector-icons'))).map(name => `connector-icons/${name}`)]) {
  const source = join(site, relative), target = join(output, relative);
  await mkdir(dirname(target), { recursive: true });
  await copyFile(source, target);
  manifest[relative] = createHash('sha256').update(await readFile(source)).digest('hex');
}
// Firebase browser configuration is QLOO-owned and injected from local env.
// The API key is public client configuration; restrict it in Firebase Console.
const runtimeConfig = {
  canonicalWebsite: stagingHost, legacyWebsite: stagingHost, backendBaseUrl: backend,
  downloads: { windows: { available: false }, macos: { available: false } },
  firebase,
};
await writeFile(join(output, 'config.js'), `export const runtimeConfig = Object.freeze(${JSON.stringify(runtimeConfig, null, 2)});\n`);
const page = await readFile(join(output, 'connectors.html'), 'utf8');
await writeFile(join(output, 'connectors.html'), page
  .replace(/https:\/\/keyboardwtf\.com\/connectors/g, `${stagingHost}/connectors`)
  .replace('<main class="connector-page">', '<main class="connector-page"><p role="note" class="small muted">QLOO connector access · no purchases or managed AI.</p>')
  .replace('href="./support"', `href="${stagingHost}/connectors"`));
// Reuse the accepted account renderer with this deployment's Firebase/API.
// This is a clearly labelled TEST derivative, never the real billing account.
const account = await readFile(join(output, 'account.html'), 'utf8');
await writeFile(join(output, 'account.html'), account
  .replace(/https:\/\/keyboardwtf\.com\/account\.html/g, `${stagingHost}/account`)
  .replace('Account & billing · keyboard.wtf', 'QLOO connector account')
  .replace('<main>', '<main><p role="note" class="shell status">QLOO connector identity and authorization. No subscription or purchase controls.</p>')
  .replace('<h1>Your account</h1>', '<h1>Connector account</h1>')
  .replace(/<nav class="site-nav">[\s\S]*?<\/nav>/,
    '<nav class="site-nav"><div class="shell nav-inner"><a class="brand" href="./connectors">QLOO</a><div class="nav-links"><a class="nav-link" href="./connectors">Connectors</a><a class="nav-link" href="./account">Connector account</a></div></div></nav>')
  .replace('<section class="content-section compact-section">', '<section class="content-section compact-section hidden">')
  .replace('One clear monthly plan, with server-verified billing and usage.', 'Purchases are disabled on this connector test surface. The displayed allowance is a staging record, not your real subscription.')
  .replace('<link rel="stylesheet" href="./luma/luma-ui.css">', '')
  .replace('<script type="module" src="./site-enhancements.js"></script>', '')
  .replace(/href="\.\/(?:#download|pricing\.html|reviews\.html)"/g, 'href="./connectors"')
  .replace(/href="\.\/(support|privacy|terms|refunds|changelog)\.html"/g, `href="${stagingHost}/connectors"`));
const accountScript = await readFile(join(output, 'account.js'), 'utf8');
const productsLine = 'const products = (catalog.products || []).filter((product) => product.kind === "subscription");';
if (accountScript.split(productsLine).length !== 2)
  throw new Error('Account product boundary changed; review the staging derivative before deploying.');
await writeFile(join(output, 'account.js'), accountScript.replace(productsLine,
  'const products = []; // QLOO connector account: no purchase controls.'));
// The existing one-time desktop sign-in must use this project's Firebase/API.
// No production credential/session copying and no second auth implementation.
const login = await readFile(join(output, 'desktop-login.html'), 'utf8');
await writeFile(join(output, 'desktop-login.html'), login
  .replace(/https:\/\/keyboardwtf\.com\/desktop-login\.html/g, `${stagingHost}/desktop-login.html`)
  .replace('<main>', '<main><p role="note" class="shell small muted">QLOO connector sign-in. It uses this deployment\'s identity and connector authorization only.</p>')
  .replace('<link rel="stylesheet" href="./luma/luma-ui.css">', '')
  .replace('<script type="module" src="./site-enhancements.js"></script>', ''));
const config = JSON.parse(await readFile(join(site, 'vercel.json'), 'utf8'));
config.redirects.unshift({ source: '/', destination: '/connectors', permanent: false });
await writeFile(join(output, 'vercel.json'), JSON.stringify(config, null, 2));
const generatedManifest = {};
for (const relative of [...Object.keys(manifest), 'config.js', 'vercel.json'])
  generatedManifest[relative] = createHash('sha256').update(await readFile(join(output, relative))).digest('hex');
await writeFile(join(root, '.artifacts/connectors/staging-web-source.json'), JSON.stringify({
  project: process.env.QLOO_VERCEL_PROJECT_ID ?? '', scope: process.env.QLOO_VERCEL_SCOPE ?? '',
  web: stagingHost, backend, manifest, generatedManifest
}, null, 2));
// Explicit project + scope prevent inheriting production/site/.vercel.
if (!process.argv.includes('--deploy')) {
  console.log(JSON.stringify({ output, web: stagingHost, productionModified: false }));
} else {
  const cli = resolve(process.env.APPDATA, 'npm/node_modules/vercel/dist/index.js');
  const vercelProject = required('QLOO_VERCEL_PROJECT_ID');
  const vercelScope = required('QLOO_VERCEL_SCOPE');
  const run = (args) => new Promise((accept, reject) => {
    const child = spawn(process.execPath, [cli, ...args], { cwd: output, stdio: 'inherit' });
    child.on('error', reject); child.on('exit', code => code === 0 ? accept() : reject(new Error('Staging web command failed.')));
  });
  let linked;
  try { linked = JSON.parse(await readFile(join(output, '.vercel/project.json'), 'utf8')); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (!linked) {
    await run(['link', '--yes', '--project', vercelProject, '--scope', vercelScope]);
    linked = JSON.parse(await readFile(join(output, '.vercel/project.json'), 'utf8'));
  }
  if (linked.projectId !== vercelProject) throw new Error('Wrong QLOO project link; refusing deployment.');
  await run(['deploy', '--prod', '--yes', '--scope', vercelScope]);
}
