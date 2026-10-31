// QLOO Jarvis web demo — browser UI. Plain ESM, no dependencies.
// All untrusted text (model output, Qloo names, user input) is rendered with
// textContent via h(); no innerHTML with data.
import {
  DOMAINS, addObservation, applyObservations, describePreference, observationsFromFeedback,
  removePreference, sanitizeProfile, seedPersona, normalizeText,
} from '/lib/profile.js';

const STORAGE_KEY = 'qloo-jarvis-web-demo:profile:v1';
const EXAMPLES = {
  shirts: { q: 'Which of these two shirts fits my style better?', opts: 'A: navy minimalist linen shirt, Uniqlo / B: neon graphic tee with large logo' },
  tonight: { q: 'Find a movie I would genuinely enjoy tonight', opts: '' },
  inception: { q: 'Which is a better pick for me: Inception or Mamma Mia!?', opts: '' },
  math: { q: 'What is 17% of 2,340?', opts: '' },
};
const MAX_IMAGES = 3;
const MAX_B64 = Math.floor(1.5 * 1024 * 1024);

const $ = (sel) => document.querySelector(sel);
const state = { profile: null, images: [], busy: false, lastChoices: new Map(), freshPrefIds: new Set() };

// ------------------------------------------------------------------ helpers

function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'style') Object.assign(el.style, v);
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (k === 'text') el.textContent = v;
    else el.setAttribute(k, v === true ? '' : String(v));
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

