// SYNTHETIC mock data for DEMO_MOCK=1 and the offline tests.
// Nothing here is a real Qloo or Gemini response: IDs are fake UUIDs, tags are
// invented ("urn:tag:demo:*"), descriptions and affinities are hand-written or
// computed from a toy tag-overlap formula. Titles/brands are public names only.

import { DOMAIN_TYPE, normalizeText } from './profile.js';

export const FIXTURE_NOTE = 'Synthetic demo data (DEMO_MOCK=1). Not real Qloo or Gemini output.';

const uid = (n) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const TAGS = [
  ['science_fiction', 'Science Fiction', 'urn:tag:genre:media', ['movie', 'book', 'tv_show']],
  ['cerebral', 'Cerebral', 'urn:tag:keyword:media', ['movie', 'book']],
  ['space', 'Space', 'urn:tag:keyword:media', ['movie', 'book']],
  ['epic', 'Epic', 'urn:tag:keyword:media', ['movie']],
  ['first_contact', 'First Contact', 'urn:tag:keyword:media', ['movie', 'book']],
  ['neo_noir', 'Neo-Noir', 'urn:tag:genre:media', ['movie']],
  ['dystopian', 'Dystopian', 'urn:tag:keyword:media', ['movie', 'book']],
  ['heist', 'Heist', 'urn:tag:keyword:media', ['movie']],
  ['thriller', 'Thriller', 'urn:tag:genre:media', ['movie', 'book']],
  ['artificial_intelligence', 'Artificial Intelligence', 'urn:tag:keyword:media', ['movie', 'book']],
  ['musical', 'Musical', 'urn:tag:genre:media', ['movie']],
  ['comedy', 'Comedy', 'urn:tag:genre:media', ['movie', 'tv_show']],
  ['romance', 'Romance', 'urn:tag:genre:media', ['movie', 'book']],
  ['feel_good', 'Feel-Good', 'urn:tag:keyword:media', ['movie']],
  ['horror', 'Horror', 'urn:tag:genre:media', ['movie', 'book']],
  ['drama', 'Drama', 'urn:tag:genre:media', ['movie']],
  ['minimalist', 'Minimalist', 'urn:tag:style:demo', ['brand', 'place']],
  ['basics', 'Wardrobe Basics', 'urn:tag:style:demo', ['brand']],
  ['japanese', 'Japanese Design', 'urn:tag:style:demo', ['brand']],
  ['outdoor', 'Outdoor', 'urn:tag:style:demo', ['brand']],
  ['sustainable', 'Sustainable', 'urn:tag:style:demo', ['brand']],
  ['scandinavian', 'Scandinavian', 'urn:tag:style:demo', ['brand']],
  ['streetwear', 'Streetwear', 'urn:tag:style:demo', ['brand']],
  ['logo_heavy', 'Logo-Heavy', 'urn:tag:style:demo', ['brand']],
  ['graphic', 'Graphic Prints', 'urn:tag:style:demo', ['brand']],
  ['formal', 'Formalwear', 'urn:tag:style:demo', ['brand']],
  ['indie', 'Indie', 'urn:tag:genre:music', ['artist']],
  ['electronic', 'Electronic', 'urn:tag:genre:music', ['artist']],
  ['pop', 'Pop', 'urn:tag:genre:music', ['artist']],
].map(([slug, name, type, parents], i) => ({
  id: `urn:tag:demo:${slug}`,
  slug,
  name,
  type,
  parents: parents.map((p) => ({ type: `urn:entity:${p}` })),
  popularity: Math.round((0.95 - i * 0.02) * 1000) / 1000,
}));
const TAG_BY_SLUG = Object.fromEntries(TAGS.map((t) => [t.slug, t]));

const E = (n, name, type, popularity, tags, description) => ({ id: uid(n), name, type: `urn:entity:${type}`, popularity, tags, description });

