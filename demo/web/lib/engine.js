// Personalized decision engine: taste memory + Qloo evidence + Gemini final decision.
// Server-side only (imported by api/decide.js and the tests).

import {
  DOMAIN_TYPE, ENTITY_ATTRIBUTES, STYLE_ATTRIBUTES, checkSensitive, cleanText, matchOption,
  normalizeText, sanitizeProfile, selectRelevant, valueMatches,
} from './profile.js';
import { createQlooSession, QlooError, MAX_CALLS_PER_REQUEST } from './qloo.js';
import { createGemini, normalizeExtraction } from './gemini.js';

export const QLOO_WEIGHT = 1.5;
const MAX_LIKED = 4;
const MAX_STYLE = 2;
const MAX_OPTION_LOOKUPS = 4;
const MAX_DISLIKED = 2;
const IMAGE_MIME = ['image/jpeg', 'image/png', 'image/webp'];
const MAX_IMAGE_B64 = Math.floor(1.5 * 1024 * 1024);

export class HttpError extends Error {
  constructor(status, message, extra) {
    super(message);
    this.status = status;
    this.extra = extra;
  }
}

export function qlooTypeForDomain(domain) {
  return DOMAIN_TYPE[domain] || null;
}

export function isMockEnv(env = {}) {
  return env.DEMO_MOCK === '1' || String(env.DEMO_MOCK).toLowerCase() === 'true';
}

const r3 = (n) => Math.round(n * 1000) / 1000;

function safeQlooTerm(value, maxWords = 6) {
  const v = String(value || '').trim();
  return v.length > 0 && v.length <= 60 && v.split(/\s+/).length <= maxWords && !checkSensitive('', v);
}

function localConstraintFailures(option, constraints) {
  const out = [];
  for (const c of constraints || []) {
    const m = /\b(under|below|less than|max(?:imum)?)\s*\$?\s*(\d+(?:\.\d+)?)/i.exec(c);
    if (!m) continue;
    const price = (option.attributes || []).find((a) => a.name === 'price');
    const p = price ? Number(String(price.value).replace(/[^0-9.]/g, '')) : NaN;
    if (Number.isFinite(p) && p > Number(m[2])) out.push(c);
  }
  return out;
}

function engineConfidence({ task, ranked, recommendations, grounded, signalCount }) {
  if (task === 'answer') return null;
  if (recommendations) {
    if (!grounded || !recommendations.length) return 'low';
    return signalCount >= 3 && recommendations[0].affinity >= 0.85 ? 'high' : 'medium';
  }
  if (!ranked.length) return 'low';
  const profileEvidence = ranked.some((o) => o.evidence.some((e) => e.source === 'profile'));
  const qlooEvidence = ranked.some((o) => o.affinity != null);
  if (ranked.length === 1) return profileEvidence ? 'medium' : 'low';
  const [a, b] = ranked;
  if (b.constraintFailures.length && !a.constraintFailures.length) return profileEvidence || qlooEvidence ? 'high' : 'medium';
  const margin = a.score - b.score;
  if ((margin >= 0.6 && profileEvidence && qlooEvidence) || (margin >= 1.0 && profileEvidence)) return 'high';
  if (margin >= 0.25) return 'medium';
  return 'low';
}

/** Settle a batch of Qloo lookups; cap errors become "skipped", any other error aborts Qloo. */
async function settle(promises, warnings) {
  const res = await Promise.allSettled(promises);
  const fatal = res.find((r) => r.status === 'rejected' && r.reason?.code !== 'request_cap');
  if (fatal) throw fatal.reason;
  if (res.some((r) => r.status === 'rejected')) warnings.push(`Per-request Qloo cap (${MAX_CALLS_PER_REQUEST} calls) reached; some lookups were skipped.`);
  return res.map((r) => (r.status === 'fulfilled' ? r.value : null));
}

