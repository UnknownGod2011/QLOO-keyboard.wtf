import { mountLuma } from './luma/luma-site.js';
import { createPublicLumaVoiceAdapter } from './luma-public-adapter.js';

// This module does not read forms, Firebase state, account data or local credentials.
const media = matchMedia('(prefers-color-scheme: dark)');
const nav = document.querySelector('.nav-links') || document.querySelector('.nav-inner');
let explicitTheme = false;
try { explicitTheme = ['light','dark'].includes(localStorage.getItem('keyboardwtf-theme')); } catch {}
const moon = document.createElement('button');
moon.className = 'theme-toggle';
moon.type = 'button';
moon.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M20.3 14.1A8.5 8.5 0 0 1 9.9 3.7a8.5 8.5 0 1 0 10.4 10.4Z"/></svg>';
nav?.append(moon);
let luma;
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;
  moon.setAttribute('aria-label', theme === 'dark' ? 'Use light theme' : 'Use dark theme');
  moon.setAttribute('aria-pressed', String(theme === 'dark'));
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#000000' : '#f4f5f2');
  window.dispatchEvent(new CustomEvent('luma:theme', {detail:{theme}}));
}
const onThemeClick = () => {
  explicitTheme = true;
  const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  try { localStorage.setItem('keyboardwtf-theme',theme); } catch {}
  applyTheme(theme);
};
const onSystemTheme = (event) => { if (!explicitTheme) applyTheme(event.matches ? 'dark' : 'light'); };
moon.addEventListener('click',onThemeClick);
media.addEventListener('change',onSystemTheme);
applyTheme(document.documentElement.dataset.theme || (media.matches ? 'dark' : 'light'));

// Pairing is intentionally quiet: no pet or microphone on the sensitive login page.
let petRoot;
const mountPet = () => {
  if (/\/desktop-login(?:\.html)?\/?$/.test(location.pathname) || luma) return;
  if (!petRoot) {
    petRoot = document.getElementById('luma-home-root');
    if (!petRoot) {
      petRoot = document.createElement('div');
      petRoot.className = 'luma-overlay-root';
      // Overlay only: Luma never inserts a column, spacer or section into the site.
      document.body.append(petRoot);
    }
  }
  luma = mountLuma({surface:'website',root:petRoot,voiceAdapter:createPublicLumaVoiceAdapter()});
};
mountPet();
const onMac = (event) => {
  if (!event.target.closest('[data-macos-coming-soon],#macDownload')) return;
  luma?.react('waiting','macOS is coming soon. Nothing will download yet.');
};
document.addEventListener('click',onMac);
const cleanup = () => {
  luma?.destroy();
  luma = undefined;
  document.removeEventListener('click',onMac);
  moon.removeEventListener('click',onThemeClick);
  media.removeEventListener('change',onSystemTheme);
};
window.addEventListener('pagehide',cleanup);
// Back/forward cache restores the page without evaluating its modules again.
// Recreate a quiet presentation, never a microphone/socket session.
window.addEventListener('pageshow',(event) => {
  if (!event.persisted) return;
  moon.addEventListener('click',onThemeClick);
  media.addEventListener('change',onSystemTheme);
  document.addEventListener('click',onMac);
  applyTheme(document.documentElement.dataset.theme || (media.matches ? 'dark' : 'light'));
  mountPet();
});
