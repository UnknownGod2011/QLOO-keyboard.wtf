// Qloo Taste AI client (hackathon API) with quota protection.
//
// Quota protection is PER SERVERLESS INSTANCE (module-level memory). Vercel may run
// several instances, and each cold start begins with an empty cache and budget.
//   - TTL cache keyed by canonical URL (the API key is a header, never in the key)
//     search/tags 24 h, insights 6 h; plus in-flight de-duplication
//   - per-request cap (default 8 HTTP calls, retries included)
//   - per-instance daily cap (QLOO_DAILY_BUDGET, default 300)
//   - 8 s timeout per attempt, one retry with backoff only on 429/5xx/timeout
// The key is never logged or returned.

import { mockQlooFetch } from './fixtures.js';
import { normalizeText, tokens } from './profile.js';

export const DEFAULT_QLOO_BASE = 'https://hackathon.api.qloo.com';
export const TTL_MS = Object.freeze({ search: 24 * 3600e3, tags: 24 * 3600e3, insights: 6 * 3600e3 });
export const MAX_CALLS_PER_REQUEST = 8;
const MAX_CACHE_ENTRIES = 500;

const shared = { cache: new Map(), inflight: new Map(), budget: { day: '', used: 0 } };

export class QlooError extends Error {
  constructor(code, message, status) {
    super(message);
    this.name = 'QlooError';
    this.code = code;
    this.status = status || 0;
  }
}

/** Test helper: clear the per-instance cache, in-flight map and daily budget. */
export function resetQlooState() {
  shared.cache.clear();
  shared.inflight.clear();
  shared.budget = { day: '', used: 0 };
}

export function instanceBudget() {
  return { day: shared.budget.day, used: shared.budget.used, cacheEntries: shared.cache.size };
}

function enc(v) {
  return encodeURIComponent(v).replace(/%2C/gi, ',').replace(/%3A/gi, ':');
}

/** Deterministic URL: params sorted by name, empty values dropped. Contains no secret. */
export function canonicalUrl(base, path, params) {
  const entries = Object.entries(params || {})
    .filter(([, v]) => v !== undefined && v !== null && v !== '' && !(Array.isArray(v) && !v.length))
    .map(([k, v]) => [k, Array.isArray(v) ? v.join(',') : String(v)])
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
  return `${String(base).replace(/\/+$/, '')}${path}?${entries.map(([k, v]) => `${enc(k)}=${enc(v)}`).join('&')}`;
}

function clone(v) { return v === undefined ? v : JSON.parse(JSON.stringify(v)); }
function num(v) { const n = Number(v); return Number.isFinite(n) ? Math.round(n * 1000) / 1000 : null; }

/** Guard against /search returning an unrelated entity for a vague query. */
export function namesCompatible(query, resultName) {
  const a = normalizeText(query);
  const b = normalizeText(resultName);
  if (!a || !b) return false;
  if (a === b || ` ${b} `.includes(` ${a} `) || ` ${a} `.includes(` ${b} `)) return true;
  const ta = new Set(tokens(a).filter((t) => t.length >= 3));
  return tokens(b).some((t) => t.length >= 3 && ta.has(t));
}

async function fetchWithTimeout(fetchImpl, url, init, timeoutMs) {
  const ctrl = new AbortController();
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => { ctrl.abort(); reject(new QlooError('timeout', 'Qloo request timed out')); }, timeoutMs);
  });
  try {
    return await Promise.race([fetchImpl(url, { ...init, signal: ctrl.signal }), timeout]);
  } finally {
    clearTimeout(timer);
  }
}

/**
 * One session per /api/decide request. Tracks this request's calls and cache hits.
 * opts: {env, mock, fetchImpl, disabled, now, sleep, timeoutMs, maxCalls, deadlineMs}
 */
