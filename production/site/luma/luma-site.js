import {Luma} from './luma.js';
import {mountCrystalTrail} from './crystal-trail.js';
import {mountWindSnow} from './wind-snow.js';
import {WinterAudio, MOODS} from './winter-audio.js';
import {getTourSteps} from './product-guide.js';
import {placeLumaPanel} from './panel-placement.js';

const randomBetween = (min, max) => min + Math.random() * (max - min);
// Catch up to actual provider captions without inventing text or queuing timers.
export function nextCaptionLength(current, wanted, elapsed, reduced = false) {
  return reduced ? wanted : Math.min(wanted, current + Math.max(1, Math.ceil(Math.max(0, elapsed) * .12)));
}
const safeStore = {
  get(key) {try {return localStorage.getItem(`keyboardwtf-luma-${key}`);} catch {return null;}},
  set(key, value) {try {localStorage.setItem(`keyboardwtf-luma-${key}`, String(value));} catch {/* Optional preferences never block Settings. */}},
};
const icons = {
  voice: '<path d="M8 2a3 3 0 0 0-3 3v4a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z"/><path d="M2 8v1a6 6 0 0 0 12 0V8M8 15v3M5 18h6"/>',
  cursor: '<path d="m3 2 11 7-5 1-2 5Z"/><path d="m10 11 3 5"/>',
  sound: '<path d="m9 3-5 4H1v4h3l5 4Z"/><path d="M12 5a6 6 0 0 1 0 8M14 2a10 10 0 0 1 0 14"/>',
  settings: '<path d="m7 1-1 2-2 1-2-.2L1 6l1.6 1.4v2.3L1 11l1 2.2 2-.2 2 1 1 2h3l1-2 2-1 2 .2 1-2.2-1.6-1.3V7.4L16 6l-1-2.2-2 .2-2-1-1-2Z"/><circle cx="8.5" cy="8.5" r="2.6"/>',
};

