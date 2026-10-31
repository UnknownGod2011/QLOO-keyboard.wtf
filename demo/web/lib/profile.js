// Taste-profile math shared by the browser (localStorage profile) and the server
// (preference matching). Pure functions only: no I/O, no globals, no network.

export const DOMAINS = Object.freeze([
  'movies', 'tv', 'music', 'books', 'podcasts', 'games', 'fashion', 'brands',
  'dining', 'travel', 'hotels', 'lifestyle', 'people', 'technical', 'other',
]);

// Domain -> Qloo entity type. Domains without a type (technical, other, lifestyle)
// never trigger Qloo calls.
export const DOMAIN_TYPE = Object.freeze({
  movies: 'urn:entity:movie',
  tv: 'urn:entity:tv_show',
  music: 'urn:entity:artist',
  books: 'urn:entity:book',
  podcasts: 'urn:entity:podcast',
  games: 'urn:entity:video_game',
  dining: 'urn:entity:place',
  hotels: 'urn:entity:place',
  travel: 'urn:entity:destination',
  fashion: 'urn:entity:brand',
  brands: 'urn:entity:brand',
  people: 'urn:entity:person',
});

export const WEIGHTS = Object.freeze({ explicit: 1.0, choice: 0.6, mention: 0.35, rejection: 0.6 });
export const SOURCES = Object.freeze(Object.keys(WEIGHTS));
export const HALF_LIFE_DAYS = Object.freeze({ explicit: 365, inferred: 120, contextual: 120 });
export const TEMPORARY_DEFAULT_DAYS = 14;
export const DUPLICATE_WINDOW_MS = 24 * 3600 * 1000;
export const LIMITS = Object.freeze({ maxPreferences: 200, maxObservations: 40, maxText: 80, maxQuote: 160 });
const DAY_MS = 86400000;

// Attributes whose values are cultural entities Qloo can resolve via /search.
export const ENTITY_ATTRIBUTES = Object.freeze(['entity', 'brand', 'artist', 'band', 'title', 'movie', 'show', 'book', 'author', 'podcast', 'game', 'restaurant', 'hotel', 'place', 'destination']);
// Attributes whose values are style/genre words Qloo can resolve via /v2/tags.
export const STYLE_ATTRIBUTES = Object.freeze(['style', 'genre', 'theme', 'mood', 'aesthetic', 'vibe', 'cuisine', 'tag', 'keyword']);

// ---------------------------------------------------------------- text helpers

export function normalizeText(s) {
  return String(s ?? '')
    .normalize('NFKD').replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/colour/g, 'color')
    .replace(/grey/g, 'gray')
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

export function cleanText(s, max = LIMITS.maxText) {
  return String(s ?? '').replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, max);
}

const STOPWORDS = new Set([
  'a', 'an', 'the', 'and', 'or', 'of', 'with', 'for', 'in', 'on', 'to', 'my', 'i', 'me', 'very', 'really',
  'style', 'color', 'tone', 'brand', 'like', 'stuff', 'thing', 'kind', 'type', 'genre', 'movie', 'film',
  'music', 'show', 'book', 'vibe', 'look', 'wear', 'clothe', 'clothing', 'item', 'piece',
]);
const NEGATORS = new Set(['no', 'without', 'non', 'zero', 'not']);

function stem(t) {
  if (t.length > 3 && t.endsWith('s') && !t.endsWith('ss')) return t.slice(0, -1);
  return t;
}

/** Significant, stemmed tokens. Tokens directly after "no"/"without" are dropped ("no logo"). */
export function tokens(s, { dropNegated = false } = {}) {
  const raw = normalizeText(s).split(' ').filter(Boolean);
  const out = [];
  for (let i = 0; i < raw.length; i++) {
    if (dropNegated && i > 0 && NEGATORS.has(raw[i - 1])) continue;
    if (NEGATORS.has(raw[i])) continue;
    out.push(stem(raw[i]));
  }
  return out;
}

function tokenMatch(a, b) {
  if (a === b) return true;
  return Math.min(a.length, b.length) >= 4 && (a.startsWith(b) || b.startsWith(a));
}

/**
 * Does a preference value occur in an option's text?
 * Entity-like values need the whole phrase; style/attribute values need at least
 * half of their significant tokens (so "loud logos" matches "large logo").
 */