function newId() {
  return (globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(36).slice(2)}`).slice(0, 36);
}

function loadProfile() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) return sanitizeProfile(JSON.parse(raw));
  } catch { /* corrupted storage: reseed */ }
  return seedPersona();
}

function saveProfile(p) {
  state.profile = p;
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(p)); } catch { /* storage full or disabled */ }
  renderPrefs();
}

function mode() { return document.querySelector('input[name="mode"]:checked')?.value || 'on'; }

function showError(msg) {
  const el = $('#error');
  if (!msg) { el.hidden = true; el.textContent = ''; return; }
  el.hidden = false;
  el.textContent = msg;
}

function setBusy(b) {
  state.busy = b;
  for (const id of ['#go', '#compare']) $(id).disabled = b;
  $('#results').setAttribute('aria-busy', b ? 'true' : 'false');
}

function questionKey(input) {
  return normalizeText(`${input.question} | ${input.optionsText} | ${input.images.length}`);
}

// ------------------------------------------------------------------ status

async function loadHealth() {
  try {
    const r = await fetch('/api/health', { headers: { Accept: 'application/json' } });
    const j = await r.json();
    const g = $('#st-gemini');
    const q = $('#st-qloo');
    if (j.mock) {
      g.textContent = 'Mock mode · synthetic fixtures';
      g.className = 'pill mock';
      q.textContent = 'Qloo: mock';
      q.className = 'pill mock';
      return;
    }
    g.textContent = j.geminiConfigured ? 'Gemini: live' : 'Gemini: not configured';
    g.className = `pill ${j.geminiConfigured ? 'ok' : 'warn'}`;
    q.textContent = j.qlooConfigured ? 'Qloo: live' : 'Qloo: not configured (fallback)';
    q.className = `pill ${j.qlooConfigured ? 'ok' : 'warn'}`;
  } catch {
    $('#st-gemini').textContent = 'Server unreachable';
    $('#st-gemini').className = 'pill warn';
    $('#st-qloo').textContent = 'Qloo: unknown';
  }
}

// ------------------------------------------------------------------ images

async function downscale(file, maxSide, quality) {
  const url = URL.createObjectURL(file);
  try {
    const img = await new Promise((resolve, reject) => {
      const i = new Image();
      i.onload = () => resolve(i);
      i.onerror = () => reject(new Error('Could not read that image.'));
      i.src = url;
    });
    const scale = Math.min(1, maxSide / Math.max(img.naturalWidth, img.naturalHeight));
    const w = Math.max(1, Math.round(img.naturalWidth * scale));
    const hgt = Math.max(1, Math.round(img.naturalHeight * scale));
    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = hgt;
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, w, hgt);
    ctx.drawImage(img, 0, 0, w, hgt);
    const dataUrl = canvas.toDataURL('image/jpeg', quality);
    return dataUrl.slice(dataUrl.indexOf(',') + 1);
  } finally {
    URL.revokeObjectURL(url);
  }
}

async function addImages(files) {
  showError('');
  for (const file of files) {
    if (state.images.length >= MAX_IMAGES) { showError(`At most ${MAX_IMAGES} images per question.`); break; }
    if (!/^image\/(jpeg|png|webp)$/.test(file.type)) { showError('Only JPEG, PNG or WebP images are supported.'); continue; }
    try {
      let data = await downscale(file, 1024, 0.85);
      if (data.length > MAX_B64) data = await downscale(file, 768, 0.7);
      if (data.length > MAX_B64) { showError('That image is too large even after resizing.'); continue; }
      state.images.push({ mimeType: 'image/jpeg', data, preview: URL.createObjectURL(file), name: file.name });
    } catch (e) {
      showError(e.message);
    }
  }
  renderThumbs();
}

function renderThumbs() {
  const box = $('#thumbs');
  box.replaceChildren(...state.images.map((img, i) => h('div', { class: 'thumb' },
    h('img', { src: img.preview, alt: `Image ${i + 1}: ${img.name}` }),
    h('button', { type: 'button', 'aria-label': `Remove image ${i + 1}`, onclick: () => { URL.revokeObjectURL(img.preview); state.images.splice(i, 1); renderThumbs(); } }, '×'))));
}

// ------------------------------------------------------------------ requests

function collectInput() {
  return {
    question: $('#q').value.trim(),
    optionsText: $('#opts').value.trim(),
    location: $('#loc').value.trim(),
    images: state.images.map(({ mimeType, data }) => ({ mimeType, data })),
    simulateQlooOutage: $('#outage').checked,
  };
}

async function postDecide(body) {
  let res;
  try {
    res = await fetch('/api/decide', { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json' }, body: JSON.stringify(body) });
  } catch {
    throw new Error('Could not reach the demo server. Check your connection and try again.');
  }
  let json = null;
  try { json = await res.json(); } catch { /* non-JSON error page */ }
  if (!res.ok) throw new Error(json?.error || `The demo server returned HTTP ${res.status}.`);
  json.decisionId = newId();
  json.request = { question: body.question, optionsText: body.optionsText, imageCount: body.images.length };
  return json;
}

function placeholder(text) {
  return h('div', { class: 'card placeholder' }, h('div', { class: 'spinner', 'aria-hidden': 'true' }), h('span', {}, text));
}

async function runSingle() {
  const input = collectInput();
  if (!input.question) { showError('Type a question first, or pick an example.'); $('#q').focus(); return; }
  showError('');
  setBusy(true);
  const m = mode();
  const box = $('#results');
  box.className = 'results';
  box.replaceChildren(placeholder(m === 'on' ? 'Reading your question, checking taste memory and Qloo, then deciding…' : 'Asking Gemini (original Jarvis, no taste memory)…'));
  try {
    const body = { ...input, mode: m, profile: m === 'on' ? state.profile : undefined };
    const result = await postDecide(body);
    box.replaceChildren(renderResult(result, input));
  } catch (e) {
    box.replaceChildren();
    showError(e.message);
  } finally {
    setBusy(false);
  }
}

async function runCompare() {
  const input = collectInput();
  if (!input.question) { showError('Type a question first, or pick an example.'); $('#q').focus(); return; }
  showError('');
  setBusy(true);
  const box = $('#results');
  box.className = 'results compare';
  const left = placeholder('Qloo OFF: Gemini only…');
  const right = placeholder('Qloo ON: waiting for the same extracted options…');
  box.replaceChildren(left, right);
  try {
    const off = await postDecide({ ...input, mode: 'off' });
    const offCard = renderResult(off, input);
    left.replaceWith(offCard);
    const on = await postDecide({ ...input, mode: 'on', profile: state.profile, extraction: off.extraction });
    right.replaceWith(renderResult(on, input));
  } catch (e) {
    showError(e.message);
    for (const el of [...box.querySelectorAll('.placeholder')]) el.remove();
  } finally {
    setBusy(false);
  }
}

// ------------------------------------------------------------------ result card

function badgeFor(r) {
  if (r.mode === 'off') return { cls: 'off', text: 'Gemini only', label: 'Qloo OFF · original Jarvis' };
  if (r.engine.qlooGrounded) return { cls: 'grounded', text: 'Qloo-grounded', label: 'Qloo ON · taste memory + Qloo + Gemini' };
  if (r.engine.qlooFailed) return { cls: 'memory', text: 'Taste memory only (Qloo unavailable)', label: 'Qloo ON · fallback' };
  if (!r.engine.qlooRelevant) return { cls: 'skip', text: 'Qloo not needed · 0 calls', label: 'Qloo ON · not a taste question' };
  return { cls: 'memory', text: 'Taste memory only (no Qloo evidence)', label: 'Qloo ON' };
}

function chosenOption(r) {
  const ref = r.decision.choiceRef;
  if (ref?.kind === 'option') return r.extraction.options[ref.index] || null;
  if (ref?.kind === 'recommendation') {
    const rec = r.engine.recommendations?.[ref.index];
    return rec ? { label: rec.name, entity: { name: rec.name }, attributes: [] } : null;
  }
  return null;
}

function affinityCell(a) {
  if (a == null) return h('span', { class: 'muted' }, '—');
  return h('span', { class: 'aff' }, h('span', { class: 'aff-bar' }, h('i', { style: { width: `${Math.round(Math.max(0, Math.min(1, a)) * 100)}%` } })), String(a));
}

function evidenceList(evidence) {
  if (!evidence?.length) return h('span', { class: 'muted' }, 'no matching evidence');
  return h('ul', { class: 'ev' }, evidence.map((e) => h('li', {},
    h('span', { class: e.sign === '+' ? 's-plus' : e.sign === '−' ? 's-minus' : 's-dot' }, `${e.sign} `),
    `${e.source === 'qloo' ? 'Qloo: ' : e.source === 'constraint' ? '' : 'Taste memory: '}${e.text}`)));
}

function renderHow(r) {
  const e = r.engine;
  const body = h('div', { class: 'how-body' });
  body.append(h('div', {},
    h('h5', {}, 'Why Qloo was or was not used'),
    h('p', {}, e.reason || '—')));
  body.append(h('div', { class: 'kv' },
    h('span', {}, 'Domain ', h('b', {}, r.extraction.domain)),
    h('span', {}, 'Task ', h('b', {}, r.extraction.task)),
    h('span', {}, 'Qloo type ', h('b', {}, e.entityType ? e.entityType.replace('urn:entity:', '') : 'none')),
    h('span', {}, 'Qloo calls ', h('b', {}, String(e.calls))),
    h('span', {}, 'Cache hits ', h('b', {}, String(e.cacheHits))),
    r.mode === 'on' ? h('span', {}, 'Preferences considered ', h('b', {}, String(e.preferencesConsidered ?? 0))) : null,
    r.extraction.context ? h('span', {}, 'Context ', h('b', {}, r.extraction.context)) : null,
    h('span', {}, 'Time ', h('b', {}, `${(r.timingMs / 1000).toFixed(1)} s`))));
  if (e.signals?.length) {
    body.append(h('div', {}, h('h5', {}, 'Taste signals sent to Qloo (resolved)'),
      h('div', { class: 'signals' }, e.signals.map((s) => h('span', { class: 'sig' }, `${s.name} · ${s.kind} `, h('code', {}, s.id))))));
  }
  if (r.extraction.options.length) {
    const rows = (e.options || []).map((o, i) => h('tr', {},
      h('td', { class: 'num' }, o.rank ? `#${o.rank}` : String(i + 1)),
      h('td', {}, h('div', {}, o.label), evidenceList(o.evidence)),
      h('td', {}, o.qlooEntity ? h('div', {}, o.qlooEntity.name, h('div', { class: 'muted small' }, h('code', {}, o.qlooEntity.id))) : h('span', { class: 'muted' }, r.mode === 'off' ? 'not used' : 'not resolved')),
      h('td', { class: 'num' }, affinityCell(o.affinity), o.qlooDelta != null ? h('div', { class: 'muted small' }, `${o.qlooDelta >= 0 ? '+' : ''}${o.qlooDelta} vs avg`) : null),
      h('td', { class: 'num' }, o.prefScore == null ? '—' : String(o.prefScore))));
    body.append(h('div', {}, h('h5', {}, 'Options: taste memory vs Qloo'),
      h('table', { class: 'opt-table' },
        h('thead', {}, h('tr', {}, h('th', {}, 'Rank'), h('th', {}, 'Option & evidence'), h('th', {}, 'Qloo entity'), h('th', {}, 'Qloo affinity'), h('th', {}, 'Taste score'))),
        h('tbody', {}, rows))));
  }
  if (e.recommendations) {
    body.append(h('div', {}, h('h5', {}, 'Qloo recommendations (after your dislikes were filtered)'),
      e.recommendations.length ? h('table', { class: 'opt-table' },
        h('thead', {}, h('tr', {}, h('th', {}, 'Title'), h('th', {}, 'Qloo affinity'), h('th', {}, 'Strongest links'))),
        h('tbody', {}, e.recommendations.map((x) => h('tr', {}, h('td', {}, x.name, h('div', { class: 'muted small' }, x.tags.join(' · '))), h('td', { class: 'num' }, affinityCell(x.affinity)), h('td', {}, x.why || '—'))))) : h('p', { class: 'muted' }, 'None.')));
  }
  const notes = [...(e.warnings || []), ...(e.caveats || [])];
  if (notes.length) body.append(h('div', {}, h('h5', {}, 'Caveats'), h('ul', { class: 'caveats' }, notes.map((n) => h('li', {}, n)))));
  if (e.callLog?.length) {
    body.append(h('div', {}, h('h5', {}, 'Qloo call log (this request)'),
      h('ul', { class: 'caveats' }, e.callLog.map((c) => h('li', {}, `${c.endpoint}: ${c.cached ? `cache (${c.cached === true ? 'TTL' : c.cached})` : `HTTP ${c.status || 'error'} in ${c.ms} ms${c.attempt ? ' (retry)' : ''}`}`)))));
  }
  const raw = { mode: r.mode, extraction: r.extraction, engine: { ...e, brief: undefined }, brief: e.brief, decision: r.decision, timings: r.timings };
  body.append(h('div', {}, h('h5', {}, 'Raw response'), h('pre', { class: 'code' }, JSON.stringify(raw, null, 2))));
  return h('details', { class: 'how' }, h('summary', {}, 'How this was decided'), body);
}