async function qlooStage({ extraction, prefs, options, qloo, entityType, location, warnings, caveats }) {
  const isRecommend = extraction.options.length === 0;
  const out = { grounded: false, signals: [], recommendations: undefined, reason: '' };
  const liked = prefs.filter((p) => p.polarity > 0 && ENTITY_ATTRIBUTES.includes(p.attribute) && safeQlooTerm(p.value));
  const styles = prefs.filter((p) => p.polarity > 0 && STYLE_ATTRIBUTES.includes(p.attribute) && safeQlooTerm(p.value, 3));
  const disliked = isRecommend ? prefs.filter((p) => p.polarity < 0 && ENTITY_ATTRIBUTES.includes(p.attribute) && safeQlooTerm(p.value)) : [];
  const lookups = isRecommend ? [] : options.filter((o) => o.entityName && safeQlooTerm(o.entityName)).slice(0, MAX_OPTION_LOOKUPS);

  if (!liked.length && !styles.length) {
    out.reason = 'Cold start: your taste memory has no liked titles, brands or style words for this domain yet, so there was nothing for Qloo to score against. No Qloo calls were made.';
    caveats.push('Cold start: no taste signals for Qloo in this domain yet.');
    return out;
  }
  if (!isRecommend && !lookups.length) {
    out.reason = 'None of the options names a brand, title or place Qloo could look up (e.g. unbranded items), so Qloo was not called. Decided from taste memory and Gemini judgement.';
    caveats.push('Qloo could not score these options: none of them is a named brand/title/place.');
    return out;
  }

  // Deterministic call budget: 1 insights call reserved; options > liked > style > disliked.
  let remaining = qloo.remainingCalls() - 1;
  const nOpt = Math.max(0, Math.min(lookups.length, remaining)); remaining -= nOpt;
  const nLiked = Math.max(0, Math.min(liked.length, MAX_LIKED, remaining)); remaining -= nLiked;
  const nStyle = Math.max(0, Math.min(styles.length, MAX_STYLE, remaining)); remaining -= nStyle;
  const nDis = Math.max(0, Math.min(disliked.length, MAX_DISLIKED, remaining));

  const all = await settle([
    ...lookups.slice(0, nOpt).map((o) => qloo.search(o.entityName, entityType)),
    ...liked.slice(0, nLiked).map((p) => qloo.search(p.value, entityType)),
    ...styles.slice(0, nStyle).map((p) => qloo.tag(p.value, entityType)),
    ...disliked.slice(0, nDis).map((p) => qloo.search(p.value, entityType)),
  ], warnings);
  const optRes = all.slice(0, nOpt);
  const likedRes = all.slice(nOpt, nOpt + nLiked);
  const styleRes = all.slice(nOpt + nLiked, nOpt + nLiked + nStyle);
  const disRes = all.slice(nOpt + nLiked + nStyle);

  lookups.slice(0, nOpt).forEach((o, i) => {
    const r = optRes[i];
    if (r) o.qlooEntity = { id: r.id, name: r.name, disambiguation: r.disambiguation };
    else caveats.push(`Qloo could not resolve the option "${o.entityName}".`);
  });
  const optionIds = new Set(options.filter((o) => o.qlooEntity).map((o) => o.qlooEntity.id));
  const unresolved = [];
  liked.slice(0, nLiked).forEach((p, i) => {
    const r = likedRes[i];
    if (!r) { unresolved.push(p.value); return; }
    if (optionIds.has(r.id)) {
      caveats.push(`${r.name} is one of the options, so it was not used as a Qloo signal (avoids self-confirmation); it still counts as a direct taste-memory match.`);
      return;
    }
    if (!out.signals.some((s) => s.id === r.id)) out.signals.push({ name: r.name, id: r.id, kind: 'entity', prefId: p.id });
  });
  styles.slice(0, nStyle).forEach((p, i) => {
    const t = styleRes[i];
    if (t) out.signals.push({ name: t.name, id: t.id, kind: 'tag', prefId: p.id });
    else unresolved.push(`${p.value} (no matching tag)`);
  });
  if (unresolved.length) caveats.push(`Qloo could not resolve: ${unresolved.join(', ')}.`);
  const entitySignals = out.signals.filter((s) => s.kind === 'entity');
  const tagSignals = out.signals.filter((s) => s.kind === 'tag');
  const nameById = new Map(out.signals.map((s) => [s.id, s.name]));

  if (!out.signals.length) {
    out.reason = 'None of your taste signals could be resolved by Qloo, so no insights call was made.';
    return out;
  }
  if (entityType === 'urn:entity:destination' && !entitySignals.length) {
    out.reason = 'Qloo destination insights need at least one liked destination entity; none resolved.';
    caveats.push('Add a liked destination to get Qloo-grounded travel picks.');
    return out;
  }
  const base = {
    'filter.type': entityType,
    'signal.interests.entities': entitySignals.map((s) => s.id).sort(),
    'signal.interests.tags': tagSignals.map((s) => s.id).sort(),
  };
  const explainText = (explain) => (explain || []).filter((x) => nameById.has(x.id) && x.score > 0).slice(0, 2).map((x) => `${nameById.get(x.id)} ${x.score}`).join(', ');

  if (!isRecommend) {
    const candidates = options.filter((o) => o.qlooEntity);
    if (!candidates.length) {
      out.reason = 'Qloo could not resolve any option to a known entity, so it did not score them.';
      return out;
    }
    const ids = [...new Set(candidates.map((o) => o.qlooEntity.id))].sort();
    const results = await settle([qloo.insights({ ...base, 'filter.results.entities': ids, take: ids.length })], warnings);
    const byId = new Map((results[0] || []).map((e) => [e.id, e]));
    for (const o of candidates) {
      const e = byId.get(o.qlooEntity.id);
      if (!e || e.affinity == null) { caveats.push(`Qloo returned no affinity for ${o.qlooEntity.name}.`); continue; }
      o.affinity = e.affinity;
      o.explain = explainText(e.explain);
    }
    out.grounded = options.some((o) => o.affinity != null);
    out.reason = out.grounded
      ? `Qloo scored ${options.filter((o) => o.affinity != null).length} of ${options.length} options against ${out.signals.length} taste signal(s) using filter.results.entities.`
      : 'Qloo answered but returned no affinity for the options.';
    return out;
  }

  // Recommend mode: no options given -> Qloo recommendations.
  if (entityType === 'urn:entity:place' && !location) {
    out.reason = 'Place recommendations need a city; add a location to get Qloo-grounded picks.';
    caveats.push('No city given, so Qloo place recommendations were skipped.');
    return out;
  }
  const exclude = new Set(entitySignals.map((s) => s.id));
  disliked.slice(0, nDis).forEach((p, i) => { if (disRes[i]) exclude.add(disRes[i].id); });
  const params = { ...base, 'filter.exclude.entities': [...exclude].sort(), take: 5 };
  if (entityType === 'urn:entity:place') params['filter.location.query'] = location;
  const results = (await settle([qloo.insights(params)], warnings))[0] || [];
  const strongDislikes = prefs.filter((p) => p.polarity < 0 && !ENTITY_ATTRIBUTES.includes(p.attribute) && (p.label === 'strong' || p.kind === 'explicit'));
  const recs = [];
  for (const e of results) {
    const text = `${e.tags.map((t) => t.name).join(' ')} ${e.description}`;
    const clash = strongDislikes.find((p) => valueMatches(p.value, text, p.attribute));
    if (clash) { caveats.push(`Dropped ${e.name}: it is tagged "${clash.value}", which you dislike.`); continue; }
    const m = matchOption({ label: e.name, description: e.description, attributes: e.tags.map((t) => ({ name: 'tag', value: t.name })), entity: { name: e.name } }, prefs);
    recs.push({ name: e.name, id: e.id, affinity: e.affinity, description: e.description, tags: e.tags.map((t) => t.name).slice(0, 4), why: explainText(e.explain), prefScore: m.score, evidence: m.evidence });
  }
  out.recommendations = recs;
  out.grounded = recs.length > 0;
  out.reason = out.grounded
    ? `Qloo recommended ${recs.length} ${entityType.replace('urn:entity:', '')} entities from ${out.signals.length} taste signal(s), excluding ${exclude.size} already-liked or disliked entities.`
    : 'Qloo returned no recommendations that fit your taste memory.';
  return out;
}

