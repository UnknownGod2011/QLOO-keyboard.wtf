// Gemini REST client (generateContent, JSON mode) + prompts + output normalizers.
// In mock mode (DEMO_MOCK=1) no network call is made: lib/fixtures.js answers.

import { mockGemini } from './fixtures.js';
import { DOMAINS, cleanText, checkSensitive } from './profile.js';

export const DEFAULT_GEMINI_MODEL = 'gemini-3.5-flash';
export const ENTITY_TYPES = Object.freeze(['artist', 'book', 'brand', 'destination', 'movie', 'person', 'place', 'podcast', 'tv_show', 'video_game'].map((t) => `urn:entity:${t}`));
const TASKS = ['choose', 'rank', 'recommend', 'answer'];

export class GeminiError extends Error {
  constructor(code, message) {
    super(message);
    this.name = 'GeminiError';
    this.code = code;
  }
}

export const EXTRACT_SYSTEM = `You are the perception step of QLOO Jarvis, a desktop assistant. Read the user's question, any option text and any attached images, and return ONLY a JSON object:
{"domain": one of ${DOMAINS.join('|')},
 "task": "choose" (pick one of the given options) | "rank" | "recommend" (no options given, the user wants suggestions) | "answer" (factual, maths, code or how-to),
 "options": [{"label": short human name such as "A: navy linen shirt" or the exact title,
              "description": one plain sentence,
              "attributes": [{"name": "color|style|material|pattern|feature|brand|genre|era|price|...", "value": "..."}],
              "entity": {"name": canonical brand/title/artist/place name if clearly identified, else null,
                         "type": one of ${ENTITY_TYPES.join('|')} or null},
              "fails_constraints": [stated constraints this option clearly fails]}],
 "constraints": [explicit requirements such as budget, dress code, dietary needs],
 "context": occasion if stated (e.g. "wedding"), else "",
 "location": city if stated, else "",
 "mentioned_preferences": [{"domain": "...", "attribute": "entity|genre|style|color|...", "value": "...", "polarity": 1 or -1}] for tastes the user states about themselves in the question (max 3)}
Rules: use domain "technical" for maths, code, factual or how-to questions with no taste component. For images, treat each visible item as an option and describe its visible attributes (colour, style, material, pattern, logo size, brand only if legible). Never guess a brand that is not legible. Do not invent options. Never include sensitive personal data (health, religion, politics, sexuality, ethnicity, finances, contact details).`;

export const FINAL_OFF_SYSTEM = `You are Jarvis, a helpful desktop voice assistant. Answer using only the user's question and the extracted options. You know nothing about this user's personal taste. Return ONLY JSON:
{"choice": the option label you pick, the item you recommend, or the direct answer,
 "rationale": at most 3 short spoken-style sentences,
 "confidence": "high" | "medium" | "low",
 "notes": caveats in one sentence}`;

export const FINAL_ON_SYSTEM = `You are QLOO Jarvis, a taste-aware desktop assistant. Decide using the user's question, the extracted options and the ENGINE BRIEF, which contains the user's relevant taste-memory preferences and any Qloo cultural-affinity evidence computed by the decision engine.
Rules:
1. Pick exactly one option label, or one recommendation name when recommendations are given, or give the direct answer for factual tasks.
2. Ground the rationale in the brief. Put the ids of the preferences you rely on (e.g. "p3") in cited_preferences. Quote Qloo evidence exactly as given (entity names and affinity numbers) in cited_qloo.
3. If the brief says qloo_grounded is false you MUST NOT claim or imply that Qloo or cultural-affinity data informed the decision, and cited_qloo MUST be [].
4. Qloo measures taste affinity between cultural entities (titles, brands, artists, places). Qloo does NOT judge colour, fit, size, material or visual appearance. You judge those yourself from the option descriptions and attributes against the user's stated preferences.
5. You may disagree with engine_top_choice only for a concrete reason (a constraint, a visual mismatch, a contextual preference); say so.
6. Do not invent preferences, numbers or facts. Use qualitative confidence high|medium|low, never percentages, and say what would change your mind in uncertainty.
7. Never mention or infer sensitive personal traits.
Return ONLY JSON: {"choice": "...", "rationale": "...", "cited_preferences": ["p1"], "cited_qloo": ["..."], "confidence": "high|medium|low", "uncertainty": "..."}`;

/** Parse model text as JSON defensively (code fences, leading prose, trailing junk). */
export function parseJsonText(text) {
  let s = String(text || '').trim();
  const fence = /```(?:json)?\s*([\s\S]*?)```/i.exec(s);
  if (fence) s = fence[1].trim();
  try { return JSON.parse(s); } catch { /* fall through */ }
  const start = s.indexOf('{');
  const end = s.lastIndexOf('}');
  if (start >= 0 && end > start) {
    try { return JSON.parse(s.slice(start, end + 1)); } catch { /* fall through */ }
  }
  throw new GeminiError('bad_json', 'Gemini returned text that is not valid JSON');
}