function renderResult(r, input) {
  const b = badgeFor(r);
  const d = r.decision;
  const card = h('article', { class: 'card result', 'aria-label': `${b.label} result` });
  card.append(h('header', { class: 'r-head' },
    h('span', { class: `badge ${b.cls}` }, b.text),
    h('span', { class: 'mode-label' }, b.label),
    h('span', { class: 'meta' }, `Qloo calls ${r.engine.calls} · cache hits ${r.engine.cacheHits} · ${(r.timingMs / 1000).toFixed(1)} s${r.mock ? ' · mock' : ''}`)));

  const key = `${r.mode}|${questionKey(input)}`;
  const prev = state.lastChoices.get(key);
  if (prev && prev !== d.choice) card.append(h('div', { class: 'changed' }, `Changed since last time: previously “${prev}”, now “${d.choice}”.`));
  state.lastChoices.set(key, d.choice);

  card.append(h('p', { class: 'choice-label' }, r.extraction.task === 'answer' ? 'Answer' : 'Jarvis picks'));
  card.append(h('h3', { class: 'choice' }, d.choice));
  if (d.rationale) card.append(h('p', { class: 'rationale' }, d.rationale));
  card.append(h('div', { class: 'facts' }, h('span', { class: `conf ${d.confidence}` }, `Confidence: ${d.confidence}`)));

  if (r.engine.recommendations?.length) {
    const chosen = d.choiceRef?.kind === 'recommendation' ? d.choiceRef.index : -1;
    card.append(h('ul', { class: 'recs', 'aria-label': 'Qloo recommendations' }, r.engine.recommendations.map((x, i) => h('li', { class: `rec${i === chosen ? ' chosen' : ''}` },
      h('span', { class: 'rec-name' }, x.name), affinityCell(x.affinity),
      h('span', { class: 'rec-why' }, [x.tags.slice(0, 3).join(' · '), x.why ? `closest to ${x.why}` : ''].filter(Boolean).join(' — '))))));
  }

  const cites = h('div', { class: 'cites' });
  if (d.cited_preferences?.length) {
    cites.append(h('h4', {}, 'From your taste memory'), h('div', { class: 'cite-list' }, d.cited_preferences.map((p) => h('span', { class: 'cite' }, `${p.statement}${p.strength ? ` · ${p.strength}` : ''}`))));
  }
  if (d.cited_qloo?.length) {
    cites.append(h('h4', {}, 'Qloo evidence'), h('div', { class: 'cite-list' }, d.cited_qloo.map((q) => h('span', { class: 'cite q' }, q))));
  }
  if (cites.childElementCount) card.append(cites);
  if (d.uncertainty) card.append(h('p', { class: 'uncertainty' }, `What could change this: ${d.uncertainty}`));
  for (const w of r.engine.warnings || []) card.append(h('div', { class: 'warn-box' }, w));
  if (d.groundingNote) card.append(h('div', { class: 'warn-box' }, d.groundingNote));

  // Inferred mentions ("I love sci-fi") are learned only with taste memory on.
  if (r.mode === 'on' && r.extraction.mentioned_preferences?.length) {
    const inputs = r.extraction.mentioned_preferences.map((m) => ({ ...m, source: 'mention', quote: input.question.slice(0, 140), fingerprint: `mention:${normalizeText(input.question).slice(0, 60)}:${normalizeText(m.value)}` }));
    const { profile, results } = applyObservations(state.profile, inputs);
    const learned = results.filter((x) => x.status === 'added' || x.status === 'changed');
    if (learned.length) {
      learned.forEach((x) => x.prefId && state.freshPrefIds.add(x.prefId));
      saveProfile(profile);
      card.append(h('p', { class: 'noted' }, `Noted from your question: ${learned.map((x) => x.message.replace(/^Learned: /, '')).join('; ')}`));
    }
  }

  const option = chosenOption(r);
  const tasteDomain = !['technical', 'other'].includes(r.extraction.domain);
  if (option && tasteDomain) {
    const msg = h('p', { class: 'msg', role: 'status', 'aria-live': 'polite' });
    // Learning only shows with taste memory on, so "Ask again" re-runs ON (or the comparison).
    const again = h('button', { type: 'button', class: 'btn ghost small', hidden: true, onclick: () => { if (state.busy) return; if ($('#results').classList.contains('compare')) runCompare(); else runSingleWith('on'); } }, 'Ask again');
    const give = (polarity) => {
      const inputs = observationsFromFeedback({ domain: r.extraction.domain, option, polarity, decisionId: r.decisionId });
      if (!inputs.length) { msg.textContent = 'Nothing to learn from this option.'; return; }
      const { profile, results } = applyObservations(state.profile, inputs);
      const added = results.filter((x) => x.status === 'added' || x.status === 'changed');
      const dups = results.filter((x) => x.status === 'duplicate').length;
      const refused = results.filter((x) => x.status === 'refused').length;
      added.forEach((x) => x.prefId && state.freshPrefIds.add(x.prefId));
      saveProfile(profile);
      msg.className = `msg ${added.length ? 'good' : ''}`;
      msg.textContent = [
        added.length ? `Learned ${added.length} ${polarity > 0 ? 'positive' : 'negative'} signal${added.length > 1 ? 's' : ''} (${inputs.map((x) => x.value).join(', ')}).` : '',
        dups ? `${dups} duplicate${dups > 1 ? 's' : ''} ignored (same feedback within 24 h).` : '',
        refused ? `${refused} sensitive item(s) refused.` : '',
        added.length ? 'Ask again to see the decision update.' : '',
      ].filter(Boolean).join(' ');
      again.hidden = !added.length;
    };
    card.append(h('div', { class: 'feedback' },
      h('button', { type: 'button', class: 'btn small good', onclick: () => give(1) }, 'I’m choosing this'),
      h('button', { type: 'button', class: 'btn small bad', onclick: () => give(-1) }, 'Not for me'),
      again, msg));
  }
  card.append(renderHow(r));
  return card;
}

