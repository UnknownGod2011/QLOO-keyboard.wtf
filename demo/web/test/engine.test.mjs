import test from 'node:test';
import assert from 'node:assert/strict';
import { decide, qlooTypeForDomain, enforceGrounding, matchChoice } from '../lib/engine.js';
import { createQlooSession, resetQlooState, canonicalUrl } from '../lib/qloo.js';
import { FINAL_ON_SYSTEM, parseJsonText } from '../lib/gemini.js';
import { mockQlooFetch } from '../lib/fixtures.js';
import { seedPersona, emptyProfile, applyObservations, observationsFromFeedback } from '../lib/profile.js';

const ENV = { DEMO_MOCK: '1' };
const noSleep = async () => {};
const SHIRTS = { question: 'Which of these two shirts fits my style better?', optionsText: 'A: navy minimalist linen shirt, Uniqlo / B: neon graphic tee with large logo' };
const MOVIES = { question: 'Which is a better pick for me: Inception or Mamma Mia!?' };

function countingFetch(inner = mockQlooFetch) {
  const urls = [];
  const f = async (url, init) => { urls.push(String(url)); return inner(url, init); };
  f.urls = urls;
  return f;
}
const httpError = (status) => async () => ({ ok: false, status, headers: { get: () => null }, json: async () => ({}) });

test('domains map to the right Qloo entity types', () => {
  const expected = {
    movies: 'urn:entity:movie', tv: 'urn:entity:tv_show', music: 'urn:entity:artist', books: 'urn:entity:book',
    podcasts: 'urn:entity:podcast', games: 'urn:entity:video_game', dining: 'urn:entity:place', hotels: 'urn:entity:place',
    travel: 'urn:entity:destination', fashion: 'urn:entity:brand', brands: 'urn:entity:brand', people: 'urn:entity:person',
    technical: null, other: null, lifestyle: null,
  };
  for (const [domain, type] of Object.entries(expected)) assert.equal(qlooTypeForDomain(domain), type, domain);
});

test('technical question: zero Qloo calls, still answered', async () => {
  resetQlooState();
  const f = countingFetch();
  const r = await decide({ question: 'What is 17% of 2,340?', mode: 'on', profile: seedPersona() }, { env: ENV, qlooFetch: f });
  assert.equal(f.urls.length, 0);
  assert.equal(r.extraction.domain, 'technical');
  assert.equal(r.engine.qlooRelevant, false);
  assert.equal(r.engine.calls, 0);
  assert.equal(r.engine.qlooGrounded, false);
  assert.deepEqual(r.decision.cited_qloo, []);
  assert.equal(r.decision.choice, '397.8');
});

test('candidate scoring uses one insights call with filter.results.entities', async () => {
  resetQlooState();
  const f = countingFetch();
  const r = await decide({ ...MOVIES, mode: 'on', profile: seedPersona() }, { env: ENV, qlooFetch: f });
  const insights = f.urls.filter((u) => u.includes('/v2/insights'));
  assert.equal(insights.length, 1);
  const q = new URL(insights[0]).searchParams;
  assert.equal(q.get('filter.type'), 'urn:entity:movie');
  assert.equal(q.get('filter.results.entities').split(',').length, 2);
  assert.equal(q.get('signal.interests.entities').split(',').length, 3);
  assert.equal(q.get('feature.explainability'), 'true');
  assert.equal(r.engine.qlooGrounded, true);
  assert.ok(r.engine.calls <= 8);
  const inc = r.engine.options.find((o) => o.label === 'Inception');
  const mm = r.engine.options.find((o) => o.label === 'Mamma Mia!');
  assert.equal(inc.qlooEntity.name, 'Inception');
  assert.ok(inc.affinity > mm.affinity);
  assert.equal(r.decision.choice, 'Inception');
  assert.ok(r.decision.cited_qloo.length >= 1);
  assert.deepEqual(r.engine.signals.map((s) => s.name).sort(), ['Arrival', 'Blade Runner 2049', 'Interstellar']);
  for (const u of f.urls) assert.ok(!/key/i.test(new URL(u).search), 'API key never in URL');
});

test('cache and in-flight de-duplication reduce Qloo calls', async () => {
  resetQlooState();
  const f = countingFetch();
  const body = { ...SHIRTS, mode: 'on', profile: seedPersona() };
  const first = await decide(body, { env: ENV, qlooFetch: f });
  const n1 = f.urls.length;
  assert.equal(first.engine.calls, n1);
  assert.ok(first.engine.cacheHits >= 1, 'option "Uniqlo" and liked "Uniqlo" share one in-flight search');
  assert.equal(f.urls.filter((u) => u.includes('/search?') && u.includes('query=Uniqlo')).length, 1);
  const second = await decide(body, { env: ENV, qlooFetch: f });
  assert.equal(f.urls.length, n1, 'identical request served from cache');
  assert.equal(second.engine.calls, 0);
  assert.ok(second.engine.cacheHits >= n1);
  resetQlooState();
  const g = countingFetch();
  await Promise.all([decide(body, { env: ENV, qlooFetch: g }), decide(body, { env: ENV, qlooFetch: g })]);
  assert.equal(g.urls.length, n1, 'concurrent identical requests share calls');
});