function multiline(s, max) {
  return String(s ?? '').replace(/[\u0000-\u0009\u000b-\u001f\u007f]/g, ' ').trim().slice(0, max);
}

export function normalizeExtraction(raw) {
  const r = raw && typeof raw === 'object' ? raw : {};
  const domain = DOMAINS.includes(r.domain) ? r.domain : 'other';
  const options = (Array.isArray(r.options) ? r.options : []).slice(0, 6).map((o, i) => ({
    label: cleanText(o?.label || o?.name || '', 80) || `Option ${i + 1}`,
    description: cleanText(o?.description || '', 240),
    attributes: (Array.isArray(o?.attributes) ? o.attributes : []).slice(0, 8)
      .map((a) => ({ name: cleanText(a?.name, 30).toLowerCase(), value: cleanText(a?.value, 60) }))
      .filter((a) => a.name && a.value && !checkSensitive(a.name, a.value)),
    entity: {
      name: cleanText(o?.entity?.name || '', 80) || null,
      type: ENTITY_TYPES.includes(o?.entity?.type) ? o.entity.type : null,
    },
    fails_constraints: (Array.isArray(o?.fails_constraints) ? o.fails_constraints : []).slice(0, 4).map((s) => cleanText(s, 80)).filter(Boolean),
  }));
  let task = TASKS.includes(r.task) ? r.task : (options.length ? 'choose' : 'answer');
  if (options.length && task === 'recommend') task = 'choose';
  if (!options.length && (task === 'choose' || task === 'rank')) task = domain === 'technical' || domain === 'other' ? 'answer' : 'recommend';
  if (domain === 'technical') task = options.length ? task : 'answer';
  const mentioned = (Array.isArray(r.mentioned_preferences) ? r.mentioned_preferences : []).slice(0, 3)
    .map((m) => ({
      domain: DOMAINS.includes(m?.domain) ? m.domain : domain,
      attribute: cleanText(m?.attribute || 'general', 30).toLowerCase() || 'general',
      value: cleanText(m?.value, 40),
      polarity: Number(m?.polarity) < 0 ? -1 : 1,
    }))
    .filter((m) => m.value && !checkSensitive(m.attribute, m.value) && m.domain !== 'technical' && m.domain !== 'other');
  return {
    domain,
    task,
    options,
    constraints: (Array.isArray(r.constraints) ? r.constraints : []).slice(0, 6).map((s) => cleanText(s, 80)).filter(Boolean),
    context: cleanText(r.context || '', 40).toLowerCase(),
    location: cleanText(r.location || '', 60),
    mentioned_preferences: mentioned,
  };
}

function normConfidence(c) {
  if (typeof c === 'number') return c >= 0.75 ? 'high' : c >= 0.45 ? 'medium' : 'low';
  const s = String(c || '').toLowerCase();
  return s.includes('high') ? 'high' : s.includes('med') ? 'medium' : 'low';
}

export function normalizeDecision(raw) {
  const r = raw && typeof raw === 'object' ? raw : {};
  const list = (v, n, max) => (Array.isArray(v) ? v : v ? [v] : []).slice(0, n).map((x) => (typeof x === 'string' ? cleanText(x, max) : x)).filter(Boolean);
  return {
    choice: cleanText(typeof r.choice === 'string' ? r.choice : r.choice?.label || r.choice?.name || '', 200) || 'No clear choice',
    rationale: multiline(r.rationale || '', 1200),
    cited_preferences: list(r.cited_preferences, 8, 160),
    cited_qloo: list(r.cited_qloo, 6, 200).filter((x) => typeof x === 'string'),
    confidence: normConfidence(r.confidence),
    uncertainty: multiline(r.uncertainty || r.notes || '', 600),
  };
}

function optionsBlock(extraction) {
  return JSON.stringify((extraction.options || []).map((o) => ({ label: o.label, description: o.description, attributes: o.attributes, fails_constraints: o.fails_constraints })), null, 1);
}

export function extractPrompt({ question, optionsText = '', location = '', imageCount = 0 }) {
  return `QUESTION:\n${question}\n\nOPTIONS TEXT (may be empty):\n${optionsText || '(none)'}\n\nLOCATION HINT: ${location || '(none)'}\nIMAGES ATTACHED: ${imageCount}${imageCount ? ' (each image shows one option unless the question says otherwise; label them "Image 1", "Image 2", ...)' : ''}\n\nReturn the JSON now.`;
}

export function finalOffPrompt({ question, extraction }) {
  return `QUESTION:\n${question}\n\nTASK: ${extraction.task}\nDOMAIN: ${extraction.domain}\nOPTIONS:\n${optionsBlock(extraction)}\nCONSTRAINTS: ${JSON.stringify(extraction.constraints)}\nCONTEXT: ${extraction.context || '(none)'}\n\nReturn the JSON now.`;
}