export const CATALOG = [
  E(1, 'Interstellar', 'movie', 0.93, ['science_fiction', 'space', 'cerebral', 'epic'], 'Synthetic note: a sweeping space-exploration drama.'),
  E(2, 'Arrival', 'movie', 0.86, ['science_fiction', 'cerebral', 'first_contact', 'drama'], 'Synthetic note: a quiet first-contact story about language.'),
  E(3, 'Blade Runner 2049', 'movie', 0.87, ['science_fiction', 'neo_noir', 'dystopian', 'cerebral'], 'Synthetic note: a slow-burn neo-noir sequel.'),
  E(4, 'Inception', 'movie', 0.94, ['science_fiction', 'cerebral', 'heist', 'thriller'], 'Synthetic note: a layered dream-heist thriller.'),
  E(5, 'Mamma Mia!', 'movie', 0.96, ['musical', 'comedy', 'romance', 'feel_good'], 'Synthetic note: a sunny jukebox musical.'),
  E(6, 'Annihilation', 'movie', 0.8, ['science_fiction', 'horror', 'cerebral'], 'Synthetic note: an eerie expedition into a mutating zone.'),
  E(7, 'Ex Machina', 'movie', 0.84, ['science_fiction', 'cerebral', 'artificial_intelligence', 'thriller'], 'Synthetic note: a tense chamber piece about an AI.'),
  E(8, 'Dune', 'movie', 0.91, ['science_fiction', 'epic', 'space'], 'Synthetic note: a desert-planet epic.'),
  E(9, 'Moon', 'movie', 0.7, ['science_fiction', 'space', 'cerebral', 'drama'], 'Synthetic note: a lonely lunar-base mystery.'),
  E(10, 'Hereditary', 'movie', 0.82, ['horror', 'drama'], 'Synthetic note: a family horror story.'),
  E(11, 'Her', 'movie', 0.83, ['science_fiction', 'romance', 'artificial_intelligence', 'drama'], 'Synthetic note: a gentle romance with an operating system.'),
  E(12, 'Contact', 'movie', 0.78, ['science_fiction', 'first_contact', 'space', 'cerebral'], 'Synthetic note: a scientist hunts for a signal from the stars.'),
  E(13, 'Notting Hill', 'movie', 0.85, ['romance', 'comedy', 'feel_good'], 'Synthetic note: a bookshop romantic comedy.'),
  E(14, 'Gravity', 'movie', 0.82, ['space', 'thriller', 'drama'], 'Synthetic note: a survival thriller in orbit.'),
  E(21, 'Uniqlo', 'brand', 0.92, ['minimalist', 'basics', 'japanese'], 'Synthetic note: a casual-basics clothing brand.'),
  E(22, 'Patagonia', 'brand', 0.88, ['outdoor', 'sustainable', 'minimalist'], 'Synthetic note: an outdoor apparel brand.'),
  E(23, 'Muji', 'brand', 0.8, ['minimalist', 'japanese', 'basics'], 'Synthetic note: a no-brand-style household and apparel brand.'),
  E(24, 'COS', 'brand', 0.74, ['minimalist', 'scandinavian', 'formal'], 'Synthetic note: a modern minimalist fashion label.'),
  E(25, 'Everlane', 'brand', 0.7, ['minimalist', 'sustainable', 'basics'], 'Synthetic note: a basics label focused on transparency.'),
  E(26, 'Supreme', 'brand', 0.9, ['streetwear', 'logo_heavy', 'graphic'], 'Synthetic note: a streetwear label known for box logos.'),
  E(27, 'Ed Hardy', 'brand', 0.66, ['graphic', 'logo_heavy', 'streetwear'], 'Synthetic note: a label known for loud graphic prints.'),
  E(28, "Arc'teryx", 'brand', 0.79, ['outdoor', 'minimalist'], 'Synthetic note: a technical outdoor brand.'),
  E(31, 'Project Hail Mary', 'book', 0.85, ['science_fiction', 'space', 'first_contact'], 'Synthetic note: a lone astronaut saves the day.'),
  E(32, 'The Martian', 'book', 0.86, ['science_fiction', 'space'], 'Synthetic note: survival on Mars, told with humour.'),
  E(33, 'Pride and Prejudice', 'book', 0.9, ['romance', 'drama'], 'Synthetic note: a classic of manners and romance.'),
  E(41, 'Radiohead', 'artist', 0.88, ['indie', 'electronic'], 'Synthetic note: an experimental rock band.'),
  E(42, 'Daft Punk', 'artist', 0.9, ['electronic'], 'Synthetic note: an electronic duo.'),
  E(43, 'ABBA', 'artist', 0.92, ['pop'], 'Synthetic note: a pop group.'),
];
const BY_ID = new Map(CATALOG.map((e) => [e.id, e]));

function tagObjects(entity) {
  return entity.tags.map((slug) => TAG_BY_SLUG[slug]).filter(Boolean).map((t) => ({ id: t.id, name: t.name, type: t.type }));
}

function jsonResponse(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => 'application/json' },
    async json() { return JSON.parse(JSON.stringify(body)); },
    async text() { return JSON.stringify(body); },
  };
}