export function createQlooSession(opts = {}) {
  const env = opts.env || {};
  const mock = !!opts.mock;
  const apiKey = String(env.QLOO_API_KEY || '');
  let base = String(env.QLOO_API_BASE_URL || DEFAULT_QLOO_BASE).trim().replace(/\/+$/, '');
  if (!/^https:\/\//i.test(base)) base = DEFAULT_QLOO_BASE;
  const fetchImpl = opts.fetchImpl || (mock ? mockQlooFetch : globalThis.fetch);
  const now = opts.now || Date.now;
  const sleep = opts.sleep || ((ms) => new Promise((r) => setTimeout(r, ms)));
  const timeoutMs = opts.timeoutMs ?? 8000;
  const maxCalls = opts.maxCalls ?? MAX_CALLS_PER_REQUEST;
  const dailyBudget = Number(env.QLOO_DAILY_BUDGET) > 0 ? Math.floor(Number(env.QLOO_DAILY_BUDGET)) : 300;
  const deadline = Date.now() + (opts.deadlineMs ?? 14000);
  const stats = { calls: 0, cacheHits: 0, log: [] };
  const keyPrefix = mock ? 'mock:' : 'live:';

  function budgetLeft() {
    const day = new Date(now()).toISOString().slice(0, 10);
    if (shared.budget.day !== day) shared.budget = { day, used: 0 };
    return dailyBudget - shared.budget.used;
  }

  async function rawGet(url, endpoint) {
    for (let attempt = 0; ; attempt++) {
      if (stats.calls >= maxCalls) throw new QlooError('request_cap', `Per-request Qloo cap of ${maxCalls} calls reached`);
      if (budgetLeft() <= 0) throw new QlooError('daily_budget', 'The daily Qloo budget for this demo instance is used up');
      const remaining = deadline - Date.now();
      if (remaining < 500) throw new QlooError('timeout', 'Qloo time budget for this request is used up');
      stats.calls += 1;
      shared.budget.used += 1;
      const t0 = Date.now();
      let res = null;
      let err = null;
      try {
        res = await fetchWithTimeout(fetchImpl, url, { method: 'GET', headers: { 'X-Api-Key': apiKey, Accept: 'application/json' } }, Math.min(timeoutMs, remaining));
      } catch (e) {
        err = e;
      }
      const ms = Date.now() - t0;
      if (res && res.ok) {
        let body;
        try { body = await res.json(); } catch { throw new QlooError('bad_response', 'Qloo returned a non-JSON response'); }
        stats.log.push({ endpoint, status: res.status, ms, cached: false, attempt });
        return body;
      }
      const status = res ? res.status : 0;
      stats.log.push({ endpoint, status, ms, cached: false, attempt, error: err ? (err.code || err.name || 'network') : undefined });
      const retryable = !res || status === 429 || status >= 500;
      if (retryable && attempt === 0 && deadline - Date.now() > 2500) {
        let wait = 300 + Math.floor(Math.random() * 300);
        const ra = Number(res?.headers?.get?.('retry-after'));
        if (status === 429 && Number.isFinite(ra) && ra > 0) wait = Math.min(2000, ra * 1000);
        await sleep(wait);
        continue;
      }
      if (!res) throw new QlooError(err?.code === 'timeout' ? 'timeout' : 'network', err?.code === 'timeout' ? 'Qloo timed out' : 'Could not reach Qloo');
      const code = status === 401 ? 'unauthorized' : status === 403 ? 'forbidden' : status === 429 ? 'rate_limited' : `http_${status}`;
      throw new QlooError(code, `Qloo responded with HTTP ${status}`, status);
    }
  }

  async function get(endpoint, path, params, ttlMs) {
    const url = canonicalUrl(base, path, params);
    const key = keyPrefix + url;
    const hit = shared.cache.get(key);
    if (hit && hit.expiresAt > now()) {
      stats.cacheHits += 1;
      stats.log.push({ endpoint, cached: true });
      return clone(hit.value);
    }
    if (hit) shared.cache.delete(key);
    if (shared.inflight.has(key)) {
      stats.cacheHits += 1;
      stats.log.push({ endpoint, cached: 'in-flight' });
      return clone(await shared.inflight.get(key));
    }
    const p = rawGet(url, endpoint);
    shared.inflight.set(key, p);
    try {
      const value = await p;
      shared.cache.set(key, { value, expiresAt: now() + ttlMs });
      while (shared.cache.size > MAX_CACHE_ENTRIES) shared.cache.delete(shared.cache.keys().next().value);
      return clone(value);
    } finally {
      shared.inflight.delete(key);
    }
  }

  /** GET /search → best entity of the given type, or null. */
  async function search(name, type) {
    const query = String(name || '').replace(/\s+/g, ' ').trim().slice(0, 80);
    if (!query) return null;
    const body = await get('search', '/search', { query, types: type, take: 1 }, TTL_MS.search);
    const r = Array.isArray(body?.results) ? body.results[0] : null;
    if (!r?.entity_id || !r?.name) return null;
    if (Array.isArray(r.types) && r.types.length && type && !r.types.includes(type)) return null;
    if (!namesCompatible(query, r.name)) return null;
    return { id: String(r.entity_id), name: String(r.name), disambiguation: String(r.disambiguation || '').slice(0, 80), popularity: num(r.popularity) };
  }

  /** GET /v2/tags → first tag whose parents include parentType, or null. */
  async function tag(word, parentType) {
    const q = String(word || '').replace(/\s+/g, ' ').trim().slice(0, 40);
    if (!q) return null;
    const body = await get('tags', '/v2/tags', { 'filter.query': q, take: 5 }, TTL_MS.tags);
    const list = Array.isArray(body?.results?.tags) ? body.results.tags : [];
    const t = list.find((x) => Array.isArray(x?.parents) && x.parents.some((p) => p?.type === parentType));
    return t?.id ? { id: String(t.id), name: String(t.name || q) } : null;
  }

  /** GET /v2/insights (explainability on). Returns normalized entities. */
  async function insights(params) {
    const body = await get('insights', '/v2/insights', { ...params, 'feature.explainability': 'true' }, TTL_MS.insights);
    if (body && body.success === false) throw new QlooError('bad_response', 'Qloo insights returned success=false');
    const list = Array.isArray(body?.results?.entities) ? body.results.entities : [];
    return list.map((e) => ({
      id: String(e.entity_id || ''),
      name: String(e.name || ''),
      popularity: num(e.popularity),
      affinity: num(e?.query?.affinity),
      description: String(e?.properties?.description || '').slice(0, 220),
      tags: (Array.isArray(e.tags) ? e.tags : []).slice(0, 8).map((t) => ({ id: String(t?.id || ''), name: String(t?.name || '') })),
      explain: (e?.query?.explainability?.['signal.interests.entities'] || [])
        .map((x) => ({ id: String(x?.entity_id || ''), score: num(x?.score) }))
        .filter((x) => x.id),
    })).filter((e) => e.id);
  }

  return {
    configured: mock || !!apiKey,
    mock,
    disabled: !!opts.disabled,
    maxCalls,
    search,
    tag,
    insights,
    remainingCalls: () => Math.max(0, maxCalls - stats.calls),
    stats: () => ({ calls: stats.calls, cacheHits: stats.cacheHits, log: stats.log.slice() }),
  };
}