function runSingleWith(m) {
  document.getElementById(m === 'off' ? 'mode-off' : 'mode-on').checked = true;
  updateHint();
  return runSingle();
}

// ------------------------------------------------------------------ taste memory panel

function renderPrefs() {
  const box = $('#prefs');
  const now = Date.now();
  const prefs = (state.profile?.preferences || []).map((p) => describePreference(p, now));
  $('#persona-line').textContent = `${state.profile?.owner === 'Alex' ? 'Demo persona: Alex' : 'Your profile'} · ${prefs.length} preference${prefs.length === 1 ? '' : 's'}`;
  if (!prefs.length) {
    box.replaceChildren(h('p', { class: 'muted small' }, 'No preferences yet. Add a like or dislike above, or give feedback on a decision.'));
    return;
  }
  const groups = new Map();
  for (const p of prefs) {
    if (!groups.has(p.domain)) groups.set(p.domain, []);
    groups.get(p.domain).push(p);
  }
  const order = DOMAINS.filter((d) => groups.has(d));
  box.replaceChildren(...order.map((domain) => h('section', { class: 'pref-group' },
    h('h3', {}, domain),
    h('ul', {}, groups.get(domain).sort((a, b) => b.confidence - a.confidence).map((p) => {
      const days = p.expiresAt ? Math.ceil((Date.parse(p.expiresAt) - now) / 86400000) : null;
      return h('li', { class: `pref ${p.polarity < 0 ? 'dislike' : 'like'}${state.freshPrefIds.has(p.id) ? ' fresh' : ''}` },
        h('span', { class: 'pol', 'aria-label': p.polarity < 0 ? 'Dislike' : 'Like' }, p.polarity < 0 ? '−' : '+'),
        h('div', {},
          h('span', { class: 'pref-value' }, p.value),
          p.attribute !== 'entity' && p.attribute !== 'general' ? h('span', { class: 'pref-attr' }, p.attribute) : null,
          h('div', { class: 'pref-meta' },
            h('span', { class: 'tag' }, p.kind === 'contextual' ? `contextual: ${p.context}` : p.kind),
            h('span', { class: `tag ${p.label}` }, p.label),
            h('span', {}, `${p.evidenceCount} observation${p.evidenceCount === 1 ? '' : 's'}`),
            p.recentlyChanged ? h('span', { class: 'tag changed' }, 'recently changed') : null,
            days != null ? h('span', {}, days > 0 ? `expires in ${days} d` : 'expired') : null),
          h('div', { class: 'cbar', 'aria-hidden': 'true' }, h('i', { style: { width: `${Math.round(p.confidence * 100)}%` } }))),
        h('button', { type: 'button', class: 'icon-btn', 'aria-label': `Delete: ${p.statement}`, title: 'Delete', onclick: () => saveProfile(removePreference(state.profile, p.id)) }, '×'));
    })))));
}

