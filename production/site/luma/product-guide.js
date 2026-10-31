// Maintained product facts: production/site/{index,pricing,support}.html and
// docs/production/EARLY_LAUNCH_REMAINING_ACTIONS.md. No account or credential data.
export const PRODUCT_FACTS = Object.freeze({
  product: 'keyboard.wtf',
  platform: 'Windows 10 and 11 x64',
  macOS: 'Coming soon; there is no Mac download.',
  helper: 'Chrome Web Store Helper 2.1.28 connects supported Chrome page and browser tasks.',
  features: 'Dictation, writing help, selected-text questions and translations, screen understanding, app and website opening, and supported browser automation.',
  personal: 'Personal API requires the user’s own compatible API key and must not fall back to Managed charges.',
  managed: 'Managed usage uses the signed-in account’s allowance. Account & Billing shows the current server-confirmed plan and usage.',
  pricing: 'Verified 7 October 2026: the catalog lists Jarvis Monthly at ₹999/month with 500 credits. Check Pricing for current checkout availability and offers; this is not unlimited usage.',
  identity: 'Rename Jarvis in Settings > Assistant > Assistant name; tone and voice controls are there too. This changes the desktop assistant, not Luma.',
  installation: 'One normal Windows installer configures the native host. Install the separate Chrome Web Store Helper when browser interaction is needed.',
  warnings: 'The early Windows installer is unsigned and may be unrecognized. Verify the official source. Browser Keep/Keep anyway and SmartScreen More info → Run anyway are different prompts and may not be available. Never bypass a malware warning or disable protection.',
  updates: 'Early-release updates use the normal installer; unattended installation is disabled.',
  support: 'Use Support and share the app version, time and visible error. Never send API keys or private selected text.',
});

const websiteSteps = [
  ['Speak naturally', 'Dictate into your work, ask a question, or get help refining rough writing. Jarvis can also work with text you explicitly select.', 'wave'],
  ['Understand and act', 'Ask about your screen, open an app or website, or request supported browser work. Sensitive steps such as payments, passwords and one-time codes still need you.', 'review'],
  ['Start with Windows', 'Use the official Windows installer. macOS is coming soon. This early installer is unsigned: check the source and warning before deciding to continue.', 'waiting'],
  ['Connect Chrome when needed', 'Chrome Helper 2.1.28 is available in the Chrome Web Store. The installer configures its native host; no developer folders or registry edits are needed.', 'review'],
  ['Choose your usage', 'In Settings choose Managed usage for your account allowance, or Personal API for your own compatible key. Account & Billing has the current plan and usage; pricing is shown on the Pricing page.', 'wave'],
  ['You are in control', 'Use the active shortcuts shown in Settings. You can stop Jarvis and cancel tasks. Support can help with a version and error message—never send keys or private text.', 'wave'],
];

const settingsSteps = [
  ['A little help, right here', 'I’m Luma. I can explain Settings without changing your account, keys, shortcuts or saved actions.', 'wave'],
  ['Start with the active shortcuts', 'The active shortcuts shown in Settings are the ones to use. If another app has a preferred shortcut, the active fallback can differ.', 'review'],
  ['Choose your usage', 'Personal API uses your own compatible key. Managed usage uses the signed-in account’s allowance. Switching belongs to the existing Settings controls, not this tour.', 'waiting'],
  ['Give Chrome a hand', 'For supported Chrome page and browser tasks use the published Chrome Helper 2.1.28 and Check connection. Simple website opening can use your default browser.', 'review'],
  ['Keep private things private', 'Luma receives only explicitly allowed help state. API keys, credentials, selected text, account records and files are not tour context.', 'wave'],
];

export function getTourSteps(surface) {
  return (surface === 'settings' ? settingsSteps : websiteSteps).map(([title, text, emotion]) => ({title, text, emotion}));
}

export function buildAllowedGuideContext(surface, state = {}) {
  const section = typeof state.section === 'string' ? state.section.slice(0, 40) : 'overview';
  return {surface: surface === 'settings' ? 'settings' : 'website', section};
}
