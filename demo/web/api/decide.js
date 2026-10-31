// POST /api/decide — Vercel Node serverless function (also used by dev-server.mjs).
import { decide, HttpError, createRateLimiter } from '../lib/engine.js';
import { sendJson, readJsonBody, clientIp } from '../lib/http.js';

/** Factory so tests can inject env/fetch/clock. The default export uses process.env. */
export function createDecideHandler(deps = {}) {
  // Per-instance limiter: 10 decide calls per IP per 10 minutes.
  const limiter = deps.limiter || createRateLimiter({ limit: 10, windowMs: 10 * 60 * 1000 });
  return async function handler(req, res) {
    if (req.method !== 'POST') {
      res.setHeader('Allow', 'POST');
      return sendJson(res, 405, { error: 'Use POST with a JSON body.' });
    }
    const ct = String(req.headers?.['content-type'] || '').toLowerCase();
    if (!ct.includes('application/json')) return sendJson(res, 415, { error: 'Content-Type must be application/json.' });
    const rl = limiter(clientIp(req));
    if (!rl.ok) {
      res.setHeader('Retry-After', String(rl.retryAfterSec));
      return sendJson(res, 429, { error: `Demo rate limit reached (10 decisions per 10 minutes). Try again in about ${Math.ceil(rl.retryAfterSec / 60)} min.` });
    }
    let body;
    try {
      body = await readJsonBody(req);
    } catch (e) {
      return sendJson(res, e.status || 400, { error: e.message });
    }
    try {
      const result = await decide(body, { ...deps, env: deps.env || process.env });
      return sendJson(res, 200, result);
    } catch (e) {
      if (e instanceof HttpError) return sendJson(res, e.status, { error: e.message });
      // Never log request bodies or keys; name + message only.
      console.error('[api/decide] unexpected error:', e?.name, e?.message);
      return sendJson(res, 500, { error: 'Something went wrong on the demo server. Please try again.' });
    }
  };
}

const handler = createDecideHandler();
export default async function decideHandler(req, res) {
  return handler(req, res);
}