function onAdd(ev) {
  ev.preventDefault();
  const msg = $('#add-msg');
  const r = addObservation(state.profile, {
    domain: $('#add-domain').value,
    attribute: $('#add-attr').value,
    value: $('#add-value').value,
    polarity: Number(document.querySelector('input[name="pol"]:checked')?.value || 1),
    source: 'explicit',
    context: $('#add-context').value,
    temporary: $('#add-temp').checked,
    quote: 'Added in the taste-memory panel',
  });
  msg.textContent = r.message;
  msg.className = `msg ${r.status === 'refused' || r.status === 'invalid' ? 'bad' : 'good'}`;
  if (r.status === 'added' || r.status === 'changed') {
    state.freshPrefIds.add(r.pref.id);
    saveProfile(r.profile);
    $('#add-value').value = '';
    $('#add-context').value = '';
    $('#add-temp').checked = false;
  }
}

// ------------------------------------------------------------------ wiring

function updateHint() {
  $('#mode-hint').textContent = mode() === 'on'
    ? 'Qloo ON: taste memory + Qloo Taste AI evidence + Gemini final decision.'
    : 'Qloo OFF: the original Jarvis behaviour. Gemini only, no taste memory, no Qloo.';
}

function init() {
  state.profile = loadProfile();
  saveProfile(state.profile);
  const sel = $('#add-domain');
  for (const d of DOMAINS.filter((x) => x !== 'technical' && x !== 'other')) sel.append(h('option', { value: d }, d));
  sel.value = 'movies';
  $('#ask').addEventListener('submit', (e) => { e.preventDefault(); if (!state.busy) runSingle(); });
  $('#compare').addEventListener('click', () => { if (!state.busy) runCompare(); });
  for (const r of document.querySelectorAll('input[name="mode"]')) r.addEventListener('change', updateHint);
  for (const chip of document.querySelectorAll('.chip')) {
    chip.addEventListener('click', () => {
      const ex = EXAMPLES[chip.dataset.example];
      $('#q').value = ex.q;
      $('#opts').value = ex.opts;
      $('#go').focus();
    });
  }
  $('#img').addEventListener('change', (e) => { addImages([...e.target.files]); e.target.value = ''; });
  $('#reset').addEventListener('click', () => {
    state.freshPrefIds.clear();
    state.lastChoices.clear();
    saveProfile(seedPersona());
    $('#add-msg').textContent = 'Demo persona restored.';
    $('#add-msg').className = 'msg good';
  });
  $('#add').addEventListener('submit', onAdd);
  $('#q').addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && !state.busy) runSingle(); });
  updateHint();
  loadHealth();
  $('#results').replaceChildren(h('div', { class: 'card empty' },
    h('p', {}, h('strong', {}, 'Try an example, then press “Compare OFF vs ON”.')),
    h('p', { class: 'small' }, 'OFF is the original Jarvis (Gemini only). ON adds Alex’s taste memory and Qloo Taste AI evidence. Give feedback on a pick and ask again to watch it learn.')));
}

init();
