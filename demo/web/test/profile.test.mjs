import test from 'node:test';
import assert from 'node:assert/strict';
import * as P from '../lib/profile.js';

const DAY = 86400000;
const T0 = Date.parse('2026-01-10T12:00:00Z');

test('confidence = 1 - exp(-1.2|net|) and grows with repeated choices', () => {
  let p = P.emptyProfile();
  const confs = [];
  for (let i = 0; i < 4; i++) {
    const r = P.addObservation(p, { domain: 'movies', attribute: 'entity', value: 'Arrival', polarity: 1, source: 'choice', fingerprint: `decision-${i}` }, T0 + i * 1000);
    assert.equal(r.status, 'added');
    p = r.profile;
    confs.push(P.prefConfidence(p.preferences[0], T0 + i * 1000));
  }
  for (let i = 1; i < confs.length; i++) assert.ok(confs[i] > confs[i - 1], `confidence should grow (${confs})`);
  assert.ok(Math.abs(confs[0] - (1 - Math.exp(-1.2 * 0.6))) < 1e-6);
  assert.equal(P.confidenceLabel(confs[0]), 'moderate');
  assert.equal(P.confidenceLabel(confs[3]), 'strong');
  assert.equal(p.preferences.length, 1);
  assert.equal(p.preferences[0].kind, 'inferred');
});

test('weights and half-lives: explicit 1.0/365 d, inferred mention 0.35/120 d', () => {
  const ex = P.addObservation(P.emptyProfile(), { domain: 'music', attribute: 'artist', value: 'Radiohead', polarity: 1, source: 'explicit' }, T0).profile.preferences[0];
  assert.ok(Math.abs(P.netEvidence(ex, T0) - 1) < 1e-9);
  assert.ok(Math.abs(P.netEvidence(ex, T0 + 365 * DAY) - 0.5) < 1e-9);
  const inf = P.addObservation(P.emptyProfile(), { domain: 'music', attribute: 'genre', value: 'jazz', polarity: 1, source: 'mention' }, T0).profile.preferences[0];
  assert.ok(Math.abs(P.netEvidence(inf, T0) - 0.35) < 1e-9);
  assert.ok(Math.abs(P.netEvidence(inf, T0 + 120 * DAY) - 0.175) < 1e-9);
  const rej = P.addObservation(P.emptyProfile(), { domain: 'music', attribute: 'genre', value: 'polka', source: 'rejection' }, T0).profile.preferences[0];
  assert.equal(rej.polarity, -1);
  assert.ok(Math.abs(P.netEvidence(rej, T0) + 0.6) < 1e-9);
});

test('a contradicting explicit statement halves older opposite evidence (no blind overwrite)', () => {
  const p = P.addObservation(P.emptyProfile(), { domain: 'fashion', attribute: 'color', value: 'navy', polarity: 1, source: 'explicit' }, T0).profile;
  const r = P.addObservation(p, { domain: 'fashion', attribute: 'color', value: 'navy', polarity: -1, source: 'explicit', quote: 'I am over navy now' }, T0 + 1000);
  assert.equal(r.status, 'changed');
  const pref = r.profile.preferences[0];
  assert.equal(r.profile.preferences.length, 1);
  assert.equal(pref.observations[0].weight, 0.5);
  assert.equal(pref.observations[1].weight, 1);
  assert.equal(pref.polarity, -1);
  assert.ok(pref.recentlyChanged);
  assert.ok(Math.abs(P.netEvidence(pref, T0 + 1000) + 0.5) < 1e-3);
  assert.match(r.message, /recently changed/);
});

test('temporary preferences expire after 14 days by default and are skipped', () => {
  const p = P.addObservation(P.emptyProfile(), { domain: 'dining', attribute: 'cuisine', value: 'ramen', polarity: 1, source: 'explicit', temporary: true }, T0).profile;
  const pref = p.preferences[0];
  assert.equal(pref.kind, 'temporary');
  assert.equal(Date.parse(pref.expiresAt) - T0, 14 * DAY);
  assert.equal(P.selectRelevant(p, { domain: 'dining', now: T0 + 13 * DAY }).length, 1);
  assert.equal(P.selectRelevant(p, { domain: 'dining', now: T0 + 15 * DAY }).length, 0);
  assert.equal(P.isExpired(pref, T0 + 15 * DAY), true);
  assert.equal(P.pruneExpired(p, T0 + 15 * DAY).preferences.length, 0);
});