export function valueMatches(prefValue, optionText, attribute = '') {
  const value = normalizeText(prefValue);
  const text = normalizeText(optionText);
  if (!value || !text) return false;
  if (ENTITY_ATTRIBUTES.includes(attribute)) return ` ${text} `.includes(` ${value} `);
  let pt = tokens(value).filter((t) => !STOPWORDS.has(t));
  if (!pt.length) pt = tokens(value);
  const ot = tokens(text, { dropNegated: true });
  if (!pt.length || !ot.length) return false;
  const matched = pt.filter((t) => ot.some((o) => tokenMatch(t, o))).length;
  return matched >= 1 && matched / pt.length >= 0.5;
}

// ----------------------------------------------------------- sensitive data

const SENSITIVE_PATTERNS = [
  { category: 'health', label: 'health', re: /\b(health|medical|medication|diagnos\w*|disease|illness|diabet\w*|cancer|hiv|pregnan\w*|disabilit\w*|depression|anxiety disorder|adhd|autism|therapist|prescription|allerg\w*|surgery|blood type)\b/ },
  { category: 'religion', label: 'religion', re: /\b(religion|religious|my faith|muslim|islam\w*|jewish|judaism|hindu\w*|buddhis\w*|atheis\w*|catholic\w*|protestant|sikh\w*|mormon\w*)\b/ },
  { category: 'politics', label: 'politics', re: /\b(politics|political (party|view\w*|opinion\w*|affiliation)|democrat\w*|republican\w*|vote for|voting|voter|election\w*|left wing|right wing|labour party|conservative party|liberal party)\b/ },
  { category: 'sexuality', label: 'sexuality', re: /\b(sexuality|sexual orientation|sexual preference\w*|gay|lesbian|bisexual|transgender|lgbt\w*|heterosexual|homosexual)\b/ },
  { category: 'ethnicity', label: 'ethnicity or race', re: /\b(ethnicity|ethnic origin|racial|skin color)\b/ },
  { category: 'finances', label: 'finances', re: /\b(salary|income|debt\w*|loan\w*|mortgage|credit score|bank account|net worth|finances|bankrupt\w*|paycheck)\b/ },
  { category: 'contact', label: 'contact details', re: /\b(home address|my address|street address|phone number|email address|postcode|zip code)\b/ },
  { category: 'immigration', label: 'immigration status', re: /\b(immigra\w*|visa status|citizenship|asylum|refugee status|undocumented|deportation)\b/ },
  { category: 'criminal', label: 'criminal record', re: /\b(criminal record|convicted|conviction\w*|felon\w*|parole|jail time|prison record)\b/ },
];
const SENSITIVE_ATTRIBUTES = {
  health: 'health', medical: 'health', religion: 'religion', politics: 'politics', political: 'politics',
  sexuality: 'sexuality', 'sexual orientation': 'sexuality', ethnicity: 'ethnicity', race: 'ethnicity',
  finances: 'finances', income: 'finances', salary: 'finances', debt: 'finances', address: 'contact',
  phone: 'contact', email: 'contact', immigration: 'immigration', citizenship: 'immigration',
  'criminal record': 'criminal', criminal: 'criminal',
};
const EMAIL_RE = /[^\s@]+@[^\s@]+\.[a-z]{2,}/i;
const PHONE_RE = /\+?\d[\d\s().-]{7,}\d/;

/** Returns null when safe, else {category, label}. */
export function checkSensitive(attribute, value) {
  const attr = String(attribute ?? '').toLowerCase().trim();
  if (SENSITIVE_ATTRIBUTES[attr]) {
    const category = SENSITIVE_ATTRIBUTES[attr];
    return { category, label: SENSITIVE_PATTERNS.find((p) => p.category === category)?.label || category };
  }
  const raw = String(value ?? '');
  if (EMAIL_RE.test(raw) || PHONE_RE.test(raw)) return { category: 'contact', label: 'contact details' };
  const text = ` ${normalizeText(`${attr} ${raw}`)} `;
  for (const p of SENSITIVE_PATTERNS) if (p.re.test(text)) return { category: p.category, label: p.label };
  return null;
}

