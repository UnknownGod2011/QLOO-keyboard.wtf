// GET /api/health — reports configuration without revealing any secret.
import { sendJson } from '../lib/http.js';
import { isMockEnv } from '../lib/engine.js';

export function healthPayload(env = {}) {
  return {
    ok: true,
    qlooConfigured: Boolean(env.QLOO_API_KEY),
    geminiConfigured: Boolean(env.GEMINI_API_KEY),
    mock: isMockEnv(env),
  };
}

export default async function handler(req, res) {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    res.setHeader('Allow', 'GET, HEAD');
    return sendJson(res, 405, { error: 'Use GET.' });
  }
  return sendJson(res, 200, healthPayload(process.env));
}