/**
 * Run the taste-aware engine. Returns the engine summary plus the brief for Gemini.
 */
export async function runEngine({ extraction, profile, qloo, now = Date.now(), location = '' }) {
  const warnings = [];
  const caveats = [];
  const domain = extraction.domain;
  const entityType = qlooTypeForDomain(domain);
  const qlooRelevant = !!entityType;
  const prefs = selectRelevant(profile, { domain, context: extraction.context, now });

  const options = extraction.options.map((o, index) => {
    const m = matchOption(o, prefs);
    return {
      index, label: o.label, entityName: o.entity?.name || null, qlooEntity: null, affinity: null, qlooDelta: 0, explain: '',
      prefScore: m.score, evidence: m.evidence,
      constraintFailures: [...new Set([...(o.fails_constraints || []), ...localConstraintFailures(o, extraction.constraints)])],
    };
  });

  let stage = { grounded: false, signals: [], recommendations: undefined, reason: '' };
  let qlooFailed = false;
  if (!qlooRelevant) {
    stage.reason = domain === 'technical'
      ? 'Technical/factual question: taste data is irrelevant, so no Qloo calls were made.'
      : `The "${domain}" domain has no Qloo entity type, so no Qloo calls were made.`;
  } else if (!qloo || !qloo.configured) {
    qlooFailed = true;
    stage.reason = 'Qloo is not configured on this deployment, so the decision uses taste memory only.';
    warnings.push('Qloo unavailable (not configured): this result is based on taste memory only and is NOT Qloo-grounded.');
  } else if (qloo.disabled) {
    qlooFailed = true;
    stage.reason = 'Simulated Qloo outage: the decision uses taste memory only.';
    warnings.push('Qloo unavailable (simulated outage): this result is based on taste memory only and is NOT Qloo-grounded.');
  } else {
    try {
      stage = await qlooStage({ extraction, prefs, options, qloo, entityType, location, warnings, caveats });
    } catch (e) {
      qlooFailed = true;
      const code = e instanceof QlooError ? e.code : 'error';
      for (const o of options) { o.affinity = null; o.explain = ''; }
      stage = { grounded: false, signals: [], recommendations: undefined, reason: `Qloo failed (${code}), so the decision fell back to taste memory only.` };
      warnings.push(code === 'daily_budget'
        ? 'The daily Qloo budget for this demo instance is used up: taste memory only, NOT Qloo-grounded.'
        : `Qloo unavailable (${code}): this result is based on taste memory only and is NOT Qloo-grounded.`);
    }
  }

  // Combine: preference score + centred Qloo affinity; constraint failures rank last.
  const scored = options.filter((o) => o.affinity != null);
  const mean = scored.length ? scored.reduce((s, o) => s + o.affinity, 0) / scored.length : 0;
  for (const o of options) {
    if (o.affinity != null && scored.length >= 2) {
      o.qlooDelta = r3(o.affinity - mean);
      o.evidence.push({
        source: 'qloo', sign: o.qlooDelta >= 0 ? '+' : '−', weight: r3(QLOO_WEIGHT * o.qlooDelta),
        text: `Qloo affinity ${o.affinity} for ${o.qlooEntity.name} (${o.qlooDelta >= 0 ? '+' : ''}${o.qlooDelta} vs. the options' average)${o.explain ? ` · strongest links: ${o.explain}` : ''}`,
      });
    } else if (o.affinity != null) {
      o.evidence.push({ source: 'qloo', sign: '·', weight: 0, text: `Qloo affinity ${o.affinity} for ${o.qlooEntity.name}; no other option could be scored by Qloo, so it did not change the ranking${o.explain ? ` · strongest links: ${o.explain}` : ''}` });
    }
    for (const c of o.constraintFailures) o.evidence.push({ source: 'constraint', sign: '−', weight: 0, text: `fails constraint: ${c}` });
    o.score = r3(o.prefScore + QLOO_WEIGHT * o.qlooDelta);
  }
  const ranked = [...options].sort((a, b) => (a.constraintFailures.length > 0) - (b.constraintFailures.length > 0) || b.score - a.score || a.index - b.index);
  ranked.forEach((o, i) => { o.rank = i + 1; });
  if (scored.length === 1 && options.length > 1) caveats.push('Qloo could only score one option, so its affinity did not change the ranking.');

  const task = extraction.task;
  const confidence = engineConfidence({ task, ranked, recommendations: stage.recommendations, grounded: stage.grounded, signalCount: stage.signals.length });
  const stats = qloo?.stats ? qloo.stats() : { calls: 0, cacheHits: 0, log: [] };
  const usedPrefIds = new Set([
    ...options.flatMap((o) => o.evidence.filter((e) => e.prefId).map((e) => e.prefId)),
    ...stage.signals.map((s) => s.prefId),
    ...(stage.recommendations || []).flatMap((r) => r.evidence.map((e) => e.prefId)),
  ]);

  const brief = {
    task,
    domain,
    context: extraction.context,
    qloo_relevant: qlooRelevant,
    qloo_grounded: stage.grounded,
    qloo_reason: stage.reason,
    relevant_preferences: prefs.slice(0, 16).map((p) => ({ id: p.id, statement: p.statement, value: p.value, strength: p.label, kind: p.kind, polarity: p.polarity, recently_changed: !!p.recentlyChanged })),
    qloo_signals: stage.signals.map((s) => `${s.name} (${s.kind})`),
    options: ranked.map((o) => ({
      label: o.label,
      rank: o.rank,
      engine_score: o.score,
      profile_evidence: o.evidence.filter((e) => e.source === 'profile').map((e) => ({ pref_id: e.prefId, sign: e.sign, text: e.text })),
      qloo_entity: o.qlooEntity?.name || null,
      qloo_affinity: o.affinity,
      qloo_vs_average: o.affinity != null && scored.length >= 2 ? o.qlooDelta : null,
      qloo_strongest_links: o.explain || null,
      constraint_failures: o.constraintFailures,
    })),
    recommendations: stage.recommendations ? stage.recommendations.map((r) => ({ name: r.name, affinity: r.affinity, description: r.description, tags: r.tags, why: r.why || null })) : undefined,
    engine_top_choice: ranked[0]?.label || stage.recommendations?.[0]?.name || null,
    engine_confidence: confidence,
    caveats: [...warnings, ...caveats],
    reminder: 'Qloo does not judge colour, fit, size, material or visual appearance; judge those from the option descriptions.',
  };

  return {
    qlooRelevant,
    qlooGrounded: stage.grounded,
    qlooFailed,
    reason: stage.reason,
    entityType,
    calls: stats.calls,
    cacheHits: stats.cacheHits,
    callLog: stats.log,
    signals: stage.signals.map(({ name, id, kind }) => ({ name, id, kind })),
    options: options.map((o) => ({
      label: o.label, rank: o.rank, score: o.score, qlooEntity: o.qlooEntity, affinity: o.affinity, qlooDelta: o.affinity != null && scored.length >= 2 ? o.qlooDelta : null,
      prefScore: o.prefScore, evidence: o.evidence, constraintFailures: o.constraintFailures,
    })),
    ...(stage.recommendations ? { recommendations: stage.recommendations } : {}),
    preferencesConsidered: prefs.length,
    preferencesUsed: [...usedPrefIds].filter(Boolean).length,
    confidence,
    caveats,
    warnings,
    brief,
  };
}