export function sensitiveMessage(hit) {
  return `Not saved: this looks like sensitive personal information (${hit.label}). Taste memory only stores tastes such as titles, brands, styles and genres.`;
}

// --------------------------------------------------------- confidence maths

function clamp(n, lo, hi) { return Math.min(hi, Math.max(lo, Number.isFinite(n) ? n : 0)); }

export function halfLifeDays(obs, pref) {
  if (pref?.context) return HALF_LIFE_DAYS.contextual;
  return obs?.source === 'explicit' ? HALF_LIFE_DAYS.explicit : HALF_LIFE_DAYS.inferred;
}

export function decay(ageMs, halfLife) {
  if (!(ageMs > 0)) return 1;
  return Math.pow(0.5, ageMs / (halfLife * DAY_MS));
}

/** net = Σ polarity · weight · decay(age) */
export function netEvidence(pref, now = Date.now()) {
  let net = 0;
  for (const o of pref?.observations || []) {
    const at = Date.parse(o.at);
    const age = Number.isFinite(at) ? now - at : 0;
    net += (o.polarity < 0 ? -1 : 1) * clamp(Number(o.weight), 0, 1) * decay(age, halfLifeDays(o, pref));
  }
  return net;
}

export function confidenceFromNet(net) { return 1 - Math.exp(-1.2 * Math.abs(net)); }
export function prefConfidence(pref, now = Date.now()) { return confidenceFromNet(netEvidence(pref, now)); }
export function confidenceLabel(c) { return c >= 0.65 ? 'strong' : c >= 0.45 ? 'moderate' : 'tentative'; }

export function isExpired(pref, now = Date.now()) {
  if (!pref?.expiresAt) return false;
  const t = Date.parse(pref.expiresAt);
  return Number.isFinite(t) && t <= now;
}

export function deriveKind(pref) {
  if (pref.expiresAt) return 'temporary';
  if (pref.context) return 'contextual';
  if ((pref.observations || []).some((o) => o.source === 'explicit')) return 'explicit';
  return 'inferred';
}

export function prefKey(domain, attribute, value, context = '') {
  return [domain, normalizeText(attribute), normalizeText(value), normalizeText(context)].join('|');
}

function fingerprintOf(key, polarity, source, quote) {
  const s = `${key}|${polarity}|${source}|${normalizeText(quote)}`;
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); }
  return `fp_${(h >>> 0).toString(36)}`;
}

export function statementOf(pref) {
  const verb = pref.polarity < 0 ? 'dislikes' : 'likes';
  const attr = pref.attribute && pref.attribute !== 'entity' && pref.attribute !== 'general' ? ` (${pref.attribute})` : '';
  const ctx = pref.context ? ` for ${pref.context}` : '';
  return `${verb} ${pref.value}${attr}${ctx}`;
}

// --------------------------------------------------------------- profile ops

export function emptyProfile(owner = 'You') {
  return { version: 1, owner, seq: 0, preferences: [], updatedAt: null };
}

function cloneProfile(profile) {
  const p = profile && typeof profile === 'object' ? JSON.parse(JSON.stringify(profile)) : emptyProfile();
  if (!Array.isArray(p.preferences)) p.preferences = [];
  if (!Number.isFinite(p.seq)) p.seq = p.preferences.length;
  return p;
}

/**
 * Add one observation. Returns {profile, status, pref?, message}.
 * status: added | changed | duplicate | refused | invalid
 */