export function finalOnPrompt({ question, extraction, brief }) {
  return `QUESTION:\n${question}\n\nTASK: ${extraction.task}\nDOMAIN: ${extraction.domain}\nOPTIONS:\n${optionsBlock(extraction)}\nCONSTRAINTS: ${JSON.stringify(extraction.constraints)}\nCONTEXT: ${extraction.context || '(none)'}\n\nENGINE BRIEF (JSON):\n${JSON.stringify(brief, null, 1)}\n\nqloo_grounded = ${brief.qloo_grounded ? 'true' : 'false'}${brief.qloo_grounded ? '' : ' (do NOT claim Qloo grounding; cited_qloo must be [])'}.\nReturn the JSON now.`;
}

export function createGemini({ env = {}, mock = false, fetchImpl, timeoutMs = 20000 } = {}) {
  const apiKey = String(env.GEMINI_API_KEY || '');
  const model = /^[a-z0-9][a-z0-9.\-]{1,60}$/i.test(String(env.GEMINI_MODEL || '')) ? env.GEMINI_MODEL : DEFAULT_GEMINI_MODEL;
  // Extraction is a simpler structured task; it uses the lighter model so the final
  // decision keeps the main model's per-minute quota.
  const extractModel = /^[a-z0-9][a-z0-9.\-]{1,60}$/i.test(String(env.GEMINI_EXTRACT_MODEL || '')) ? env.GEMINI_EXTRACT_MODEL : 'gemini-3.5-flash-lite';
  const doFetch = fetchImpl || globalThis.fetch;

  // One bounded retry for transient failures (5xx/429/timeout/truncated JSON). A 429 on the
  // main model (per-model quota exhausted) retries once on the lighter model instead.
  async function generateJson(args) {
    try {
      return await generateJsonOnce(args);
    } catch (e) {
      const transient = e instanceof GeminiError && (/^http_(5\d\d|429)$/.test(e.code) || ['timeout', 'network', 'empty', 'bad_json'].includes(e.code));
      if (!transient) throw e;
      const current = args.useModel || model;
      if (e.code === 'http_429' && current !== extractModel) return generateJsonOnce({ ...args, useModel: extractModel });
      await new Promise((r) => setTimeout(r, e.code === 'http_429' ? 4000 : 400));
      return generateJsonOnce(args);
    }
  }

  async function generateJsonOnce({ system, text, images = [], useModel = model }) {
    if (!apiKey) throw new GeminiError('not_configured', 'Gemini is not configured');
    const url = `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(useModel)}:generateContent`;
    const body = {
      systemInstruction: { parts: [{ text: system }] },
      contents: [{ role: 'user', parts: [{ text }, ...images.map((i) => ({ inlineData: { mimeType: i.mimeType, data: i.data } }))] }],
      // Thinking models spend part of the output budget on reasoning; keep room for the JSON answer.
      generationConfig: { responseMimeType: 'application/json', temperature: 0.2, maxOutputTokens: 8192 },
    };
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), timeoutMs);
    try {
      const res = await doFetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'x-goog-api-key': apiKey }, body: JSON.stringify(body), signal: ctrl.signal });
      if (!res.ok) throw new GeminiError(`http_${res.status}`, `Gemini responded with HTTP ${res.status}`);
      const data = await res.json();
      const parts = data?.candidates?.[0]?.content?.parts || [];
      const out = parts.filter((p) => typeof p?.text === 'string' && !p.thought).map((p) => p.text).join('');
      if (!out.trim()) throw new GeminiError('empty', 'Gemini returned no text');
      return parseJsonText(out);
    } catch (e) {
      if (e instanceof GeminiError) throw e;
      if (e?.name === 'AbortError') throw new GeminiError('timeout', 'Gemini timed out');
      throw new GeminiError('network', 'Could not reach Gemini');
    } finally {
      clearTimeout(timer);
    }
  }

  return {
    model,
    configured: mock || !!apiKey,
    async extract({ question, optionsText, images = [], location }) {
      const raw = mock
        ? mockGemini('extract', { question, optionsText, images, location })
        : await generateJson({ system: EXTRACT_SYSTEM, text: extractPrompt({ question, optionsText, location, imageCount: images.length }), images, useModel: extractModel });
      return normalizeExtraction(raw);
    },
    async decideOff({ question, extraction }) {
      const raw = mock
        ? mockGemini('final_off', { question, extraction })
        : await generateJson({ system: FINAL_OFF_SYSTEM, text: finalOffPrompt({ question, extraction }) });
      return normalizeDecision(raw);
    },
    async decideOn({ question, extraction, brief }) {
      const raw = mock
        ? mockGemini('final_on', { question, extraction, brief })
        : await generateJson({ system: FINAL_ON_SYSTEM, text: finalOnPrompt({ question, extraction, brief }) });
      return normalizeDecision(raw);
    },
  };
}
