import test from 'node:test';
import assert from 'node:assert/strict';
import { Readable } from 'node:stream';
import { createDecideHandler } from '../api/decide.js';
import healthHandler, { healthPayload } from '../api/health.js';
import { resetQlooState } from '../lib/qloo.js';
import { seedPersona } from '../lib/profile.js';

const MOCK = { DEMO_MOCK: '1' };
const SHIRTS = { question: 'Which of these two shirts fits my style better?', optionsText: 'A: navy minimalist linen shirt, Uniqlo / B: neon graphic tee with large logo' };

function fakeRes() {
  return {
    statusCode: 200,
    headers: {},
    body: '',
    setHeader(k, v) { this.headers[k.toLowerCase()] = v; },
    end(b) { this.body = b ? String(b) : ''; this.ended = true; },
  };
}
function streamReq(body, { method = 'POST', headers = {}, ip = '127.0.0.1' } = {}) {
  const r = Readable.from([Buffer.from(typeof body === 'string' ? body : JSON.stringify(body))]);
  r.method = method;
  r.headers = { 'content-type': 'application/json', ...headers };
  r.socket = { remoteAddress: ip };
  return r;
}
async function call(handler, req) {
  const res = fakeRes();
  await handler(req, res);
  return { res, json: res.body ? JSON.parse(res.body) : null };
}

test('decide handler returns well-formed JSON in both modes', async () => {
  resetQlooState();
  const handler = createDecideHandler({ env: MOCK });
  for (const mode of ['off', 'on']) {
    const { res, json } = await call(handler, streamReq({ ...SHIRTS, mode, profile: seedPersona() }));
    assert.equal(res.statusCode, 200, res.body);
    assert.match(res.headers['content-type'], /application\/json/);
    assert.equal(json.mode, mode);
    for (const k of ['mode', 'extraction', 'engine', 'decision', 'timingMs']) assert.ok(k in json, k);
    for (const k of ['qlooRelevant', 'qlooGrounded', 'reason', 'entityType', 'calls', 'cacheHits', 'signals', 'options', 'warnings']) assert.ok(k in json.engine, `engine.${k}`);
    for (const k of ['choice', 'rationale', 'cited_preferences', 'cited_qloo', 'confidence', 'uncertainty']) assert.ok(k in json.decision, `decision.${k}`);
    for (const o of json.engine.options) for (const k of ['label', 'qlooEntity', 'affinity', 'prefScore', 'evidence']) assert.ok(k in o, `option.${k}`);
    assert.ok(['high', 'medium', 'low'].includes(json.decision.confidence));
    assert.equal(json.extraction.domain, 'fashion');
    assert.equal(json.extraction.options.length, 2);
    if (mode === 'on') {
      assert.equal(json.engine.qlooGrounded, true);
      assert.equal(json.engine.entityType, 'urn:entity:brand');
      assert.match(json.decision.choice, /^A/);
      assert.ok(json.decision.cited_preferences.length >= 1);
      assert.ok(json.engine.calls >= 1 && json.engine.calls <= 8);
    } else {
      assert.equal(json.engine.calls, 0);
      assert.equal(json.engine.qlooGrounded, false);
    }
  }
});

test('works with a Vercel-style pre-parsed req.body and reused extraction', async () => {
  resetQlooState();
  const handler = createDecideHandler({ env: MOCK });
  const off = await call(handler, { method: 'POST', headers: { 'content-type': 'application/json' }, body: { ...SHIRTS, mode: 'off' } });
  assert.equal(off.res.statusCode, 200);
  const on = await call(handler, { method: 'POST', headers: { 'content-type': 'application/json' }, body: { ...SHIRTS, mode: 'on', profile: seedPersona(), extraction: off.json.extraction } });
  assert.equal(on.res.statusCode, 200);
  assert.equal(on.json.timings.extractionReused, true);
  assert.deepEqual(on.json.extraction.options.map((o) => o.label), off.json.extraction.options.map((o) => o.label));
});

test('rejects bad requests with friendly JSON errors', async () => {
  const handler = createDecideHandler({ env: MOCK });
  const get = await call(handler, { method: 'GET', headers: {} });
  assert.equal(get.res.statusCode, 405);
  assert.equal(get.res.headers.allow, 'POST');
  assert.equal((await call(handler, streamReq('{}', { headers: { 'content-type': 'text/plain' } }))).res.statusCode, 415);
  assert.equal((await call(handler, streamReq('{not json'))).res.statusCode, 400);
  assert.equal((await call(handler, streamReq({ mode: 'on' }))).res.statusCode, 400);
  assert.equal((await call(handler, streamReq({ question: 'hi', mode: 'maybe' }))).res.statusCode, 400);
  const img = { mimeType: 'image/png', data: 'iVBORw0KGgo=' };
  assert.equal((await call(handler, streamReq({ question: 'hi', mode: 'on', images: [img, img, img, img] }))).res.statusCode, 400);
  assert.equal((await call(handler, streamReq({ question: 'hi', mode: 'on', images: [{ mimeType: 'image/svg+xml', data: 'AAAA' }] }))).res.statusCode, 400);
  const big = await call(handler, streamReq({ question: 'hi', mode: 'on', images: [{ mimeType: 'image/png', data: 'A'.repeat(1.6 * 1024 * 1024) }] }));
  assert.equal(big.res.statusCode, 400);
  assert.ok(big.json.error);
});

test('per-IP rate limit: 10 decide calls per 10 minutes', async () => {
  const handler = createDecideHandler({ env: MOCK });
  for (let i = 0; i < 10; i++) {
    assert.equal((await call(handler, streamReq({ question: 'What is 17% of 2,340?', mode: 'on' }, { ip: '10.0.0.9' }))).res.statusCode, 200);
  }
  const blocked = await call(handler, streamReq({ question: 'What is 17% of 2,340?', mode: 'on' }, { ip: '10.0.0.9' }));
  assert.equal(blocked.res.statusCode, 429);
  assert.ok(Number(blocked.res.headers['retry-after']) > 0);
  assert.equal((await call(handler, streamReq({ question: 'What is 17% of 2,340?', mode: 'on' }, { ip: '10.0.0.10' }))).res.statusCode, 200);
});

test('missing Gemini key -> 503; Gemini failure -> 502 with a friendly message', async () => {
  const noKey = await call(createDecideHandler({ env: {} }), streamReq({ question: 'hi', mode: 'off' }));
  assert.equal(noKey.res.statusCode, 503);
  assert.match(noKey.json.error, /Gemini/);
  const failing = createDecideHandler({ env: { GEMINI_API_KEY: 'test-placeholder' }, geminiFetch: async () => ({ ok: false, status: 500, json: async () => ({}) }) });
  const r = await call(failing, streamReq({ question: 'hi', mode: 'on' }));
  assert.equal(r.res.statusCode, 502);
  assert.match(r.json.error, /try again/);
  assert.ok(!r.res.body.includes('test-placeholder'));
});

test('health reports configuration but never key values', async () => {
  const p = healthPayload({ QLOO_API_KEY: 'secret-value-123', GEMINI_API_KEY: 'another-secret', DEMO_MOCK: '0' });
  assert.deepEqual(p, { ok: true, qlooConfigured: true, geminiConfigured: true, mock: false });
  assert.ok(!JSON.stringify(p).includes('secret'));
  const res = fakeRes();
  await healthHandler({ method: 'GET', headers: {} }, res);
  assert.equal(res.statusCode, 200);
  assert.deepEqual(Object.keys(JSON.parse(res.body)).sort(), ['geminiConfigured', 'mock', 'ok', 'qlooConfigured']);
});