export function addObservation(profile, input, now = Date.now()) {
  const value = cleanText(input?.value);
  if (!value) return { profile, status: 'invalid', message: 'Please enter something you like or dislike.' };
  const attribute = (cleanText(input.attribute || 'general', 40).toLowerCase()) || 'general';
  const hit = checkSensitive(attribute, value) || checkSensitive('', input.quote || '') || checkSensitive('', input.context || '');
  if (hit) return { profile, status: 'refused', category: hit.category, message: sensitiveMessage(hit) };

  const p = cloneProfile(profile);
  const domain = DOMAINS.includes(input.domain) ? input.domain : 'lifestyle';
  const source = SOURCES.includes(input.source) ? input.source : 'explicit';
  let polarity = Number(input.polarity) < 0 ? -1 : 1;
  if (source === 'rejection') polarity = -1;
  const context = cleanText(input.context || '', 40).toLowerCase();
  const key = prefKey(domain, attribute, value, context);
  const atIso = new Date(now).toISOString();
  const weight = clamp(Number(input.weight ?? WEIGHTS[source]), 0, 1);
  const quote = cleanText(input.quote || '', LIMITS.maxQuote);
  const fingerprint = input.fingerprint ? cleanText(input.fingerprint, 120) : fingerprintOf(key, polarity, source, quote);

  let pref = p.preferences.find((x) => prefKey(x.domain, x.attribute, x.value, x.context) === key);
  if (pref && pref.observations.some((o) => o.fingerprint === fingerprint && Math.abs(now - Date.parse(o.at)) < DUPLICATE_WINDOW_MS)) {
    return { profile, status: 'duplicate', pref, message: `Already noted in the last 24 h, so the duplicate was ignored (${statementOf(pref)}).` };
  }

  let status = 'added';
  if (!pref) {
    if (p.preferences.length >= LIMITS.maxPreferences) {
      return { profile, status: 'invalid', message: `Taste memory is full (${LIMITS.maxPreferences} preferences). Delete some first.` };
    }
    p.seq += 1;
    pref = { id: `p${p.seq}`, domain, attribute, value, polarity, kind: 'inferred', context, observations: [], createdAt: atIso };
    p.preferences.push(pref);
  } else if (source === 'explicit') {
    // A new explicit statement with opposite polarity halves older opposite evidence (no blind overwrite).
    const opposite = pref.observations.filter((o) => (o.polarity < 0 ? -1 : 1) !== polarity);
    if (opposite.length) {
      for (const o of opposite) o.weight = Math.round((o.weight / 2) * 10000) / 10000;
      pref.recentlyChanged = atIso;
      status = 'changed';
    }
  }

  pref.observations.push({ weight, polarity, at: atIso, source, quote, fingerprint });
  if (pref.observations.length > LIMITS.maxObservations) pref.observations = pref.observations.slice(-LIMITS.maxObservations);
  if (input.temporary) {
    const days = clamp(Number(input.temporaryDays || TEMPORARY_DEFAULT_DAYS), 1, 90);
    pref.expiresAt = new Date(now + days * DAY_MS).toISOString();
  }
  pref.kind = deriveKind(pref);
  const net = netEvidence(pref, now);
  pref.polarity = net > 0 ? 1 : net < 0 ? -1 : polarity;
  pref.updatedAt = atIso;
  p.updatedAt = atIso;
  const c = confidenceFromNet(net);
  const verb = status === 'changed' ? 'Updated (recently changed)' : 'Learned';
  return { profile: p, status, pref, message: `${verb}: ${statementOf(pref)} (${confidenceLabel(c)}).` };
}

export function removePreference(profile, id) {
  const p = cloneProfile(profile);
  p.preferences = p.preferences.filter((x) => x.id !== id);
  p.updatedAt = new Date().toISOString();
  return p;
}

/** Remove expired temporary preferences. */
export function pruneExpired(profile, now = Date.now()) {
  const p = cloneProfile(profile);
  p.preferences = p.preferences.filter((x) => !isExpired(x, now));
  return p;
}