function signalTagSet(entityIds, tagIds) {
  const set = new Set();
  for (const id of entityIds) for (const t of BY_ID.get(id)?.tags || []) set.add(`urn:tag:demo:${t}`);
  for (const t of tagIds) set.add(t);
  return set;
}

function mockAffinity(entity, signalTags) {
  const own = entity.tags.map((t) => `urn:tag:demo:${t}`);
  const overlap = own.filter((t) => signalTags.has(t)).length / Math.max(1, own.length);
  return Math.round(Math.min(0.99, 0.12 + 0.8 * overlap + 0.05 * entity.popularity) * 1000) / 1000;
}

function explain(entity, signalEntityIds) {
  const own = new Set(entity.tags);
  return signalEntityIds.map((id) => {
    const s = BY_ID.get(id);
    if (!s) return { entity_id: id, score: 0 };
    const inter = s.tags.filter((t) => own.has(t)).length;
    const union = new Set([...s.tags, ...entity.tags]).size;
    return { entity_id: id, score: Math.round((inter / Math.max(1, union)) * 1000) / 1000 };
  }).sort((a, b) => b.score - a.score);
}

const VALID_TYPES = new Set(['artist', 'book', 'brand', 'destination', 'movie', 'person', 'place', 'podcast', 'tv_show', 'video_game'].map((t) => `urn:entity:${t}`));

/** fetch()-compatible mock of the Qloo hackathon API (search, v2/tags, v2/insights). */
export async function mockQlooFetch(url) {
  const u = new URL(url);
  const q = u.searchParams;
  const list = (k) => (q.get(k) || '').split(',').map((s) => s.trim()).filter(Boolean);
  const take = Math.max(1, Math.min(50, Number(q.get('take') || 5)));
  if (u.pathname === '/search') {
    const query = normalizeText(q.get('query'));
    const types = list('types');
    const hits = CATALOG
      .filter((e) => !types.length || types.includes(e.type))
      .map((e) => ({ e, n: normalizeText(e.name) }))
      .filter(({ n }) => query && (n === query || n.includes(query) || (query.includes(n) && n.length >= 4)))
      .sort((a, b) => (b.n === query) - (a.n === query) || b.e.popularity - a.e.popularity)
      .slice(0, take)
      .map(({ e }) => ({
        name: e.name, entity_id: e.id, types: [e.type], properties: { description: e.description },
        popularity: e.popularity, disambiguation: 'synthetic demo entity', tags: tagObjects(e),
      }));
    return jsonResponse(200, { results: hits });
  }
  if (u.pathname === '/v2/tags') {
    const query = normalizeText(q.get('filter.query'));
    const tags = TAGS.filter((t) => query && (normalizeText(t.name).includes(query) || t.slug.includes(query.replace(/ /g, '_'))))
      .slice(0, take)
      .map(({ id, name, type, parents, popularity }) => ({ id, name, type, parents, popularity }));
    return jsonResponse(200, { success: true, results: { tags } });
  }
  if (u.pathname === '/v2/insights') {
    const type = q.get('filter.type');
    if (!VALID_TYPES.has(type)) return jsonResponse(403, { success: false, error: 'unsupported filter.type' });
    const signalEntities = list('signal.interests.entities');
    const signalTags = list('signal.interests.tags');
    if (type === 'urn:entity:destination' && !signalEntities.length) {
      return jsonResponse(400, { success: false, error: 'destination requires signal.interests.entities' });
    }
    const tagSet = signalTagSet(signalEntities, signalTags);
    const restrict = list('filter.results.entities');
    const exclude = new Set(list('filter.exclude.entities'));
    let pool = restrict.length
      ? restrict.map((id) => BY_ID.get(id)).filter((e) => e && e.type === type)
      : CATALOG.filter((e) => e.type === type && !exclude.has(e.id) && !signalEntities.includes(e.id));
    pool = pool.map((e) => ({ e, a: mockAffinity(e, tagSet) })).sort((x, y) => y.a - x.a).slice(0, take);
    const entities = pool.map(({ e, a }) => ({
      name: e.name, entity_id: e.id, popularity: e.popularity,
      properties: { description: e.description }, tags: tagObjects(e),
      query: { affinity: a, explainability: { 'signal.interests.entities': explain(e, signalEntities) } },
    }));
    return jsonResponse(200, { success: true, results: { entities } });
  }
  return jsonResponse(404, { success: false, error: 'not found' });
}

