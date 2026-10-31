// Consumes an ephemeral staging redirect via stdin, never a command-line secret.
// Navigates logged-out pages only; no credentials, consent clicks or provider data.
import { chromium } from 'file:///C:/Users/Admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/index.mjs';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
let input = ''; for await (const chunk of process.stdin) input += chunk;
const { redirect, toolkit } = JSON.parse(input);
const initial = new URL(redirect);
if (initial.protocol !== 'https:' || !['backend.composio.dev', 'connect.composio.dev'].includes(initial.hostname)
  || !['googlesuper', 'github'].includes(toolkit)) throw new Error('Not a reviewed staging authorization redirect.');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const output = resolve(root, '.artifacts/connectors/hosted'); await mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
const hosts = new Set(), observations = [];
page.on('framenavigated', frame => { if (frame === page.mainFrame()) { try { hosts.add(new URL(frame.url()).hostname); } catch {} } });
try {
  await page.goto(redirect, { waitUntil: 'networkidle', timeout: 25000 });
  const visible = await page.locator('body').innerText();
  const headings = await page.locator('h1,h2,[role="heading"]').allTextContents();
  const proof = { toolkit, finalHost: new URL(page.url()).hostname, hosts: [...hosts],
    title: (await page.title()).slice(0, 150), headings: headings.map(value => value.trim().slice(0, 150)),
    visibleComposioBrand: /composio/i.test(visible), possibleProviderError: /redirect_uri_mismatch|invalid_client|access blocked|oauth app access restrictions/i.test(visible),
    signedIn: false, consentCompleted: false, contentRead: false, providerWrite: false };
  await page.screenshot({ path: resolve(output, `authorization-${toolkit}.png`), fullPage: true });
  await writeFile(resolve(output, `authorization-${toolkit}.json`), JSON.stringify(proof, null, 2));
  console.log(JSON.stringify(proof));
} catch {
  console.log(JSON.stringify({ toolkit, observedHosts: [...hosts], rendered: false, consentCompleted: false }));
  process.exitCode = 1;
} finally { await browser.close(); }