/** Rebuild an untrusted profile (from localStorage or a request) into a safe, bounded shape. */
export function sanitizeProfile(input, now = Date.now()) {
  const out = emptyProfile(cleanText(input?.owner || 'You', 40));
  const list = Array.isArray(input?.preferences) ? input.preferences.slice(0, LIMITS.maxPreferences) : [];
  let seq = 0;
  for (const raw of list) {
    if (!raw || typeof raw !== 'object') continue;
    const value = cleanText(raw.value);
    const attribute = cleanText(raw.attribute || 'general', 40).toLowerCase() || 'general';
    if (!value || checkSensitive(attribute, value)) continue;
    const observations = (Array.isArray(raw.observations) ? raw.observations : []).slice(-LIMITS.maxObservations)
      .map((o) => ({
        weight: clamp(Number(o?.weight), 0, 1),
        polarity: Number(o?.polarity) < 0 ? -1 : 1,
        at: Number.isFinite(Date.parse(o?.at)) && Date.parse(o.at) <= now + DAY_MS ? new Date(Date.parse(o.at)).toISOString() : new Date(now).toISOString(),
        source: SOURCES.includes(o?.source) ? o.source : 'mention',
        quote: cleanText(o?.quote || '', LIMITS.maxQuote),
        fingerprint: cleanText(o?.fingerprint || '', 120),
      }))
      .filter((o) => o.weight > 0);
    if (!observations.length) continue;
    const idMatch = /^p(\d{1,6})$/.exec(String(raw.id || ''));
    if (idMatch) seq = Math.max(seq, Number(idMatch[1]));
    const pref = {
      id: idMatch ? raw.id : `p${++seq + 100000}`,
      domain: DOMAINS.includes(raw.domain) ? raw.domain : 'lifestyle',
      attribute,
      value,
      polarity: Number(raw.polarity) < 0 ? -1 : 1,
      kind: 'inferred',
      context: cleanText(raw.context || '', 40).toLowerCase(),
      observations,
      createdAt: Number.isFinite(Date.parse(raw.createdAt)) ? raw.createdAt : observations[0].at,
    };
    if (raw.expiresAt && Number.isFinite(Date.parse(raw.expiresAt))) pref.expiresAt = new Date(Date.parse(raw.expiresAt)).toISOString();
    if (raw.recentlyChanged && Number.isFinite(Date.parse(raw.recentlyChanged))) pref.recentlyChanged = raw.recentlyChanged;
    pref.kind = deriveKind(pref);
    const net = netEvidence(pref, now);
    if (net !== 0) pref.polarity = net > 0 ? 1 : -1;
    out.preferences.push(pref);
  }
  out.seq = Math.max(seq, Number.isFinite(input?.seq) ? Math.min(input.seq, 1e6) : 0);
  out.updatedAt = input?.updatedAt || null;
  return out;
}

export function contextMatches(prefContext, requestContext) {
  const a = normalizeText(prefContext);
  const b = normalizeText(requestContext);
  if (!a || !b) return false;
  return ` ${b} `.includes(` ${a} `) || ` ${a} `.includes(` ${b} `) || tokens(a).some((t) => tokens(b).some((u) => tokenMatch(t, u) && t.length >= 4));
}

/** Annotate a preference with computed confidence (never trust client-sent numbers). */
export function describePreference(pref, now = Date.now()) {
  const net = netEvidence(pref, now);
  const confidence = confidenceFromNet(net);
  const polarity = net > 0 ? 1 : net < 0 ? -1 : pref.polarity;
  return {
    ...pref,
    polarity,
    net: Math.round(net * 1000) / 1000,
    confidence: Math.round(confidence * 1000) / 1000,
    label: confidenceLabel(confidence),
    evidenceCount: (pref.observations || []).length,
    expired: isExpired(pref, now),
    statement: statementOf({ ...pref, polarity }),
  };
}

/**
 * Preferences relevant to a decision: same domain (or a domain sharing the same
 * Qloo type, e.g. fashion/brands) + lifestyle; contextual ones only when the
 * context matches; expired temporary ones skipped.
 */
export function selectRelevant(profile, { domain, context = '', now = Date.now(), limit = 24 } = {}) {
  if (!domain || domain === 'technical') return [];
  const type = DOMAIN_TYPE[domain];
  const out = [];
  for (const pref of profile?.preferences || []) {
    const sameDomain = pref.domain === domain || pref.domain === 'lifestyle' || (type && DOMAIN_TYPE[pref.domain] === type);
    if (!sameDomain) continue;
    if (isExpired(pref, now)) continue;
    if (pref.context && !contextMatches(pref.context, context)) continue;
    const d = describePreference(pref, now);
    if (d.confidence < 0.05) continue;
    out.push(d);
  }
  out.sort((a, b) => b.confidence - a.confidence);
  return out.slice(0, limit);
}

export function optionText(option) {
  const attrs = (option?.attributes || []).map((a) => a?.value).filter(Boolean).join(' ');
  return [option?.label, option?.description, attrs, option?.entity?.name].filter(Boolean).join(' ');
}

/**
 * Local preference matching for one option.
 * Returns {score, evidence:[{source:'profile', prefId, sign, weight, text}]}.
 * Exact entity matches (e.g. "Not for me" on this very title) count double.
 */