// ------------------------------------------------------------------ Gemini mock

const COLORS = ['navy', 'black', 'white', 'gray', 'beige', 'olive', 'brown', 'khaki', 'cream', 'neon', 'red', 'pink', 'green', 'blue', 'yellow', 'orange', 'purple', 'charcoal', 'earth tones'];
const STYLES = ['minimalist', 'minimal', 'graphic', 'formal', 'casual', 'oversized', 'slim', 'vintage', 'streetwear', 'tailored'];
const MATERIALS = ['linen', 'cotton', 'wool', 'denim', 'polyester', 'leather', 'silk', 'cashmere'];

const DOMAIN_RULES = [
  ['technical', /(\d+(?:\.\d+)?\s*%|\bpercent\b|\bcalculate\b|\bconvert\b|\bregex\b|\bformula\b|\bcode\b|\bpython\b|\bjavascript\b|\bsquare root\b)/i],
  ['fashion', /\b(shirt|shirts|tee|t-shirt|jacket|dress|outfit|wear|shoes|sneakers|jeans|coat|hoodie|blazer|suit)\b/i],
  ['podcasts', /\bpodcasts?\b/i],
  ['tv', /\b(tv|series|episode|sitcom)\b/i],
  ['movies', /\b(movie|movies|film|films|cinema)\b/i],
  ['music', /\b(song|songs|album|artist|band|music|playlist)\b/i],
  ['books', /\b(book|books|novel|novels|read)\b/i],
  ['games', /\b(video game|game|games|gaming)\b/i],
  ['hotels', /\bhotels?\b/i],
  ['dining', /\b(restaurant|restaurants|dinner|lunch|brunch|cafe|eat)\b/i],
  ['travel', /\b(trip|travel|destination|vacation|holiday|city break)\b/i],
  ['brands', /\bbrands?\b/i],
];

function findCatalog(text, types) {
  const n = ` ${normalizeText(text)} `;
  return CATALOG.filter((e) => (!types || types.includes(e.type)) && n.includes(` ${normalizeText(e.name)} `))
    .sort((a, b) => b.name.length - a.name.length);
}

function detectDomain(text) {
  const movieHit = findCatalog(text, ['urn:entity:movie']);
  for (const [domain, re] of DOMAIN_RULES) {
    if (domain === 'technical' && re.test(text)) return 'technical';
    if (domain !== 'technical' && re.test(text)) return domain;
  }
  if (movieHit.length) return 'movies';
  if (findCatalog(text, ['urn:entity:brand']).length) return 'brands';
  if (findCatalog(text, ['urn:entity:book']).length) return 'books';
  if (findCatalog(text, ['urn:entity:artist']).length) return 'music';
  return 'other';
}

function splitOptions(question, optionsText) {
  const src = String(optionsText || '').trim();
  if (src) {
    return src.split(/\s*(?:\n|;|\s\/\s)\s*/).map((s) => s.trim()).filter(Boolean).slice(0, 6);
  }
  const m = /:\s*(.+)$/.exec(String(question || ''));
  if (m && /\sor\s/i.test(m[1])) {
    return m[1].replace(/\?+\s*$/, '').split(/\s*,\s*|\s+or\s+/i).map((s) => s.trim()).filter(Boolean).slice(0, 6);
  }
  return [];
}

function attributesFor(text, domain) {
  const n = ` ${normalizeText(text)} `;
  const attrs = [];
  for (const c of COLORS) if (n.includes(` ${normalizeText(c)} `)) attrs.push({ name: 'color', value: c });
  for (const s of STYLES) if (n.includes(` ${s} `)) attrs.push({ name: 'style', value: s === 'minimal' ? 'minimalist' : s });
  for (const m of MATERIALS) if (n.includes(` ${m} `)) attrs.push({ name: 'material', value: m });
  if (/\blarge logo|\bbig logo|\bloud logo/.test(n)) attrs.push({ name: 'feature', value: 'large logo' });
  else if (/\blogo\b/.test(n) && !/\bno logo\b/.test(n)) attrs.push({ name: 'feature', value: 'logo' });
  if (domain === 'movies' || domain === 'books') {
    const e = findCatalog(text, [DOMAIN_TYPE[domain]])[0];
    for (const slug of e?.tags?.slice(0, 2) || []) attrs.push({ name: 'genre', value: TAG_BY_SLUG[slug].name.toLowerCase() });
  }
  return attrs;
}