test('canonical cache URL is order-independent and contains no key', () => {
  const a = canonicalUrl('https://x.test', '/v2/insights', { take: 2, 'filter.type': 'urn:entity:movie' });
  const b = canonicalUrl('https://x.test', '/v2/insights', { 'filter.type': 'urn:entity:movie', take: 2 });
  assert.equal(a, b);
  assert.equal(a, 'https://x.test/v2/insights?filter.type=urn:entity:movie&take=2');
});

test('self-signal avoided: an option entity is not used as its own Qloo signal', async () => {
  resetQlooState();
  const f = countingFetch();
  const r = await decide({ ...SHIRTS, mode: 'on', profile: seedPersona() }, { env: ENV, qlooFetch: f });
  const q = new URL(f.urls.find((u) => u.includes('/v2/insights'))).searchParams;
  const optionId = r.engine.options[0].qlooEntity.id;
  assert.ok(!q.get('signal.interests.entities').includes(optionId));
  assert.ok(q.get('signal.interests.tags').includes('minimalist'));
  assert.match(r.decision.choice, /^A/);
  assert.equal(r.decision.confidence, 'high');
});

test('Qloo failure falls back to taste memory with qloo_grounded=false', async () => {
  resetQlooState();
  let n = 0;
  const failing = async () => { n++; return httpError(503)(); };
  const r = await decide({ ...MOVIES, mode: 'on', profile: seedPersona() }, { env: ENV, qlooFetch: failing, sleep: noSleep });
  assert.equal(r.engine.qlooGrounded, false);
  assert.equal(r.engine.qlooFailed, true);
  assert.equal(r.engine.brief.qloo_grounded, false);
  assert.ok(r.engine.warnings.some((w) => /NOT Qloo-grounded/.test(w)));
  assert.deepEqual(r.decision.cited_qloo, []);
  for (const o of r.engine.options) assert.equal(o.affinity, null);
  assert.ok(n >= 2 && n <= 8, `calls attempted: ${n}`);
  assert.ok(r.decision.choice);
});

test('simulated outage and missing key both fall back without calls', async () => {
  resetQlooState();
  const f = countingFetch();
  const a = await decide({ ...MOVIES, mode: 'on', profile: seedPersona(), simulateQlooOutage: true }, { env: ENV, qlooFetch: f });
  assert.equal(f.urls.length, 0);
  assert.equal(a.engine.qlooGrounded, false);
  assert.ok(a.engine.warnings[0].includes('simulated'));
  const live = { GEMINI_API_KEY: 'x' };
  const gemini = { configured: true, extract: async () => a.extraction, decideOn: async () => ({ choice: 'Inception', rationale: 'r', cited_preferences: [], cited_qloo: ['invented'], confidence: 'low', uncertainty: '' }) };
  const b = await decide({ ...MOVIES, mode: 'on', profile: seedPersona() }, { env: live, gemini, qlooFetch: f });
  assert.equal(f.urls.length, 0);
  assert.ok(b.engine.warnings[0].includes('not configured'));
  assert.deepEqual(b.decision.cited_qloo, [], 'invented Qloo citations removed when not grounded');
  assert.ok(b.decision.groundingNote);
});

test('retry once on 429/5xx, never on 401; per-request and daily caps', async () => {
  resetQlooState();
  let calls = 0;
  const flaky = async (url, init) => { calls++; return calls === 1 ? httpError(429)() : mockQlooFetch(url, init); };
  const s = createQlooSession({ env: ENV, mock: true, fetchImpl: flaky, sleep: noSleep });
  assert.equal((await s.search('Inception', 'urn:entity:movie')).name, 'Inception');
  assert.equal(calls, 2);

  resetQlooState();
  let c401 = 0;
  const s401 = createQlooSession({ env: { QLOO_API_KEY: 'test-placeholder' }, fetchImpl: async () => { c401++; return httpError(401)(); }, sleep: noSleep });
  await assert.rejects(() => s401.search('Inception', 'urn:entity:movie'), (e) => e.code === 'unauthorized');
  assert.equal(c401, 1);

  resetQlooState();
  const capped = createQlooSession({ env: ENV, mock: true });
  for (let i = 0; i < 8; i++) await capped.search(`unknown title ${i}`, 'urn:entity:movie');
  await assert.rejects(() => capped.search('unknown title 9', 'urn:entity:movie'), (e) => e.code === 'request_cap');

  resetQlooState();
  const budget = createQlooSession({ env: { ...ENV, QLOO_DAILY_BUDGET: '2' }, mock: true });
  await budget.search('a1', 'urn:entity:movie');
  await budget.search('a2', 'urn:entity:movie');
  await assert.rejects(() => budget.search('a3', 'urn:entity:movie'), (e) => e.code === 'daily_budget');
  resetQlooState();
});