test('duplicate fingerprint within 24 h is ignored', () => {
  const input = { domain: 'movies', attribute: 'entity', value: 'Inception', polarity: -1, source: 'rejection', fingerprint: 'fb:dec1:entity:inception:-1' };
  const p = P.addObservation(P.emptyProfile(), input, T0).profile;
  const dup = P.addObservation(p, input, T0 + 3600e3);
  assert.equal(dup.status, 'duplicate');
  assert.equal(dup.profile.preferences[0].observations.length, 1);
  const later = P.addObservation(p, input, T0 + 25 * 3600e3);
  assert.equal(later.status, 'added');
  assert.equal(later.profile.preferences[0].observations.length, 2);
});

test('sensitive attributes and values are refused with a visible message', () => {
  const cases = [
    ['health', 'diabetes'], ['general', 'my religion'], ['politics', 'left'], ['general', 'bisexual'],
    ['race', 'anything'], ['general', 'my salary is 90k'], ['general', 'alex@example.com'],
    ['general', '+44 7700 900123'], ['general', 'immigration status'], ['general', 'criminal record'],
  ];
  for (const [attribute, value] of cases) {
    const r = P.addObservation(P.emptyProfile(), { domain: 'lifestyle', attribute, value, polarity: 1 }, T0);
    assert.equal(r.status, 'refused', `${attribute}: ${value}`);
    assert.match(r.message, /sensitive/);
    assert.equal(r.profile.preferences.length, 0);
  }
  for (const value of ['Christian Dior', 'political thrillers', 'Arrested Development', 'Queer Eye', 'healthy salads']) {
    assert.equal(P.addObservation(P.emptyProfile(), { domain: 'lifestyle', attribute: 'general', value, polarity: 1 }, T0).status, 'added', value);
  }
  // sanitizeProfile drops sensitive entries smuggled in from storage or a request
  const dirty = { preferences: [{ id: 'p1', domain: 'lifestyle', attribute: 'health', value: 'asthma', polarity: 1, observations: [{ weight: 1, polarity: 1, at: new Date(T0).toISOString(), source: 'explicit' }] }] };
  assert.equal(P.sanitizeProfile(dirty, T0).preferences.length, 0);
});

test('contextual preferences apply only in a matching context and never overwrite general ones', () => {
  let p = P.addObservation(P.emptyProfile(), { domain: 'fashion', attribute: 'style', value: 'formal', polarity: -1, source: 'explicit' }, T0).profile;
  p = P.addObservation(p, { domain: 'fashion', attribute: 'style', value: 'formal', polarity: 1, source: 'explicit', context: 'wedding' }, T0).profile;
  assert.equal(p.preferences.length, 2);
  assert.equal(p.preferences[1].kind, 'contextual');
  assert.deepEqual(P.selectRelevant(p, { domain: 'fashion', context: '', now: T0 }).map((x) => x.polarity), [-1]);
  assert.equal(P.selectRelevant(p, { domain: 'fashion', context: "my cousin's wedding", now: T0 }).length, 2);
});

test('persona matching: navy minimalist Uniqlo shirt beats neon logo tee', () => {
  const prefs = P.selectRelevant(P.seedPersona(T0), { domain: 'fashion', now: T0 });
  const a = P.matchOption({ label: 'A: navy minimalist linen shirt, Uniqlo', attributes: [{ name: 'color', value: 'navy' }], entity: { name: 'Uniqlo' } }, prefs);
  const b = P.matchOption({ label: 'B: neon graphic tee with large logo', attributes: [], entity: { name: null } }, prefs);
  assert.ok(a.score > 0, `A score ${a.score}`);
  assert.ok(b.score < 0, `B score ${b.score}`);
  assert.ok(b.evidence.some((e) => /loud logos/.test(e.text)));
  // negated mentions do not count as a match
  const c = P.matchOption({ label: 'C: plain tee with no logo', attributes: [], entity: { name: null } }, prefs);
  assert.ok(!c.evidence.some((e) => /logos/.test(e.text)));
});

test('feedback observations target the chosen option entity and attributes', () => {
  const obs = P.observationsFromFeedback({ domain: 'fashion', option: { label: 'A', entity: { name: 'Uniqlo' }, attributes: [{ name: 'color', value: 'navy' }, { name: 'brand', value: 'Uniqlo' }, { name: 'price', value: '$30' }] }, polarity: 1, decisionId: 'd1' });
  assert.deepEqual(obs.map((o) => [o.attribute, o.value, o.source, o.polarity]), [['entity', 'Uniqlo', 'choice', 1], ['color', 'navy', 'choice', 1]]);
  assert.deepEqual(P.observationsFromFeedback({ domain: 'technical', option: { label: 'x' }, polarity: 1 }), []);
});
