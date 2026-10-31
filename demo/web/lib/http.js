// Tiny HTTP helpers that work with both Vercel's Node runtime (req.body pre-parsed,
// res helpers available) and plain node:http objects (dev-server.mjs, tests).

export const MAX_BODY_BYTES = 5.5 * 1024 * 1024;

export function sendJson(res, status, obj) {
  res.statusCode = status;
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.end(JSON.stringify(obj));
}

export function clientIp(req) {
  const h = req.headers || {};
  const real = String(h['x-real-ip'] || '').trim();
  if (real) return real;
  const fwd = String(h['x-forwarded-for'] || '').split(',')[0].trim();
  return fwd || req.socket?.remoteAddress || 'unknown';
}

export async function readJsonBody(req) {
  let pre;
  try {
    pre = req.body; // Vercel parses JSON lazily and throws on invalid JSON
  } catch {
    throw Object.assign(new Error('Request body is not valid JSON.'), { status: 400 });
  }
  if (pre !== undefined && pre !== null && !(typeof pre === 'string' && pre === '')) {
    if (typeof pre === 'object' && !Buffer.isBuffer(pre)) return pre;
    try { return JSON.parse(Buffer.isBuffer(pre) ? pre.toString('utf8') : String(pre)); } catch {
      throw Object.assign(new Error('Request body is not valid JSON.'), { status: 400 });
    }
  }
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > MAX_BODY_BYTES) throw Object.assign(new Error('Request body too large (max ~5 MB; images are limited to 3 × 1.5 MB).'), { status: 413 });
    chunks.push(chunk);
  }
  const text = Buffer.concat(chunks).toString('utf8');
  if (!text.trim()) throw Object.assign(new Error('Empty request body.'), { status: 400 });
  try { return JSON.parse(text); } catch {
    throw Object.assign(new Error('Request body is not valid JSON.'), { status: 400 });
  }
}