/** Map Gemini's free-text choice back to an option or recommendation index. */
export function matchChoice(choice, options = [], recommendations = []) {
  const c = normalizeText(choice);
  if (!c) return null;
  const find = (list, nameOf) => {
    let i = list.findIndex((x) => normalizeText(nameOf(x)) === c);
    if (i < 0) i = list.findIndex((x) => { const n = normalizeText(nameOf(x)); return n && ` ${c} `.includes(` ${n} `); });
    if (i < 0) i = list.findIndex((x) => { const n = normalizeText(nameOf(x)); return n && c.length >= 3 && n.includes(c); });
    return i;
  };
  let i = -1;
  if (options.length && options.every((o) => /^[A-Z1-9]\s*[:).-]/.test(o.label))) {
    const m = /^(?:option\s+)?([a-z1-9])(?:\s|$)/.exec(c);
    if (m) i = options.findIndex((o) => o.label[0].toLowerCase() === m[1]);
  }
  if (i < 0) i = find(options, (o) => o.label);
  if (i < 0) i = find(options, (o) => o.entity?.name || '');
  if (i >= 0) return { kind: 'option', index: i };
  const j = find(recommendations || [], (r) => r.name);
  return j >= 0 ? { kind: 'recommendation', index: j } : null;
}

/** Server-side guard: never show Qloo citations when not grounded; resolve preference ids. */
export function enforceGrounding(decision, engine) {
  const d = { ...decision };
  const prefs = engine?.brief?.relevant_preferences || [];
  const byId = new Map(prefs.map((p) => [p.id, p]));
  if (!engine?.qlooGrounded && d.cited_qloo.length) {
    d.cited_qloo = [];
    d.groundingNote = 'Qloo citations were removed because this decision was not Qloo-grounded.';
  }
  const cited = [];
  for (const x of d.cited_preferences || []) {
    const id = typeof x === 'string' ? x.trim() : x?.id;
    let p = byId.get(id);
    if (!p && typeof x === 'string') p = prefs.find((q) => normalizeText(x).includes(normalizeText(q.value)));
    if (p && !cited.some((c) => c.id === p.id)) cited.push({ id: p.id, statement: p.statement, strength: p.strength });
  }
  d.cited_preferences = cited;
  return d;
}