export function mockExtract({ question = '', optionsText = '', images = [], location = '' }) {
  const all = `${question} ${optionsText}`;
  const domain = detectDomain(all);
  const type = DOMAIN_TYPE[domain] || null;
  const raw = splitOptions(question, optionsText);
  let options = raw.map((text, i) => {
    const lm = /^([A-Z1-9])\s*[:).-]\s*(.+)$/.exec(text);
    const letter = lm ? lm[1] : null;
    const body = lm ? lm[2] : text;
    const hit = type ? findCatalog(body, [type])[0] || findCatalog(body)[0] : findCatalog(body)[0];
    const brand = findCatalog(body, ['urn:entity:brand'])[0];
    const attrs = attributesFor(body, domain);
    if (brand) attrs.push({ name: 'brand', value: brand.name });
    let entity = { name: null, type: null };
    if (domain === 'fashion' || domain === 'brands') { if (brand) entity = { name: brand.name, type: 'urn:entity:brand' }; }
    else if (hit) entity = { name: hit.name, type: hit.type };
    else if (type && !letter) entity = { name: body.replace(/[!?.]+$/, '') || body, type };
    return {
      label: letter ? `${letter}: ${body}`.slice(0, 80) : body.slice(0, 80),
      description: body.slice(0, 200),
      attributes: attrs.slice(0, 8),
      entity,
      fails_constraints: [],
      _index: i,
    };
  }).map(({ _index, ...o }) => o);
  if (!options.length && images.length) {
    options = images.slice(0, 3).map((_, i) => ({
      label: `Image ${i + 1}`,
      description: `Uploaded image ${i + 1}. Mock mode cannot see images, so no visual attributes were extracted.`,
      attributes: [],
      entity: { name: null, type: null },
      fails_constraints: [],
    }));
  }
  const task = domain === 'technical' ? 'answer' : options.length >= 2 ? (/\brank\b/i.test(question) ? 'rank' : 'choose') : options.length === 1 ? 'choose' : (type ? 'recommend' : 'answer');
  const ctx = /\b(wedding|interview|date night|date|party|office|work|funeral|beach|gym|dinner party)\b/i.exec(all);
  const loc = location || (/\bin ([A-Z][a-z]+(?: [A-Z][a-z]+)?)\b/.exec(question)?.[1] || '');
  const constraints = [];
  const budget = /\b(under|below|less than)\s*\$?\s*(\d+)/i.exec(all);
  if (budget) constraints.push(`${budget[1].toLowerCase()} $${budget[2]}`);
  for (const w of ['vegetarian', 'vegan', 'kid-friendly', 'family-friendly']) if (all.toLowerCase().includes(w)) constraints.push(w);
  const mentioned = [];
  const likeRe = /\bi (?:really )?(love|like|enjoy|adore|hate|dislike|can't stand|don't like)\s+([^.,;!?]+)/gi;
  let mm;
  while ((mm = likeRe.exec(question)) && mentioned.length < 3) {
    const neg = /hate|dislike|can't|don't/i.test(mm[1]);
    const value = mm[2].replace(/\b(films?|movies?|tonight|these|this)\b/gi, '').trim();
    if (value && value.length <= 40 && !/^(it|that|this|them|to)\b/i.test(value)) {
      const ent = findCatalog(value, type ? [type] : undefined)[0];
      mentioned.push({ domain: DOMAIN_TYPE[domain] ? domain : 'lifestyle', attribute: ent ? 'entity' : 'general', value: ent ? ent.name : value, polarity: neg ? -1 : 1 });
    }
  }
  return { domain, task, options, constraints, context: ctx ? ctx[1].toLowerCase() : '', location: loc, mentioned_preferences: mentioned };
}

function mockArithmetic(question) {
  const m = /(\d+(?:\.\d+)?)\s*%\s*of\s*([\d,]+(?:\.\d+)?)/i.exec(question || '');
  if (!m) return null;
  const pct = Number(m[1]);
  const base = Number(m[2].replace(/,/g, ''));
  const value = Math.round(((pct / 100) * base) * 1e6) / 1e6;
  return { text: String(value), working: `${pct}% × ${base.toLocaleString('en-US')} = ${value.toLocaleString('en-US')}` };
}

function mostPopular(domain) {
  const type = DOMAIN_TYPE[domain];
  return CATALOG.filter((e) => e.type === type).sort((a, b) => b.popularity - a.popularity)[0]?.name || null;
}

