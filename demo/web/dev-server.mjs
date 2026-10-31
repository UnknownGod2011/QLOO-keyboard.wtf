#!/usr/bin/env node
// Zero-dependency local dev server: serves the static files and routes /api/* to the
// same handlers Vercel runs. Usage:
//   node dev-server.mjs --mock          (offline synthetic fixtures, no keys needed)
//   node dev-server.mjs                 (reads ./.env if present; live Qloo + Gemini)
//   node dev-server.mjs --port 4000
import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const portArg = args.indexOf('--port');
const port = Number(portArg >= 0 ? args[portArg + 1] : process.env.PORT) || 3000;

// Minimal .env loader (KEY=VALUE lines). Values are never printed.
const envFile = path.join(root, '.env');
if (existsSync(envFile)) {
  for (const line of readFileSync(envFile, 'utf8').split(/\r?\n/)) {
    const m = /^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/.exec(line);
    if (m && !line.trim().startsWith('#') && process.env[m[1]] === undefined) process.env[m[1]] = m[2].replace(/^(['"])(.*)\1$/, '$2');
  }
}
if (args.includes('--mock')) process.env.DEMO_MOCK = '1';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.md': 'text/markdown; charset=utf-8',
};
const SECURITY_HEADERS = {
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'X-Frame-Options': 'DENY',
  'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
};
const ROUTES = {
  '/api/decide': () => import('./api/decide.js'),
  '/api/health': () => import('./api/health.js'),
};

const server = http.createServer(async (req, res) => {
  for (const [k, v] of Object.entries(SECURITY_HEADERS)) res.setHeader(k, v);
  const url = new URL(req.url || '/', 'http://localhost');
  try {
    if (url.pathname.startsWith('/api/')) {
      const load = ROUTES[url.pathname.replace(/\/+$/, '')];
      if (!load) { res.statusCode = 404; return res.end('{"error":"Not found"}'); }
      const mod = await load();
      return await mod.default(req, res);
    }
    let rel = decodeURIComponent(url.pathname);
    if (rel.endsWith('/')) rel += 'index.html';
    const file = path.normalize(path.join(root, rel));
    const relToRoot = path.relative(root, file);
    if (relToRoot.startsWith('..') || path.isAbsolute(relToRoot) || relToRoot.split(path.sep).some((seg) => seg.startsWith('.'))) {
      res.statusCode = 404;
      return res.end('Not found');
    }
    const st = await stat(file).catch(() => null);
    if (!st || !st.isFile()) { res.statusCode = 404; return res.end('Not found'); }
    res.setHeader('Content-Type', MIME[path.extname(file).toLowerCase()] || 'application/octet-stream');
    res.setHeader('Cache-Control', 'no-cache');
    return res.end(await readFile(file));
  } catch (e) {
    console.error('[dev-server]', e?.name, e?.message);
    if (!res.headersSent) { res.statusCode = 500; res.end('{"error":"Internal error"}'); }
  }
});

server.listen(port, () => {
  const mode = process.env.DEMO_MOCK === '1' ? 'MOCK (offline fixtures)' : 'LIVE';
  console.log(`QLOO Jarvis web demo on http://localhost:${port}  [${mode}]  qloo key: ${process.env.QLOO_API_KEY ? 'set' : 'missing'}, gemini key: ${process.env.GEMINI_API_KEY ? 'set' : 'missing'}`);
});
