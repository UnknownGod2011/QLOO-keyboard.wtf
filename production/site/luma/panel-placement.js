// Pure viewport geometry. Captions never share a rectangle with the crystal
// or its bottom controls, even when a long caption must scroll on mobile.
export function placeLumaPanel({viewport, pet, panel, anchor, clear = () => true}) {
  const margin = 12, width = Math.min(panel.width, viewport.width - margin * 2);
  const height = Math.min(panel.height, viewport.height - margin * 2);
  if (width <= 0 || height <= 0) return null;
  const right = viewport.width - width - margin;
  const bottom = viewport.height - height - margin;
  const clamp = (value, max) => Math.max(margin, Math.min(max, value));
  const separate = (p, h) => p.x + width <= pet.x - 8 || p.x >= pet.x + pet.width + 8
    || p.y + h <= pet.y - 8 || p.y >= pet.y + pet.height + 44;
  const fits = (p, h) => p.x >= margin && p.y >= margin && p.x + width <= viewport.width - margin
    && p.y + h <= viewport.height - margin && separate(p, h);
  const distance = p => Math.hypot(Math.max(pet.x - p.x - width, p.x - pet.x - pet.width, 0),
    Math.max(pet.y - p.y - height, p.y - pet.y - pet.height - 36, 0));
  if (anchor?.width === width) {
    const p = {x:clamp(anchor.x, right), y:clamp(anchor.y, bottom)};
    if (fits(p, height) && distance(p) <= 48) return {...p, maxHeight:height};
  }
  const candidates = [
    {x:pet.x + pet.width + 16, y:pet.y + pet.height - height},
    {x:pet.x - width - 16, y:pet.y + pet.height - height},
    {x:clamp(pet.x + (pet.width - width) / 2, right), y:pet.y - height - 16},
    {x:clamp(pet.x + (pet.width - width) / 2, right), y:pet.y + pet.height + 44},
  ];
  for (const y of [margin, bottom]) for (const x of [margin, right]) candidates.push({x,y});
  for (let y = margin; y <= bottom; y += 48) for (let x = margin; x <= right; x += 64) candidates.push({x,y});
  candidates.sort((a,b) => distance(a) - distance(b));
  const full = candidates.find(p => fits(p, height) && distance(p) <= 48 && clear(p.x,p.y,width,height))
    || candidates.find(p => fits(p, height) && distance(p) <= 48)
    || candidates.find(p => fits(p, height));
  if (full) return {...full, maxHeight:height};
  // No full-size location: use real free space, never a clamped overlapping
  // fallback. Only the caption's scroll area shrinks; fonts never scale.
  const compact = [
    {x:clamp(pet.x + (pet.width - width) / 2, right), y:margin, maxHeight:Math.min(height,pet.y - 8 - margin)},
    {x:clamp(pet.x + (pet.width - width) / 2, right), y:pet.y + pet.height + 44, maxHeight:Math.min(height,viewport.height - margin - pet.y - pet.height - 44)},
  ].filter(p => p.maxHeight >= 32 && fits(p,p.maxHeight));
  compact.sort((a,b) => b.maxHeight - a.maxHeight || distance(a) - distance(b));
  return compact[0] || null;
}