test('cold start: no taste signals means no Qloo calls and the result says so', async () => {
  resetQlooState();
  const f = countingFetch();
  const r = await decide({ ...MOVIES, mode: 'on', profile: emptyProfile() }, { env: ENV, qlooFetch: f });
  assert.equal(f.urls.length, 0);
  assert.equal(r.engine.qlooGrounded, false);
  assert.match(r.engine.reason, /Cold start/);
});

test('recommend mode: take 5, excludes liked/disliked, drops explicit dislikes, learns from "Not for me"', async () => {
  resetQlooState();
  const f = countingFetch();
  let profile = seedPersona();
  const body = { question: 'Find a movie I would genuinely enjoy tonight', mode: 'on' };
  const r1 = await decide({ ...body, profile }, { env: ENV, qlooFetch: f });
  assert.equal(r1.extraction.task, 'recommend');
  assert.ok(r1.engine.recommendations.length >= 1);
  const q = new URL(f.urls.find((u) => u.includes('/v2/insights'))).searchParams;
  assert.equal(q.get('take'), '5');
  assert.equal(q.get('filter.results.entities'), null);
  assert.equal(q.get('filter.exclude.entities').split(',').length, 3);
  assert.ok(!r1.engine.recommendations.some((x) => /annihilation|hereditary/i.test(x.name)), 'horror dropped');
  assert.ok(r1.engine.caveats.some((c) => /Dropped .*horror/i.test(c)));
  const first = r1.decision.choice;
  assert.equal(r1.decision.choiceRef.kind, 'recommendation');
  const rec = r1.engine.recommendations[r1.decision.choiceRef.index];
  profile = applyObservations(profile, observationsFromFeedback({ domain: 'movies', option: { label: rec.name, entity: { name: rec.name }, attributes: [] }, polarity: -1, decisionId: 't1' })).profile;
  const r2 = await decide({ ...body, profile }, { env: ENV, qlooFetch: f });
  assert.notEqual(r2.decision.choice, first);
  assert.ok(!r2.engine.recommendations.some((x) => x.name === first));
  assert.ok(r2.engine.cacheHits >= 3, 'liked-title lookups served from cache');
});

test('feedback changes a follow-up decision (choose mode)', async () => {
  resetQlooState();
  let profile = seedPersona();
  const r1 = await decide({ ...MOVIES, mode: 'on', profile }, { env: ENV });
  assert.equal(r1.decision.choice, 'Inception');
  const opt = r1.extraction.options[r1.decision.choiceRef.index];
  profile = applyObservations(profile, observationsFromFeedback({ domain: r1.extraction.domain, option: opt, polarity: -1, decisionId: 'd1' })).profile;
  const r2 = await decide({ ...MOVIES, mode: 'on', profile }, { env: ENV });
  assert.equal(r2.decision.choice, 'Mamma Mia!');
  assert.ok(r2.engine.options.find((o) => o.label === 'Inception').prefScore < 0);
});

test('mode off is Gemini only: no profile, no Qloo', async () => {
  resetQlooState();
  const f = countingFetch();
  const r = await decide({ ...MOVIES, mode: 'off', profile: seedPersona() }, { env: ENV, qlooFetch: f });
  assert.equal(f.urls.length, 0);
  assert.equal(r.engine.calls, 0);
  assert.equal(r.engine.qlooGrounded, false);
  assert.deepEqual(r.decision.cited_preferences, []);
  assert.deepEqual(r.decision.cited_qloo, []);
});

test('final prompt forbids false Qloo claims and says Qloo does not judge visuals', () => {
  assert.match(FINAL_ON_SYSTEM, /qloo_grounded is false you MUST NOT claim/);
  assert.match(FINAL_ON_SYSTEM, /Qloo does NOT judge colour, fit, size, material or visual appearance/);
  assert.deepEqual(parseJsonText('```json\n{"a":1}\n```'), { a: 1 });
  assert.deepEqual(parseJsonText('Sure! {"b":2} done'), { b: 2 });
});

test('enforceGrounding and matchChoice helpers', () => {
  const d = enforceGrounding({ choice: 'x', cited_qloo: ['made up'], cited_preferences: ['p1', 'p99', 'likes navy'] }, { qlooGrounded: false, brief: { relevant_preferences: [{ id: 'p1', statement: 'likes navy (color)', value: 'navy', strength: 'strong' }] } });
  assert.deepEqual(d.cited_qloo, []);
  assert.deepEqual(d.cited_preferences.map((p) => p.id), ['p1']);
  const opts = [{ label: 'A: navy shirt' }, { label: 'B: neon tee' }];
  assert.deepEqual(matchChoice('B', opts), { kind: 'option', index: 1 });
  assert.deepEqual(matchChoice('Option A: navy shirt', opts), { kind: 'option', index: 0 });
  assert.deepEqual(matchChoice('Dune', [], [{ name: 'Contact' }, { name: 'Dune' }]), { kind: 'recommendation', index: 1 });
});