export function matchOption(option, prefs) {
  const text = optionText(option);
  const entityName = normalizeText(option?.entity?.name || '');
  let score = 0;
  const evidence = [];
  for (const pref of prefs) {
    const isEntity = ENTITY_ATTRIBUTES.includes(pref.attribute);
    const exactEntity = isEntity && entityName && normalizeText(pref.value) === entityName;
    if (!exactEntity && !valueMatches(pref.value, text, pref.attribute)) continue;
    const weight = Math.round(pref.polarity * pref.confidence * (exactEntity ? 2 : 1) * 1000) / 1000;
    score += weight;
    const verb = pref.polarity > 0 ? 'matches your like' : 'conflicts with your dislike';
    evidence.push({
      source: 'profile',
      prefId: pref.id,
      sign: pref.polarity > 0 ? '+' : '−',
      weight,
      text: `${verb}: "${pref.value}"${pref.context ? ` (for ${pref.context})` : ''} · ${pref.label}, ${pref.kind}${pref.recentlyChanged ? ', recently changed' : ''}`,
    });
  }
  return { score: Math.round(score * 1000) / 1000, evidence };
}

/** Observations to record when the user gives feedback on a decided option. */
export function observationsFromFeedback({ domain, option, polarity, decisionId = '' }) {
  if (!option || !DOMAINS.includes(domain) || domain === 'technical' || domain === 'other') return [];
  const source = polarity < 0 ? 'rejection' : 'choice';
  const out = [];
  const seen = new Set();
  const push = (attribute, value) => {
    const v = cleanText(value, 40);
    const k = normalizeText(v);
    if (!v || seen.has(k) || v.length > 40) return;
    seen.add(k);
    out.push({
      domain, attribute, value: v, polarity: polarity < 0 ? -1 : 1, source,
      quote: `${polarity < 0 ? 'Not for me' : "I'm choosing this"}: ${cleanText(option.label, 80)}`,
      fingerprint: `fb:${cleanText(decisionId, 40)}:${attribute}:${k}:${polarity < 0 ? -1 : 1}`,
    });
  };
  if (option.entity?.name) push('entity', option.entity.name);
  const skip = new Set(['price', 'size', 'source', 'description', 'year', 'release year', 'runtime', 'rating', 'fit']);
  for (const a of (option.attributes || []).slice(0, 8)) {
    const name = cleanText(a?.name, 30).toLowerCase();
    if (!name || skip.has(name)) continue;
    push(name === 'brand' ? 'brand' : name, a?.value);
    if (out.length >= 6) break;
  }
  return out;
}

/** Apply a list of observation inputs; returns {profile, results:[{status,message}]}. */
export function applyObservations(profile, inputs, now = Date.now()) {
  let p = profile;
  const results = [];
  for (const input of inputs) {
    const r = addObservation(p, input, now);
    p = r.profile;
    results.push({ status: r.status, message: r.message, prefId: r.pref?.id });
  }
  return { profile: p, results };
}

/** The demo persona "Alex". Seeded 20 days in the past so decay is visible but small. */
export function seedPersona(now = Date.now()) {
  const at = now - 20 * DAY_MS;
  const items = [
    ['movies', 'entity', 'Interstellar', 1],
    ['movies', 'entity', 'Arrival', 1],
    ['movies', 'entity', 'Blade Runner 2049', 1],
    ['movies', 'genre', 'horror', -1],
    ['fashion', 'style', 'minimalist', 1],
    ['fashion', 'color', 'navy', 1],
    ['fashion', 'color', 'earth tones', 1],
    ['fashion', 'brand', 'Uniqlo', 1],
    ['fashion', 'brand', 'Patagonia', 1],
    ['fashion', 'feature', 'loud logos', -1],
    ['fashion', 'color', 'neon colours', -1],
  ];
  let p = emptyProfile('Alex');
  for (const [domain, attribute, value, polarity] of items) {
    p = addObservation(p, { domain, attribute, value, polarity, source: 'explicit', quote: 'Demo persona seed' }, at).profile;
  }
  p = addObservation(p, { domain: 'fashion', attribute: 'style', value: 'formal', polarity: 1, source: 'explicit', context: 'wedding', quote: 'Demo persona seed: for weddings I prefer formal' }, at).profile;
  return p;
}
