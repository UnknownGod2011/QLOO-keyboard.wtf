'use strict';
// Execute only the actual Settings update UI functions with synthetic responses.
// No browser, profile, network, provider or installer is used.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const html = fs.readFileSync(path.join(root, 'src/Resources/web/settings.html'), 'utf8');
function uiFunction(name) {
  const start = html.indexOf('async function ' + name + '(');
  const end = html.indexOf('\n}', start);
  assert.ok(start >= 0 && end > start);
  return html.slice(start, end + 2);
}
function fixture(replies, accepts = true) {
  const elements = new Map(), calls = [], confirmations = [];
  const context = {
    document: { getElementById(id) {
      if (!elements.has(id)) elements.set(id, { value: 'stable', style: {}, disabled: false, textContent: '' });
      return elements.get(id);
    } },
    confirm: text => { confirmations.push(text); return accepts; },
    showToast: () => {},
    fetch: async (input, init) => {
      calls.push({ input, init });
      const reply = replies.shift();
      assert.ok(reply, 'Unexpected extra HTTP request');
      if (reply.error) throw reply.error;
      return { ok: reply.ok !== false, async json() { return reply.body; } };
    },
  };
  vm.createContext(context);
  const declaration = html.match(/let verifiedUpdateOffer\s*=\s*null\s*;/)?.[0] || '';
  vm.runInContext(declaration + '\n' + ['saveUpdateChannel', 'checkForUpdates', 'installVerifiedUpdate'].map(uiFunction).join('\n'), context);
  return { context, calls, confirmations, elements };
}
const offer = { ok: true, available: true, version: '2.3.4', channel: 'stable', releaseNotes: 'Synthetic notes', consentToken: 'a'.repeat(64) };
let passed = 0, failed = 0;
async function check(name, action) {
  try { await action(); passed++; }
  catch (error) { failed++; console.error(name + ': ' + error.message); }
}
async function run() {
  await check('No unchecked installation', async () => {
    const f = fixture([]);
    await f.context.installVerifiedUpdate();
    assert.equal(f.calls.length, 0);
    assert.equal(f.confirmations.length, 0);
  });
  await check('Exact checked offer is confirmed and sent', async () => {
    const f = fixture([{ body: offer }, { body: { ok: true, started: true } }]);
    await f.context.checkForUpdates();
    await f.context.installVerifiedUpdate();
    assert.equal(f.calls.length, 2);
    assert.equal(f.calls[1].input, '/api/updates/install');
    assert.equal(f.calls[1].init.headers['X-Keyboard-Wtf-Update-Consent'], offer.consentToken);
    assert.match(f.confirmations[0], /2\.3\.4/);
    assert.match(f.confirmations[0], /stable/);
    assert.doesNotMatch(f.confirmations[0], /Active work will stop/);
  });
  await check('Consent is not persisted or exposed in URL/body', async () => {
    const f = fixture([{ body: offer }, { body: { ok: true, started: true } }]);
    await f.context.checkForUpdates();
    await f.context.installVerifiedUpdate();
    assert.ok(f.calls.every(call => !String(call.input).includes(offer.consentToken)));
    assert.equal(f.calls[1].init.body, undefined);
    assert.doesNotMatch(html.slice(html.indexOf('async function saveUpdateChannel'), html.indexOf('function formatPaidCredits')), /(?:localStorage|sessionStorage)/);
  });
  await check('Cancelled confirmation does not consume offer', async () => {
    const f = fixture([{ body: offer }], false);
    await f.context.checkForUpdates();
    await f.context.installVerifiedUpdate();
    await f.context.installVerifiedUpdate();
    assert.equal(f.calls.length, 1);
    assert.equal(f.confirmations.length, 2);
  });
  await check('Offer cannot be submitted twice', async () => {
    const f = fixture([{ body: offer }, { body: { ok: true, started: true } }]);
    await f.context.checkForUpdates();
    await f.context.installVerifiedUpdate();
    await f.context.installVerifiedUpdate();
    assert.equal(f.calls.length, 2);
    assert.equal(f.confirmations.length, 1);
  });
  for (const token of [undefined, null, '', 'a'.repeat(63), 'a'.repeat(65), 'x'.repeat(64)]) {
    await check('Invalid returned token is not offered: ' + String(token?.length), async () => {
      const f = fixture([{ body: { ...offer, consentToken: token } }]);
      await f.context.checkForUpdates();
      assert.equal(f.elements.get('installUpdateButton').style.display, 'none');
      await f.context.installVerifiedUpdate();
      assert.equal(f.calls.length, 1);
    });
  }
  for (const reply of [{ body: { ok: true, available: false } }, { ok: false, body: { ok: false } }, { error: new Error('Synthetic network failure') }]) {
    await check('New unsuccessful check invalidates old offer', async () => {
      const f = fixture([{ body: offer }, reply]);
      await f.context.checkForUpdates();
      await f.context.checkForUpdates();
      await f.context.installVerifiedUpdate();
      assert.equal(f.calls.length, 2);
      assert.equal(f.confirmations.length, 0);
    });
  }
  await check('Channel save invalidates checked offer', async () => {
    const f = fixture([{ body: offer }, { body: { ok: true } }]);
    await f.context.checkForUpdates();
    await f.context.saveUpdateChannel();
    await f.context.installVerifiedUpdate();
    assert.equal(f.calls.length, 2);
    assert.equal(f.confirmations.length, 0);
  });
  await check('Manual-launch update cannot offer in-app installation', async () => {
    const f = fixture([{ body: { ...offer, installationReady: false } }]);
    await f.context.checkForUpdates();
    assert.equal(f.elements.get('installUpdateButton').style.display, 'none');
    assert.match(f.elements.get('updateStatus').textContent, /normal installer/i);
    await f.context.installVerifiedUpdate();
    assert.equal(f.calls.length, 1);
    assert.equal(f.confirmations.length, 0);
  });
  await check('Install failure requires fresh check', async () => {
    const f = fixture([{ body: offer }, { ok: false, body: { ok: false, error: 'Synthetic failure' } }]);
    await f.context.checkForUpdates();
    await f.context.installVerifiedUpdate();
    await f.context.installVerifiedUpdate();
    assert.equal(f.calls.length, 2);
    assert.equal(f.confirmations.length, 1);
  });
  console.log(`Update consent UI checks: ${passed} passed / ${failed} failed (offline).`);
  if (failed) process.exitCode = 1;
}
run().catch(error => { console.error(error); process.exitCode = 1; });