function offEngine(extraction) {
  return {
    qlooRelevant: false,
    qlooGrounded: false,
    qlooFailed: false,
    reason: 'Qloo OFF: original Jarvis behaviour. Gemini only, no taste memory, no Qloo calls.',
    entityType: null,
    calls: 0,
    cacheHits: 0,
    signals: [],
    options: extraction.options.map((o) => ({ label: o.label, qlooEntity: null, affinity: null, prefScore: null, evidence: [] })),
    preferencesConsidered: 0,
    preferencesUsed: 0,
    confidence: null,
    caveats: [],
    warnings: [],
  };
}

function multiline(s, max) {
  return String(s ?? '').replace(/[\u0000-\u0009\u000b-\u001f\u007f]/g, ' ').trim().slice(0, max);
}

/** Validate and bound the untrusted request body. Throws HttpError(400). */
export function validateRequest(body) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new HttpError(400, 'Expected a JSON object body.');
  const question = cleanText(body.question, 1000);
  if (!question) throw new HttpError(400, 'Please type a question.');
  const mode = body.mode;
  if (mode !== 'off' && mode !== 'on') throw new HttpError(400, 'mode must be "off" or "on".');
  if (body.optionsText != null && typeof body.optionsText !== 'string') throw new HttpError(400, 'optionsText must be a string.');
  const optionsText = multiline(body.optionsText || '', 2000);
  const rawImages = body.images == null ? [] : body.images;
  if (!Array.isArray(rawImages)) throw new HttpError(400, 'images must be an array.');
  if (rawImages.length > 3) throw new HttpError(400, 'At most 3 images per question.');
  const images = rawImages.map((img, i) => {
    const mimeType = String(img?.mimeType || '').toLowerCase();
    if (!IMAGE_MIME.includes(mimeType)) throw new HttpError(400, `Image ${i + 1}: only JPEG, PNG or WebP are supported.`);
    const data = String(img?.data || '').replace(/^data:[^,]*,/, '').replace(/\s+/g, '');
    if (!data || data.length > MAX_IMAGE_B64) throw new HttpError(400, `Image ${i + 1} is empty or larger than 1.5 MB (base64).`);
    if (!/^[A-Za-z0-9+/]+={0,2}$/.test(data)) throw new HttpError(400, `Image ${i + 1} is not valid base64.`);
    return { mimeType, data };
  });
  const profile = body.profile && typeof body.profile === 'object' && !Array.isArray(body.profile) ? body.profile : null;
  const extraction = body.extraction && typeof body.extraction === 'object' && !Array.isArray(body.extraction) ? body.extraction : null;
  return {
    question,
    mode,
    optionsText,
    images,
    profile,
    extraction,
    location: cleanText(body.location || '', 80),
    simulateQlooOutage: body.simulateQlooOutage === true,
  };
}

