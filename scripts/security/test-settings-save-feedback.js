'use strict';
// Offline execution of the source-generated Settings guard and actual UI callers.
// No server/profile/provider requests, secrets or installed application are used.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const source = fs.readFileSync(path.join(root, 'src/Services/WebSettingsService.cs'), 'utf8');
const marker = 'internal static string BuildSettingsRequestGuardScript(string actionToken)';
const method = source.slice(source.indexOf(marker));
assert.ok(method.startsWith(marker));
const start = method.indexOf('return $@"') + 'return $@"'.length;
const end = method.indexOf('\n";', start);
assert.ok(start > 8 && end > start);
const guard = method.slice(start, end).replaceAll('{tokenJson}', '"offline-fixture-token"')
  .replaceAll('{{', '{').replaceAll('}}', '}');
const html = fs.readFileSync(path.join(root, 'src/Resources/web/settings.html'), 'utf8');
function uiFunction(name) {
  const begin = html.indexOf('async function ' + name + '(');
  assert.ok(begin >= 0);
  const finish = html.indexOf('\n}', begin);
  assert.ok(finish > begin);
  return html.slice(begin, finish + 2);
}
function fixture(status, body, malformed = false) {
  const toasts = [], calls = [], counters = { reload: 0, load: 0, test: 0 };
  const elements = new Map();
  const storage = new Map();
  const context = {
    Headers, URL, currentSettings: { speechEngine: 'Whisper' },
    document: { getElementById(id) {
      if (!elements.has(id)) elements.set(id, { value: id === 'maxDuration' ? '30' : 'offline-fixture-value', checked: false });
      return elements.get(id);
    } },
    showToast: text => toasts.push(text),
    loadSettings: async () => { counters.load++; },
    testAiProvider: async () => { counters.test++; },
    sessionStorage: { getItem: k => storage.get(k), setItem: (k, v) => storage.set(k, v) },
    window: {
      location: { href: 'http://localhost:8090/', origin: 'http://localhost:8090', reload: () => { counters.reload++; } },
      fetch: async (input, init) => {
        calls.push({ input, init });
        return { ok: status >= 200 && status < 300, status,
          clone() { return this; },
          async json() { if (malformed) throw new SyntaxError('fixture'); return body; } };
      },
    },
  };
  vm.createContext(context);
  vm.runInContext(guard, context);
  context.fetch = context.window.fetch;
  return { context, toasts, calls, counters, elements };
}
let checks = 0;
async function run() {
  for (const [status, body, malformed] of [
    [500, { ok: false, error: 'secret-must-not-be-echoed' }, false],
    [200, { ok: false }, false],
    [204, null, true],
    [403, { ok: false, code: 'settings_session_expired' }, false],
  ]) {
    const f = fixture(status, body, malformed);
    await assert.rejects(f.context.fetch('/api/settings', { method: 'POST' }), /Settings were not saved/);
    assert.equal(f.calls[0].init.headers.get('X-Keyboard-Wtf-Settings-Action'), 'offline-fixture-token');
    assert.equal(f.counters.reload, status === 403 ? 1 : 0);
    if (status === 403) {
      await assert.rejects(f.context.fetch('/api/settings', { method: 'POST' }));
      assert.equal(f.counters.reload, 1);
    }
    checks++;
  }
  const success = fixture(200, { ok: true });
  assert.equal((await success.context.fetch('/api/settings', { method: 'POST' })).ok, true);
  checks++;
  for (const [target, init] of [
    ['/api/settings', { method: 'GET' }], ['/api/paid/fixture', { method: 'POST' }],
    ['https://other.example/api/settings', { method: 'POST' }],
  ]) {
    const f = fixture(500, { ok: false });
    assert.equal((await f.context.fetch(target, init)).status, 500);
    if (target.startsWith('https:')) assert.equal(f.calls[0].init.headers, undefined);
    checks++;
  }
  for (const name of ['apiKeyChanged', 'webhookChanged', 'settingChanged', 'saveGeminiApiKey']) {
    for (const succeeds of [false, true]) {
      const f = fixture(succeeds ? 200 : 500, { ok: succeeds });
      vm.runInContext(uiFunction(name), f.context);
      await f.context[name]('geminiApiKey');
      const saved = f.toasts.some(text => /saved|cleared/i.test(text) && !/not|failed/i.test(text));
      if (name === 'saveGeminiApiKey') {
        assert.equal(f.counters.test, succeeds ? 1 : 0);
        assert.equal(f.elements.get('geminiApiKey').value, succeeds ? '' : 'offline-fixture-value');
      } else assert.equal(saved, succeeds);
      if (!succeeds) assert.equal(f.counters.load, 0);
      assert.ok(f.toasts.every(text => !text.includes('secret-must-not-be-echoed')));
      checks++;
    }
  }
  // Preserve existing semantic conflict handling and the active-hotkey refresh.
  for (const status of [400, 409]) {
    const f = fixture(status, { ok: false, error: 'Offline hotkey conflict' });
    const stopped = [];
    f.context.stopHotkeyRecording = message => stopped.push(message);
    vm.runInContext(uiFunction('saveHotkeyValues'), f.context);
    await f.context.saveHotkeyValues({});
    assert.ok(f.toasts.includes('Offline hotkey conflict'));
    assert.deepEqual(stopped, ['Offline hotkey conflict']);
    assert.equal(f.counters.load, 1);
    checks++;
  }
  console.log(`Settings save-feedback checks: ${checks} passed (offline).`);
}
run().catch(error => { console.error(error); process.exitCode = 1; });