/** Isolated presentation only. Never starts Jarvis, changes settings, or executes product actions. */
export function mountLuma({surface = 'website', root, voiceAdapter, allowedState = {}} = {}) {
  surface = surface === 'settings' ? 'settings' : 'website';
  const ownsRoot = !root;
  if (!root) {root = document.createElement('section'); root.setAttribute('aria-label', 'Luma product guide'); document.body.append(root);}
  if (root.dataset.lumaMounted === 'true') throw new Error('Luma is already mounted in this habitat.');
  root.dataset.lumaMounted = 'true';
  const shell = document.createElement('div');
  shell.className = 'luma-shell'; shell.dataset.surface = surface;
  shell.dataset.theme = document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
  shell.innerHTML = `<div class="luma-content">
    <div class="luma-bubble" data-luma-bubble role="region" aria-label="Luma conversation and guidance" hidden>
      <h3 data-luma-title></h3><p data-luma-speech></p>
      <p class="luma-caption-meta" data-luma-caption-meta hidden></p>
      <div class="luma-bubble-actions" data-luma-guide-actions hidden>
        <button type="button" data-luma-back>Back</button><button class="luma-primary" type="button" data-luma-next>Take a quick tour</button><button type="button" data-luma-skip>Not now</button>
      </div>
      <div class="luma-bubble-actions" data-luma-voice-actions hidden>
        <button type="button" data-luma-mic-mute aria-pressed="false">Mute microphone</button><button type="button" data-luma-voice-stop>Stop conversation</button>
      </div>
      <div class="luma-help-links" data-luma-help-links hidden><a href="https://chromewebstore.google.com/detail/gojdebdikepkpapmnggeljneamjdigbh" target="_blank" rel="noopener noreferrer">Chrome Helper ↗</a><a href="https://keyboardwtf.com/pricing.html" target="_blank" rel="noopener noreferrer">Pricing ↗</a><a href="https://keyboardwtf.com/support.html" target="_blank" rel="noopener noreferrer">Support ↗</a></div>
    </div>
    <div class="luma-stage" data-luma-stage aria-label="Luma’s roaming space">
      <div class="luma-pet" data-luma-pet><span class="halo" aria-hidden="true"></span><span class="ground-shadow" aria-hidden="true"></span>
        <button type="button" class="luma-body" data-luma-body aria-label="Say hello to Luma"><canvas width="192" height="208" data-luma-sprite aria-hidden="true"></canvas></button>
        <span class="luma-sleep" aria-hidden="true">z z</span></div>
      <canvas class="luma-particles" data-luma-particles aria-hidden="true"></canvas>
    </div>
    <div class="luma-toolbar" aria-label="Luma controls">
      <button type="button" class="luma-icon" data-luma-voice aria-label="Talk with Luma" title="Talk with Luma" aria-pressed="false"><svg viewBox="0 0 18 20" aria-hidden="true">${icons.voice}</svg></button>
      <button type="button" class="luma-icon" data-luma-cursor aria-label="Cursor: iridescent glass. Change to transparent water" title="Cursor: iridescent glass"><svg viewBox="0 0 18 18" aria-hidden="true">${icons.cursor}</svg></button>
      <button type="button" class="luma-icon" data-luma-ambience aria-label="Enable ambience" title="Ambience starts muted" aria-pressed="false"><svg viewBox="0 0 18 18" aria-hidden="true">${icons.sound}</svg></button>
      <button type="button" class="luma-icon" data-luma-options aria-label="Customize Luma" title="Customize Luma" aria-expanded="false" aria-haspopup="dialog"><svg viewBox="0 0 18 18" aria-hidden="true">${icons.settings}</svg></button>
    </div>
    <div class="luma-footer"><span class="luma-activity" data-luma-activity>Luma</span><button type="button" data-luma-tour>Quick tour</button></div>
    <div class="luma-menu" data-luma-sound-menu hidden>
      <h3>A little atmosphere</h3><label class="luma-moods">Sound mood<select data-luma-mood><option value="auto">Match the light</option>${Object.entries(MOODS).map(([key, mood]) => `<option value="${key}">${mood.name}</option>`).join('')}</select></label>
      <label>Volume <input type="range" data-luma-volume min="0" max="100" value="100" aria-label="Ambience volume"></label><p data-luma-audio-status>Muted until you enable it.</p>
      <button type="button" data-luma-sound-close>Close ambience controls</button>
    </div>
    <div class="luma-menu" data-luma-options-menu role="dialog" aria-label="Customize Luma" hidden><h3>Make yourself comfortable</h3>
      ${surface === 'website' ? '<label class="luma-surface-picker">Dark surface<select data-luma-surface-style aria-label="Dark hero surface"><option value="current">Polished glass</option><option value="window-glass">Window glass</option><option value="thick-glass">Thicker glass</option><option value="matte">Matte black</option></select></label>' : ''}
      <label><input type="checkbox" data-luma-wandering> Let Luma wander</label>
      <label><input type="checkbox" data-luma-pause> Pause Luma and effects</label>
      <label><input type="checkbox" data-luma-reduced> Reduce motion</label>
      <label><input type="checkbox" data-luma-narration> Read tour captions aloud</label>
      <p>${surface === 'settings' ? 'Luma uses your Personal API key. Provider charges may apply; no Managed credits are used. ' : ''}Microphone starts only through Voice. Luma never controls your computer or changes your account.</p>
      <div class="luma-controls-row"><button type="button" data-luma-mute-all>Mute all Luma sound</button><button type="button" data-luma-hide>Hide Luma</button></div>
    </div>
  </div><button type="button" class="luma-reopen" data-luma-reopen hidden>Show Luma</button><p class="luma-status-sr" data-luma-announcement aria-live="polite" aria-atomic="true"></p>`;
  root.append(shell);
  const $ = name => shell.querySelector(`[data-luma-${name}]`);
  // Four icons travel with the actor. The hover target is only its bottom edge,
  // not a permanent footer and not the whole page-sized overlay.
  const dock = document.createElement('div');
  dock.className = 'luma-dock'; dock.tabIndex = 0;
  dock.setAttribute('aria-label', 'Luma controls. Voice, cursor, ambience and settings.');
  dock.append(shell.querySelector('.luma-toolbar'));
  $('pet').append(dock);
  shell.append($('activity'));
  $('options-menu').append($('tour'));
  shell.querySelector('.luma-footer').remove();
  $('body').setAttribute('aria-label', 'Luma. Tap to reveal controls. Right click or Shift+F10 for companion options.');
  const removers = [];
  const listen = (element, name, callback, options) => {element.addEventListener(name, callback, options); removers.push(() => element.removeEventListener(name, callback, options));};
  let destroyed = false, hidden = safeStore.get(`hidden-${surface}`) === 'true', inView = true;
  let paused = false, voiceBusy = false, voiceSpeaking = false, micMuted = false, narrationSpeaking = false;
  let renderingBlocked = true;
  let wandering = safeStore.get('wandering') !== 'false';
  const surfaceStyles = new Set(['current','window-glass','thick-glass','matte']);
  const requestedSurface = new URLSearchParams(location.search).get('surface');
  // Polished glass is the approved default. Old experiment preferences cannot
  // silently replace it; explicit review URLs still allow material comparison.
  let surfaceStyle = surfaceStyles.has(requestedSurface) ? requestedSurface : 'current';
  if(!surfaceStyles.has(surfaceStyle))surfaceStyle='current';
  if(surface === 'website')document.documentElement.dataset.lumaSurface=surfaceStyle;
  const motionPreference = matchMedia('(prefers-reduced-motion: reduce)');
  let motionOverride = safeStore.get('reduced');
  let reduced = motionOverride === null ? motionPreference.matches : motionOverride === 'true';
  let cursor = 0, trail = null, snow = null, soundEnabled = false, manualMood = false;
  let tourIndex = -1, narration = false, utterance = null, introPending = true, welcomeUntil = 0, controlsHeld = false;
  const steps = getTourSteps(surface);
  const pet = new Luma($('stage'));
  pet.configure('size', innerWidth <= 600 ? 72 : 118);
  pet.configure('roaming', false); pet.configure('reduced', reduced); pet.configure('glow', .055);
  const trailCanvas = document.createElement('canvas'); trailCanvas.className = 'luma-effects'; trailCanvas.hidden = true; trailCanvas.setAttribute('aria-hidden', 'true');
  const snowCanvas = document.createElement('canvas'); snowCanvas.className = 'luma-effects luma-snow'; snowCanvas.hidden = true; snowCanvas.setAttribute('aria-hidden', 'true');
  document.body.append(trailCanvas, snowCanvas);
  const suspended = () => destroyed || hidden || paused || document.hidden || !inView;
  const audio = new WinterAudio({status: $('audio-status'), isPaused: suspended, wind: () => .22, duck: () => voiceSpeaking || narrationSpeaking});
  audio.setVolume(1); audio.setMood(shell.dataset.theme === 'dark' ? 'lobby' : 'dream');
  let nextIdle = 18000, nextPeek = randomBetween(240000, 480000), nextDance = randomBetween(480000, 900000), nextMood = randomBetween(180000, 360000), nextGust = randomBetween(5000, 11000);
  let lastInteraction = 0;
  let typedCaption = null, drag = null, suppressDragClick = false;

  // Layout-only rectangles, never text/account data. Cache outside the render
  // loop: roaming stays clear of existing text, links, forms and downloads.
  let layoutTimer = null, obstacleRects = [], panelSizes = new Map(), panelPositions = new Map(), panelAnchors = new Map(), layoutEpoch = 0;
  function refreshClearance() {
    layoutTimer = null;
    if (destroyed || document.hidden) return;
    const size = innerWidth <= 600 ? 72 : 118;
    if(pet.options.size!==size)pet.configure('size',size);
    const rects = [];
    const visibleRect = r => r.width > 0 && r.height > 0 && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth;
    for (const element of document.querySelectorAll('a,button,input,select,textarea,summary,h1,h2,h3,h4,p,label,li,.brand,.small,.balance,.connector-name,.hint')) {
      if (element.closest('.luma-shell,.luma-effects')) continue;
      const r = element.getBoundingClientRect();
      if (!visibleRect(r)) continue;
      if (element.matches('a,button,input,select,textarea,summary')) rects.push(r);
      else {
        const range = document.createRange(); range.selectNodeContents(element);
        for (const line of range.getClientRects()) if (visibleRect(line)) rects.push(line);
        range.detach();
      }
      if (rects.length >= 600) break;
    }
    obstacleRects = rects.map(r => ({left:r.left,right:r.right,top:r.top,bottom:r.bottom}));
    layoutEpoch++;
    pet.setObstacles(obstacleRects);
    positionPanels();
  }
  const queueClearance = () => {
    if (destroyed) return;
    clearTimeout(layoutTimer); layoutTimer = setTimeout(refreshClearance, 90);
  };
  function measurePanel(panel) {
    if(panel.hidden)return;
    // Content can change without resizing an already constrained scroll box.
    // Measure only on content/UI events, never in the walking render loop.
    panel.style.maxHeight='calc(100dvh - 24px)';
    panelSizes.set(panel,{width:panel.offsetWidth,height:panel.scrollHeight+2});
    panelPositions.delete(panel);
    positionPanels();
  }
  function positionPanels() {
    for (const panel of [$('bubble'),$('sound-menu'),$('options-menu')]) {
      if (panel.hidden) continue;
      const size = panelSizes.get(panel) || {width:Math.min(290,innerWidth-24),height:160};
      const width = size.width, height = Math.min(size.height,innerHeight-24);
      const signature = [pet.x.toFixed(1),pet.y.toFixed(1),width,height,innerWidth,innerHeight,layoutEpoch].join(':');
      if(panelPositions.get(panel)===signature)continue;
      panelPositions.set(panel,signature);
      const point = placeLumaPanel({
        viewport:{width:innerWidth,height:innerHeight},pet:{x:pet.x,y:pet.y,width:pet.options.size,height:pet.petH},panel:{width,height},
        anchor:panel===$('bubble')&&(voiceBusy||tourIndex>=0)?panelAnchors.get(panel):null,
        clear:(x,y,w,h)=>pet.clearOfContent(x,y,w,h),
      });
      if (!point) {panel.hidden=true;continue;}
      // Unsolicited greetings never sit over page copy. Explicit guidance can
      // use a conventional popover on dense pages, but never cover Luma.
      if (welcomeUntil && tourIndex<0 && !voiceBusy && !pet.clearOfContent(point.x,point.y,width,point.maxHeight)) {panel.hidden=true;continue;}
      panel.style.maxHeight = `${point.maxHeight}px`;
      panelAnchors.set(panel,{...point,width});
      panel.style.transform = `translate3d(${point.x}px,${point.y}px,0)`;
    }
  }
  const panelObserver = new ResizeObserver(entries => {
    for (const entry of entries) if(entry.contentRect.width>0)
      panelSizes.set(entry.target,{width:entry.target.offsetWidth,height:entry.target.scrollHeight+2});
    positionPanels();
  });
  [$('bubble'),$('sound-menu'),$('options-menu')].forEach(panel=>panelObserver.observe(panel));
  const pageObserver = new MutationObserver(records => {
    if(records.some(record=>!record.target.closest?.('.luma-shell,.luma-effects'))) queueClearance();
  });
  pageObserver.observe(document.querySelector('main') || document.querySelector('.layout') || document.body,{subtree:true,childList:true,characterData:true});
  listen(window,'resize',queueClearance,{passive:true});
  listen(document,'scroll',queueClearance,{passive:true,capture:true});
  listen(document,'pointerdown',queueClearance,{passive:true});
  listen(document,'keydown',queueClearance);
  document.fonts?.ready.then(()=>{if(!destroyed)queueClearance();});
  refreshClearance();

  function stopNarration() {
    if (utterance && 'speechSynthesis' in window) speechSynthesis.cancel();
    utterance = null; narrationSpeaking = false; audio.sync();
  }
  function speakCaption(text) {
    stopNarration();
    if (!narration || suspended() || voiceBusy || !('speechSynthesis' in window)) return;
    utterance = new SpeechSynthesisUtterance(text); utterance.rate = .98; utterance.pitch = 1.04;
    utterance.onstart = () => {narrationSpeaking = true; audio.sync();};
    utterance.onend = utterance.onerror = () => {narrationSpeaking = false; utterance = null; audio.sync();};
    speechSynthesis.speak(utterance);
  }
  function announce(message) { $('announcement').textContent = message; }
  function caption(title, message, duration = 9000) {
    typedCaption = null;
    if($('bubble').hidden)panelAnchors.delete($('bubble'));
    $('title').textContent = title;
    pet.say(String(message).slice(0, 1800), duration);
    measurePanel($('bubble'));
  }
  function streamCaption(text) {
    const wanted = String(text).slice(0, 2000);
    const displayed = $('speech').textContent;
    const previous = typedCaption?.target || ($('title').textContent==='Luma · Live' ? displayed : '');
    const continues = previous && wanted.startsWith(previous);
    caption('Luma · Live', reduced ? wanted : continues ? displayed : '', 300000);
    if (!reduced) typedCaption = {target:wanted, last:pet.time};
  }
  function syncEffects() {
    const canAnimate = !suspended() && !reduced && !renderingBlocked;
    // Preserve the graphics context across Off, tab hiding and transient stalls.
    // A final teardown alone releases it; reusing a deliberately lost context
    // made later cursor clicks appear to work without drawing anything.
    if (!canAnimate || cursor === 2) {trail?.suspend(); trailCanvas.hidden = true;}
    else if (!trail && trailCanvas.dataset.renderer !== 'unavailable') {
      try {trail = mountCrystalTrail(trailCanvas, {isSuspended: () => suspended() || reduced || renderingBlocked || cursor === 2, intensity: () => .72, glass: () => cursor === 0});}
      catch {trailCanvas.hidden = true; trailCanvas.dataset.renderer = 'unavailable'; /* A missing GPU must never make the guide unusable. */}
    } else trail?.resume();
    const snowActive = canAnimate && shell.dataset.theme === 'dark';
    pet.winterHatEnabled = shell.dataset.theme === 'dark';
    // Hat is a theme accessory, not a user-selectable emotion or weather knob.
    if(pet.image.complete&&pet.image.naturalWidth)pet.render();
    if (!snowActive) {snow?.suspend(); snowCanvas.hidden = true;}
    else if (!snow) {
      try {snow = mountWindSnow(snowCanvas, {isSuspended: () => suspended() || reduced || renderingBlocked || shell.dataset.theme !== 'dark', density: () => .08, wind: () => .14});}
      catch {snowCanvas.hidden = true;}
    } else snow.resume();
  }
  function syncLifecycle() {
    if (suspended()) {
      if (typedCaption) {$('speech').textContent = typedCaption.target; typedCaption = null;}
      finishDrag();
      pet.suspend(); stopNarration();
      if (voiceBusy) stopVoice();
      if (audio.enabled) audio.setEnabled(false).catch(() => {});
    } else {
      pet.resume();
      if (soundEnabled && !audio.enabled) audio.setEnabled(true).catch(() => {soundEnabled = false; updateSoundButton();});
    }
    syncEffects();
  }
  function updateSoundButton() {
    $('ambience').setAttribute('aria-pressed', String(soundEnabled));
    $('ambience').setAttribute('aria-label', soundEnabled ? 'Mute ambience and open controls' : 'Enable ambience');
  }
  function endTour(markSeen = true) {
    welcomeUntil = 0; tourIndex = -1; pet.guide = voiceBusy || controlsHeld; $('guide-actions').hidden = true; $('caption-meta').hidden = true;
    stopNarration(); if (!voiceBusy) {pet.setState('idle'); $('bubble').hidden = true;}
    if (markSeen) safeStore.set(`tour-seen-${surface}`, true);
  }
  function showStep(index) {
    if (destroyed || hidden) return;
    welcomeUntil = 0; stopVoice(); stopNarration();
    if (index >= steps.length) {endTour(); pet.play('wave'); caption('All yours', 'A little crystal company, whenever you need it.', 4500); return;}
    tourIndex = Math.max(0, index); pet.guide = true; pet.setState('idle');
    const step = steps[tourIndex]; pet.play(step.emotion === 'wave' ? 'wave' : 'shimmer');
    caption(step.title, step.text); $('guide-actions').hidden = false; $('back').hidden = tourIndex === 0;
    $('next').textContent = tourIndex === steps.length - 1 ? 'Finish' : 'Next'; $('skip').textContent = 'Skip tour';
    $('caption-meta').hidden = false; $('caption-meta').textContent = `${tourIndex + 1} of ${steps.length}`;
    measurePanel($('bubble'));
    speakCaption(`${step.title}. ${step.text}`);
  }
  function welcome() {
    if (destroyed || hidden) return;
    welcomeUntil = pet.time+4500; pet.guide = true;
    const firstVisit = safeStore.get(`tour-seen-${surface}`) !== 'true';
    caption('Hi, I’m Luma', firstVisit ? 'Your little crystal guide. Want a quick tour?' : 'Lovely to see you. I’ll be right here.', 4500);
    $('guide-actions').hidden = !firstVisit; $('back').hidden = true; $('next').textContent = 'Take a quick tour'; $('skip').textContent = 'Not now';
    measurePanel($('bubble'));
  }
  function stopVoice() {
    const wasActive = voiceBusy; voiceBusy = false; voiceSpeaking = false; micMuted = false;
    if (wasActive) voiceAdapter?.stop?.();
    shell.dataset.busy = 'false'; shell.dataset.voiceState='off'; $('voice').setAttribute('aria-pressed', 'false'); $('voice').setAttribute('aria-label', 'Talk with Luma'); $('voice').title='Talk with Luma';
    $('voice-actions').hidden = true; $('help-links').hidden = true; $('mic-mute').setAttribute('aria-pressed', 'false'); $('mic-mute').textContent = 'Mute microphone';
    pet.guide = tourIndex >= 0 || controlsHeld; pet.setState('idle'); audio.sync();
  }
  async function startVoice() {
    if (voiceBusy) {stopVoice(); pet.setState('idle'); return;}
    if (suspended()) {caption('Luma is paused', 'Resume Luma before starting a voice conversation.'); return;}
    introPending = false; endTour(false); stopNarration();
    if (!voiceAdapter?.start) {pet.setState('waiting'); caption('Voice is resting', 'Luma Live is not configured here yet. The caption tour and Settings help still work.'); announce('Luma Live is unavailable.'); return;}
    voiceBusy = true; pet.guide = true; pet.setState('loading'); shell.dataset.busy = 'true'; shell.dataset.voiceState='connecting';
    $('voice').setAttribute('aria-pressed', 'true'); $('voice').setAttribute('aria-label', 'Stop Luma conversation'); $('voice').title='Connecting… Click to stop'; $('voice-actions').hidden = false;
    caption('Connecting', surface === 'settings' ? 'Luma uses your Personal API key. Provider charges may apply; no Managed credits are used. Your microphone is used only for this Luma conversation.' : 'Your microphone is used only for this Luma conversation.');
    try {
      await voiceAdapter.start({
        onState(state) {
          if (destroyed || !voiceBusy) return;
          voiceSpeaking = state === 'speaking';
          shell.dataset.voiceState=['listening','thinking','speaking'].includes(state)?state:'connecting';
          $('voice').title=state==='listening'?'Luma is listening. Click to stop':state==='speaking'?'Luma is speaking. Click to stop':state==='thinking'?'Luma is thinking. Click to stop':'Connecting… Click to stop';
          const visual = {connecting:'loading', listening:'waiting', thinking:'loading', speaking:'review', idle:'idle'}[state] || 'idle';
          pet.setState(visual); pet.guide = true; $('bubble').hidden = false;
          $('title').textContent = state === 'listening' ? 'Live · Listening' : state === 'thinking' ? 'Live · Thinking' : state === 'speaking' ? 'Live · Speaking' : 'Connecting';
          $('help-links').hidden=!['listening','thinking','speaking'].includes(state);
          measurePanel($('bubble'));
          audio.sync();
        },
        onCaption(text, role) {if (destroyed || !voiceBusy) return; if(role==='assistant')streamCaption(text);else caption('You · Live', text);},
        onError(message) {if (destroyed) return; stopVoice(); pet.setState('error'); caption('Connection paused', String(message).slice(0, 350)); announce('Luma connection stopped.');},
        onStop() {if (destroyed || !voiceBusy) return; stopVoice(); pet.setState('idle'); caption('Voice stopped', 'The microphone is off. Start Voice whenever you want to chat.');},
      });
    } catch (error) {
      if (destroyed) return;
      stopVoice(); pet.setState('error'); caption('Voice is unavailable', error?.message || 'Please try again later.'); announce('Luma Live could not connect.');
    }
  }
  function setTheme(theme) {
    shell.dataset.theme = theme === 'dark' ? 'dark' : 'light';
    if (!manualMood) audio.setMood(shell.dataset.theme === 'dark' ? 'lobby' : 'dream');
    nextGust = pet.time + randomBetween(5000, 11000); syncEffects();
  }
  function hide() {
    hidden = true; shell.dataset.hidden = 'true'; safeStore.set(`hidden-${surface}`, true);
    endTour(false); stopVoice(); soundEnabled = false; updateSoundButton();
    shell.querySelector('.luma-content').hidden = true; $('reopen').hidden = false; syncLifecycle();
  }
  function show() {
    hidden = false; shell.dataset.hidden = 'false'; safeStore.set(`hidden-${surface}`, false);
    shell.querySelector('.luma-content').hidden = false; $('reopen').hidden = true; syncLifecycle();
  }

  listen($('next'), 'click', () => showStep(tourIndex + 1)); listen($('back'), 'click', () => showStep(tourIndex - 1));
  listen($('skip'), 'click', () => endTour()); listen($('tour'), 'click', () => {
    $('options-menu').hidden=true;$('sound-menu').hidden=true;$('options').setAttribute('aria-expanded','false');
    controlsHeld=false;show();showStep(0);
  });
  listen($('voice'), 'click', startVoice); listen($('voice-stop'), 'click', () => {stopVoice(); pet.setState('idle'); caption('Voice stopped', 'The microphone is off.');});
  listen($('mic-mute'), 'click', () => {micMuted = !micMuted; voiceAdapter?.setMuted?.(micMuted); $('mic-mute').setAttribute('aria-pressed', String(micMuted)); $('mic-mute').textContent = micMuted ? 'Unmute microphone' : 'Mute microphone';});
  listen($('cursor'), 'click', () => {
    cursor = (cursor + 1) % 3; const labels = ['iridescent glass', 'transparent water', 'normal'];
    $('cursor').setAttribute('aria-label', `Cursor: ${labels[cursor]}. Change to ${labels[(cursor + 1) % 3]}`); $('cursor').title = `Cursor: ${labels[cursor]}`;
    // Glass/water use the same original solver with a live shader uniform.
    // Preserve its context/flow between those modes; only Off tears it down.
    syncEffects();
    const unavailable=cursor!==2&&trailCanvas.dataset.renderer==='unavailable';
    if(unavailable)$('cursor').title='Cursor effect unavailable in this browser. Reload to retry.';
    announce(unavailable?'Cursor effect unavailable in this browser. The pet and guide still work.':`Cursor effect: ${labels[cursor]}.`);
  });
  listen($('ambience'), 'click', async () => {
    soundEnabled = !soundEnabled; $('sound-menu').hidden = false; updateSoundButton();
    measurePanel($('sound-menu')); try {await audio.setEnabled(soundEnabled && !suspended());} catch {soundEnabled = false; updateSoundButton(); caption('Sound is unavailable', 'Your browser could not start ambience. The guide is still available.');}
  });
  listen($('sound-close'), 'click', () => {$('sound-menu').hidden = true;if(keyboardFocus)$('ambience').focus();else{document.activeElement?.blur();releaseControls();}});
  listen($('mood'), 'change', () => {manualMood = $('mood').value !== 'auto'; audio.setMood(manualMood ? $('mood').value : shell.dataset.theme === 'dark' ? 'lobby' : 'dream');});
  listen($('volume'), 'input', () => audio.setVolume(Number($('volume').value) / 100));
  function showOptions() {controlsHeld=true;pet.move=null;pet.guide=true;$('sound-menu').hidden=true;$('options-menu').hidden=false;$('options').setAttribute('aria-expanded','true');measurePanel($('options-menu'));}
  listen($('options'), 'click',()=>{
    if($('options-menu').hidden)showOptions();
    else {$('options-menu').hidden=true;$('options').setAttribute('aria-expanded','false');releaseControls();}
  });
  listen($('body'),'contextmenu',event=>{event.preventDefault();showOptions();});
  listen($('body'),'keydown',event=>{if(event.key==='ArrowDown'){event.preventDefault();dock.focus();}if(event.key==='F10'&&event.shiftKey){event.preventDefault();showOptions();}});
  if($('surface-style')) {
    $('surface-style').value=surfaceStyle;
    listen($('surface-style'),'change',()=>{
      if(!surfaceStyles.has($('surface-style').value))return;
      surfaceStyle=$('surface-style').value;safeStore.set('surface-style',surfaceStyle);
      document.documentElement.dataset.lumaSurface=surfaceStyle;
      announce(`Dark surface: ${$('surface-style').selectedOptions[0].textContent}.`);
    });
  }
  $('wandering').checked=wandering;
  listen($('wandering'),'change',()=>{
    wandering=$('wandering').checked;safeStore.set('wandering',wandering);
    if(!wandering){
      if(pet.move&&!pet.move.layoutRetreat)pet.move=null;
      if(['peek','teleport','dance'].includes(pet.action?.name))pet.setState('idle');
    }
  });
  $('reduced').checked = reduced;
  listen($('reduced'), 'change', () => {motionOverride = String($('reduced').checked); safeStore.set('reduced', motionOverride); reduced = $('reduced').checked; pet.configure('reduced', reduced); syncEffects();});
  listen(motionPreference, 'change', () => {if (motionOverride !== null) return; reduced = motionPreference.matches; $('reduced').checked = reduced; pet.configure('reduced', reduced); syncEffects();});
  listen($('pause'), 'change', () => {paused = $('pause').checked; pet.configure('paused', paused); syncLifecycle();});
  listen($('narration'), 'change', () => {narration = $('narration').checked; if (!narration) stopNarration(); else if (tourIndex >= 0) speakCaption(`${steps[tourIndex].title}. ${steps[tourIndex].text}`);});
  listen($('mute-all'), 'click', () => {soundEnabled = false; narration = false; $('narration').checked = false; stopNarration(); if (voiceBusy) {micMuted = true; voiceAdapter?.setMuted?.(true); stopVoice();} audio.setEnabled(false).catch(() => {}); updateSoundButton(); announce('All Luma sound is muted.');});
  listen($('hide'), 'click', hide); listen($('reopen'), 'click', show);
  const holdControls = () => {controlsHeld=true;pet.move=null;pet.guide=true;};
  const releaseControls = () => {if(!$('sound-menu').hidden||!$('options-menu').hidden)return;controlsHeld=false;shell.dataset.controlsOpen='false';pet.guide=voiceBusy||tourIndex>=0||welcomeUntil>pet.time;};
  listen(dock,'pointerenter',holdControls);
  let keyboardFocus = false;
  listen(document,'keydown',()=>{keyboardFocus=true;});
  listen(document,'pointerdown',()=>{keyboardFocus=false;},{passive:true});
  listen(dock,'pointerleave',()=>{if(!keyboardFocus&&dock.contains(document.activeElement))document.activeElement.blur();if(!dock.contains(document.activeElement))releaseControls();});
  listen(dock,'focusin',holdControls);
  listen(dock,'focusout',event=>{if(!dock.contains(event.relatedTarget))releaseControls();});
  listen($('body'),'pointerdown',event=>{if(event.pointerType!=='mouse'){shell.dataset.controlsOpen=shell.dataset.controlsOpen==='true'?'false':'true';if(shell.dataset.controlsOpen==='true')holdControls();else releaseControls();}});
  function finishDrag() {
    if (!drag) return;
    if(drag.pending&&!suspended())walkDragTarget(drag.pending);
    const ownedDrag = drag; drag = null; shell.dataset.dragging = 'false';
    pet.manualPlacement=false;
    const destination=pet.move?.to||{x:pet.x,y:pet.y};
    if(!pet.safe(destination.x,destination.y)){
      const spots=[];
      for(let y=12;y<pet.h-pet.petH-44;y+=24)for(let x=8;x<pet.w-pet.options.size;x+=24)if(pet.safe(x,y))spots.push({x,y});
      spots.sort((a,b)=>Math.hypot(a.x-destination.x,a.y-destination.y)-Math.hypot(b.x-destination.x,b.y-destination.y));
      if(spots[0])pet.walkManual(spots[0]);else {pet.move=null;pet.relocate();}
    }
    try {if($('body').hasPointerCapture(ownedDrag.id))$('body').releasePointerCapture(ownedDrag.id);} catch {/* Already released. */}
    suppressDragClick = ownedDrag.moved;
    pet.guide = voiceBusy || controlsHeld || tourIndex>=0 || welcomeUntil>pet.time;
    nextIdle = pet.time + randomBetween(18000,42000);
  }
  function walkDragTarget(target) {
    pet.action=null;pet.state='idle';pet.actor.classList.remove('sleep');
    pet.walkManual(target);
  }
  listen($('body'),'pointerdown',event=>{
    if(event.button!==0||suspended()||pet.parked||pet.move?.layoutRetreat)return;
    drag={id:event.pointerId,offsetX:event.clientX-pet.x,offsetY:event.clientY-pet.y,startX:event.clientX,startY:event.clientY,moved:false,pending:null};
    suppressDragClick=false; pet.guide=true;pet.manualPlacement=true;
    try {$('body').setPointerCapture(event.pointerId);} catch {drag=null;pet.manualPlacement=false;return;}
  });
  listen($('body'),'pointermove',event=>{
    if(!drag||drag.id!==event.pointerId)return;
    if(Math.hypot(event.clientX-drag.startX,event.clientY-drag.startY)<5&&!drag.moved)return;
    drag.moved=true; shell.dataset.dragging='true';
    drag.pending={x:event.clientX-drag.offsetX,y:event.clientY-drag.offsetY};
  });
  listen($('body'),'pointerup',finishDrag);listen($('body'),'pointercancel',()=>{if(drag)drag.pending=null;pet.move=null;finishDrag();});
  listen($('body'),'lostpointercapture',finishDrag);
  listen($('body'),'click',event=>{if(suppressDragClick){event.preventDefault();event.stopImmediatePropagation();suppressDragClick=false;}},{capture:true});
  listen(document,'pointerdown',event=>{lastInteraction=pet.time;if(pet.state==='sleep')pet.play('wake');if(!shell.contains(event.target)){ $('sound-menu').hidden=true;$('options-menu').hidden=true;$('options').setAttribute('aria-expanded','false');releaseControls();}},{passive:true});
  listen(document,'pointermove',()=>{lastInteraction=pet.time;if(pet.state==='sleep')pet.play('wake');},{passive:true});
  listen(document,'keydown',()=>{lastInteraction=pet.time;if(pet.state==='sleep')pet.play('wake');});
  listen(shell, 'focusin', () => {lastInteraction = pet.time;});
  listen(shell, 'keydown', event => {if (event.key === 'Escape') {endTour(); stopVoice(); $('options-menu').hidden = true; $('sound-menu').hidden = true; $('options').setAttribute('aria-expanded', 'false');releaseControls();}});
  listen(document, 'visibilitychange', syncLifecycle);
  listen(window, 'luma:theme', event => setTheme(event.detail?.theme));
  // A viewport overlay is always in-view; only the document visibility owns it.
  const viewObserver = null;
  pet.onFrame = time => {
    if (suspended()) return;
    if(shell.dataset.entrance==='true'&&!pet.move&&!pet.action){shell.dataset.entrance='false';queueClearance();}
    if(typedCaption){
      const speech=$('speech'),length=nextCaptionLength(speech.textContent.length,typedCaption.target.length,time-typedCaption.last,reduced);
      if(length!==speech.textContent.length){speech.textContent=typedCaption.target.slice(0,length);measurePanel($('bubble'));}
      typedCaption.last=time;if(length>=typedCaption.target.length)typedCaption=null;
    }
    if(drag?.pending){
      const target=drag.pending;drag.pending=null;
      // User-directed movement crosses freely; release settles clear of content.
      walkDragTarget(target);
    }
    if(welcomeUntil && time>welcomeUntil && tourIndex<0 && !voiceBusy) endTour();
    positionPanels();
    if (introPending && !voiceBusy && !controlsHeld && tourIndex<0 && time>=pet.speechUntil && !pet.parked && !pet.move && !pet.action && shell.dataset.ready === 'true' && time >= (reduced ? 100 : 1100)) {
      introPending = false; pet.play('wave');
      if (!voiceBusy && tourIndex < 0) welcome();
    }
    const busy = voiceBusy || tourIndex >= 0 || pet.guide || narrationSpeaking;
    if (!busy && !pet.action && !pet.move && !['idle','sleep'].includes(pet.state) && time > pet.speechUntil) pet.setState('idle');
    if (!reduced && !busy && !pet.parked && !pet.action && !pet.move && pet.state === 'idle' && time >= nextIdle) {
      nextIdle = time + randomBetween(18000, 42000);
      if (time - lastInteraction > 180000) pet.play('sleep');
      else if (wandering && time >= nextDance) {nextDance = time + randomBetween(480000, 900000); pet.play('dance');}
      else if (wandering && time >= nextPeek) {nextPeek = time + randomBetween(240000, 480000); pet.play('peek');}
      else if (wandering && Math.random() < .72) pet.play('wander'); else pet.play(Math.random() < .65 ? 'wave' : 'jump');
    }
    if (snow && time >= nextGust) {nextGust = time + randomBetween(8000, 16000); snow.gust();}
    if (soundEnabled && !manualMood && time >= nextMood) {nextMood = time + randomBetween(180000, 360000); const moods = (shell.dataset.theme === 'dark' ? ['winter','lobby','deep'] : ['dream','winter']).filter(mood=>mood!==audio.mood); audio.setMood(moods[Math.floor(Math.random() * moods.length)]);}
    const dance = pet.action?.name === 'dance' ? pet.action : null; audio.dance(dance ? dance.start : null, dance ? time - dance.start : 0);
  };
  pet.onPerformance = blocked => {
    renderingBlocked = blocked;
    // Pause during a real stall, then resume existing resources after recovery.
    // One slow frame must not permanently disable cursor/snow for this page.
    syncEffects();
  };
  pet.ready.then(() => {
    if (destroyed) return;
    shell.dataset.ready = 'true';
    if (hidden) {hide(); return;}
    refreshClearance();
    const destination=pet.safe(pet.x,pet.y)?{x:pet.x,y:pet.y}:
      pet.move?.to&&pet.safe(pet.move.to.x,pet.move.to.y)?{...pet.move.to}:pet.safeSpot();
    if (!reduced && destination) {
      shell.dataset.entrance='true';
      pet.action=null;pet.move=null;pet.x=destination.x;pet.y=destination.y;
      pet.parked=false;pet.actor.style.visibility='';
      const entries=[{x:-pet.options.size,y:pet.y},{x:pet.w+pet.options.size,y:pet.y}];
      entries.sort((a,b)=>Math.abs(a.x-pet.x)-Math.abs(b.x-pet.x));
      const side=entries[0].x<0?'left':'right';
      const peeked=pet.peek(destination,side,true);
      const from=entries.find(p=>Array.from({length:24},(_,i)=>i/23).every(t=>pet.clearOfContent(p.x+(destination.x-p.x)*t,p.y)));
      if(!peeked){
        // A dense page can block a safe peek route. Still enter from the edge,
        // never expose the random home position as a middle-of-page spawn.
        const entry=from||entries[0];pet.x=entry.x;pet.y=entry.y;
        pet.move={from:entry,to:destination,start:pet.time,duration:1050,entering:true,manual:!from};
      }
      pet.render();
    }
    syncLifecycle();
  }).catch(() => {if(destroyed)return;shell.dataset.ready='false';announce('Luma artwork could not load.');});
  if (hidden) {shell.querySelector('.luma-content').hidden = true; $('reopen').hidden = false;}

  function destroy() {
    if (destroyed) return;
    finishDrag();typedCaption=null;destroyed = true; clearTimeout(layoutTimer);pageObserver.disconnect();panelObserver.disconnect();stopNarration(); stopVoice(); voiceAdapter?.dispose?.(); pet.destroy(); trail?.destroy(); snow?.destroy(); audio.destroy();
    viewObserver?.disconnect(); removers.forEach(remove => remove()); trailCanvas.remove(); snowCanvas.remove(); shell.remove(); delete root.dataset.lumaMounted;
    if (ownsRoot) root.remove();
  }
  listen(window, 'pagehide', destroy, {once: true});
  return {
    destroy, hide, show, tour: () => {show(); showStep(0);}, setTheme,
    updateAllowedState(state) {allowedState = state && typeof state === 'object' ? state : {};},
    react(state, message) {if (destroyed || hidden) return; if (['idle','loading','waiting','error','review','sleep'].includes(state)) pet.setState(state); else if (['wave','jump','celebrate'].includes(state)) pet.play(state); if (message) caption('Luma', String(message).slice(0, 900));},
    diagnostics() {return {surface, surfaceStyle, wandering, ready: shell.dataset.ready === 'true', performance:shell.dataset.performance, hidden, paused, reduced, cursor: ['glass','water','normal'][cursor], theme: shell.dataset.theme, voiceActive: voiceBusy, audioEnabled: soundEnabled, tourStep: tourIndex, animationActive: !!pet.raf, effectsActive: !trailCanvas.hidden || !snowCanvas.hidden, petBounds: {x:pet.x,y:pet.y,width:pet.options.size,height:pet.petH}, habitat: {width:pet.w,height:pet.h}};},
  };
}