/** Deterministic stand-in for the Gemini calls. stage: extract | final_off | final_on */
export function mockGemini(stage, input) {
  if (stage === 'extract') return mockExtract(input);
  const { question = '', extraction = {} } = input;
  const options = extraction.options || [];
  const isAnswer = extraction.task === 'answer' || extraction.domain === 'technical' || (!options.length && !DOMAIN_TYPE[extraction.domain]);
  if (isAnswer) {
    const a = mockArithmetic(question);
    const base = {
      choice: a ? a.text : 'Mock mode can only answer simple percentage questions.',
      rationale: a ? `${a.working}.` : 'This is a factual question; the mock model has no general knowledge.',
      confidence: a ? 'high' : 'low',
    };
    if (stage === 'final_off') return { ...base, notes: 'Mock Gemini answer.' };
    return { ...base, rationale: `${base.rationale} Taste memory is not relevant to this question, so Qloo was not called.`, cited_preferences: [], cited_qloo: [], uncertainty: 'None for the arithmetic itself.' };
  }
  if (stage === 'final_off') {
    if (options.length) {
      return {
        choice: options[0].label,
        rationale: 'Without any information about your personal taste, both options are reasonable. The first is the safer general pick. (Mock Gemini-only answer.)',
        confidence: 'low',
        notes: 'No taste profile or Qloo data was used.',
      };
    }
    const pick = mostPopular(extraction.domain) || 'a popular, well-reviewed pick';
    return { choice: pick, rationale: `${pick} is a widely popular choice in this category. I do not know your taste, so this is a generic suggestion. (Mock Gemini-only answer.)`, confidence: 'low', notes: 'Generic popularity-based suggestion.' };
  }
  // final_on: follow the engine brief honestly.
  const brief = input.brief || {};
  const grounded = !!brief.qloo_grounded;
  const caveat = (brief.caveats || []).slice(0, 2).join(' ');
  if (!options.length) {
    const rec = (brief.recommendations || [])[0];
    if (!rec) {
      return { choice: 'No grounded recommendation available', rationale: 'I could not get recommendations from Qloo, and your taste memory alone cannot name new titles reliably.', cited_preferences: [], cited_qloo: [], confidence: 'low', uncertainty: caveat || 'Add a few liked titles and try again.' };
    }
    const likes = (brief.relevant_preferences || []).filter((p) => p.polarity > 0).slice(0, 3);
    return {
      choice: rec.name,
      rationale: `Because you like ${likes.map((p) => p.value).join(', ') || 'similar titles'}, ${rec.name} is the strongest match in Qloo's taste graph${rec.why ? ` (${rec.why})` : ''}.`,
      cited_preferences: likes.map((p) => p.id),
      cited_qloo: grounded ? [`${rec.name}: affinity ${rec.affinity}`] : [],
      confidence: brief.engine_confidence || 'medium',
      uncertainty: caveat || 'Based on a handful of liked titles; more feedback will sharpen this.',
    };
  }
  const top = (brief.options || []).find((o) => o.rank === 1) || (brief.options || [])[0] || { label: options[0].label, profile_evidence: [] };
  const plus = (top.profile_evidence || []).filter((e) => e.sign === '+');
  const minusOthers = (brief.options || []).filter((o) => o !== top).flatMap((o) => (o.profile_evidence || []).filter((e) => e.sign === '−').map((e) => ({ ...e, option: o.label })));
  const parts = [];
  if (plus.length) parts.push(`it ${plus.slice(0, 3).map((e) => e.text.split(' · ')[0]).join('; ')}`);
  if (minusOthers.length) parts.push(`the alternative ${minusOthers.slice(0, 2).map((e) => e.text.split(' · ')[0]).join('; ')}`);
  if (grounded && top.qloo_affinity != null) parts.push(`Qloo gives ${top.qloo_entity} an affinity of ${top.qloo_affinity} with your taste signals`);
  return {
    choice: top.label,
    rationale: parts.length ? `${top.label} fits you better: ${parts.join(', and ')}.` : `${top.label} is a slightly better fit, but your taste memory has little evidence here.`,
    cited_preferences: [...plus, ...minusOthers].map((e) => e.pref_id).filter(Boolean).slice(0, 4),
    cited_qloo: grounded && top.qloo_affinity != null ? [`${top.qloo_entity}: affinity ${top.qloo_affinity}`] : [],
    confidence: brief.engine_confidence || 'low',
    uncertainty: caveat || 'Visual details such as fit were judged from the description only.',
  };
}