/**
 * Full decision flow used by api/decide.js and the tests.
 * deps: {env, qlooFetch, geminiFetch, now, sleep, gemini}
 */
export async function decide(body, deps = {}) {
  const t0 = Date.now();
  const env = deps.env || {};
  const mock = isMockEnv(env);
  const now = deps.now ? deps.now() : Date.now();
  const input = validateRequest(body);
  const gemini = deps.gemini || createGemini({ env, mock, fetchImpl: deps.geminiFetch });
  if (!gemini.configured) {
    throw new HttpError(503, 'This deployment has no Gemini key configured, so Jarvis cannot decide. Set GEMINI_API_KEY (or DEMO_MOCK=1 for the offline demo).');
  }
  const timings = {};
  const geminiFail = (what) => new HttpError(502, `Jarvis could not reach the language model (Gemini) to ${what}. Please try again in a moment.`);

  let extraction;
  let t = Date.now();
  if (input.extraction) {
    extraction = normalizeExtraction(input.extraction);
    timings.extractMs = 0;
    timings.extractionReused = true;
  } else {
    try {
      extraction = await gemini.extract({ question: input.question, optionsText: input.optionsText, images: input.images, location: input.location });
    } catch {
      throw geminiFail('read your question');
    }
    timings.extractMs = Date.now() - t;
  }
  if (input.location && !extraction.location) extraction.location = input.location;

  if (input.mode === 'off') {
    t = Date.now();
    let decision;
    try { decision = await gemini.decideOff({ question: input.question, extraction }); } catch { throw geminiFail('decide'); }
    timings.finalMs = Date.now() - t;
    decision.cited_preferences = [];
    decision.cited_qloo = [];
    decision.choiceRef = matchChoice(decision.choice, extraction.options);
    return { mode: 'off', mock, extraction, engine: offEngine(extraction), decision, timingMs: Date.now() - t0, timings };
  }

  const profile = sanitizeProfile(input.profile || {}, now);
  const qloo = createQlooSession({ env, mock, fetchImpl: deps.qlooFetch, disabled: input.simulateQlooOutage, now: deps.now, sleep: deps.sleep });
  t = Date.now();
  const engine = await runEngine({ extraction, profile, qloo, now, location: extraction.location || input.location });
  timings.engineMs = Date.now() - t;
  t = Date.now();
  let decision;
  try { decision = await gemini.decideOn({ question: input.question, extraction, brief: engine.brief }); } catch { throw geminiFail('decide'); }
  timings.finalMs = Date.now() - t;
  decision = enforceGrounding(decision, engine);
  decision.choiceRef = matchChoice(decision.choice, extraction.options, engine.recommendations);
  return { mode: 'on', mock, extraction, engine, decision, timingMs: Date.now() - t0, timings };
}

/** Sliding-window per-key limiter (per serverless instance). */
export function createRateLimiter({ limit = 10, windowMs = 10 * 60 * 1000, maxKeys = 5000 } = {}) {
  const hits = new Map();
  return function check(key, now = Date.now()) {
    const k = String(key || 'unknown');
    const arr = (hits.get(k) || []).filter((ts) => now - ts < windowMs);
    if (arr.length >= limit) {
      hits.set(k, arr);
      return { ok: false, retryAfterSec: Math.max(1, Math.ceil((windowMs - (now - arr[0])) / 1000)) };
    }
    arr.push(now);
    hits.delete(k);
    hits.set(k, arr);
    while (hits.size > maxKeys) hits.delete(hits.keys().next().value);
    return { ok: true, remaining: limit - arr.length };
  };
}
