import {mountLuma} from './luma-site.js';
import {createLumaVoiceAdapter} from './luma-live.js';

// This adapter never reads currentSettings, form values, account records, keys,
// saved actions, clipboard data or arbitrary page text. Its context endpoint is
// an explicit server-side allowlist protected by the existing Settings session.
const habitat = document.getElementById('luma-settings-root');
const themeButton = document.getElementById('luma-theme-toggle');
const themeKey = 'keyboardwtf-theme';
const sectionNames = new Set(['getting-started', 'voice-capture', 'account-billing', 'hotkeys', 'assistant', 'browser-automation', 'how-to-use', 'support-maintenance', 'set-api-key', 'speech-models', 'tts', 'voice-notes', 'ai', 'destinations', 'roadmap']);
let controller;
let disposed = false;
let safeState = {surface: 'settings', section: 'settings', activeHotkeys: {}, capabilities: []};
let contextPromise;
let contextController;

function currentSection() {
  const candidate = location.hash.slice(1);
  return sectionNames.has(candidate) ? candidate : 'settings';
}

async function refreshContext(signal) {
  if (disposed || document.hidden) return null;
  // Concurrent hover/start/route reads share one bounded in-flight fetch.
  if (contextPromise) return contextPromise;
  contextController = new AbortController();
  const abort = () => contextController?.abort();
  signal?.addEventListener('abort', abort, {once: true});
  if (signal?.aborted) contextController.abort();
  const timeout = setTimeout(abort, 3000);
  contextPromise = (async () => {
    try {
      const response = await fetch(`/api/luma/context?section=${encodeURIComponent(currentSection())}`, {signal: contextController.signal, credentials: 'same-origin', cache: 'no-store'});
      if (!response.ok) return null;
      const state = await response.json();
      if (disposed || state?.ok !== true || state.surface !== 'settings') return null;
      // Server fields are still projected, not forwarded wholesale to Live.
      const next = {
        surface: 'settings',
        section: sectionNames.has(state.section) ? state.section : 'settings',
        activeHotkeys: Object.fromEntries(Object.entries(state.activeHotkeys || {}).filter(([name, value]) => ['SmartWriting', 'Dictation', 'Jarvis', 'Cancel', 'OpenSettings'].includes(name) && typeof value === 'string' && value.length <= 48)),
        capabilities: Array.isArray(state.capabilities) ? state.capabilities.filter(value => typeof value === 'string').slice(0, 4) : [],
      };
      safeState = next;
      controller?.updateAllowedState?.(next);
      // The busy bit is a local concurrency guard, not model context.
      return {nativeVoiceBusy: state.nativeVoiceBusy};
    } catch {
      return null;
    } finally {
      clearTimeout(timeout);
      signal?.removeEventListener('abort', abort);
      contextController = null;
      contextPromise = null;
    }
  })();
  return contextPromise;
}

function applyTheme(theme, persist = false) {
  const next = theme === 'dark' ? 'dark' : 'light';
  document.documentElement.dataset.theme = next;
  document.documentElement.style.colorScheme = next;
  if (persist) {
    try { localStorage.setItem(themeKey, next); } catch { /* Private/disabled storage stays usable. */ }
  }
  const target = next === 'dark' ? 'light' : 'dark';
  if (themeButton) {
    themeButton.setAttribute('aria-label', `Switch to ${target} theme`);
    themeButton.setAttribute('title', `Switch to ${target} theme`);
    themeButton.setAttribute('aria-pressed', String(next === 'dark'));
  }
  controller?.setTheme(next);
  document.dispatchEvent(new CustomEvent('luma:theme', {detail: {theme: next}}));
}

const onThemeClick = () => applyTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark', true);
const onRoute = () => { void refreshContext(); };
const systemTheme = matchMedia('(prefers-color-scheme: dark)');
const onSystemTheme = event => {
  let stored;
  try { stored = localStorage.getItem(themeKey); } catch { /* Use system preference. */ }
  if (stored !== 'light' && stored !== 'dark') applyTheme(event.matches ? 'dark' : 'light');
};

async function mount() {
  if (!habitat || disposed) return;
  await refreshContext();
  if (disposed) return;
  const voiceAdapter = createLumaVoiceAdapter({
    canStart: async ({signal} = {}) => {
      const state = await refreshContext(signal);
      // Fail closed if the native voice guard is not wired/configured. Do not
      // compete with Jarvis, Dictation or Smart Writing for the microphone.
      return state?.nativeVoiceBusy === false;
    },
    requestToken: async ({signal} = {}) => {
      const response = await fetch('/api/luma/live-token', {
        method: 'POST', credentials: 'same-origin', signal,
        headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({section: currentSection()}),
      });
      let grant;
      try { grant = await response.json(); } catch { /* Never expose provider/server response text. */ }
      if (!response.ok || !grant?.token) throw new Error(grant?.message || 'Luma voice is not configured. The tour and Settings help are still available.');
      return grant;
    },
  });
  controller = mountLuma({surface: 'settings', root: habitat, voiceAdapter, allowedState: () => safeState});
  applyTheme(document.documentElement.dataset.theme);
  themeButton?.addEventListener('click', onThemeClick);
  window.addEventListener('hashchange', onRoute);
  systemTheme.addEventListener('change', onSystemTheme);
}

function destroy() {
  if (disposed) return;
  disposed = true;
  contextController?.abort();
  controller?.destroy();
  themeButton?.removeEventListener('click', onThemeClick);
  window.removeEventListener('hashchange', onRoute);
  systemTheme.removeEventListener('change', onSystemTheme);
}

window.addEventListener('pagehide', destroy);
window.addEventListener('pageshow', event => {
  if (!event.persisted || !disposed) return;
  // Back/forward restores the old DOM, not the old microphone/socket owner.
  // Mount a fresh presentation only; Live always needs another explicit click.
  disposed = false;
  controller = null;
  void mount();
});
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => { void mount(); }, {once: true});
else void mount();
