export const runtimeConfig = Object.freeze({
  // QLOO connector web configuration is supplied by its deployment process.
  // Do not inherit keyboard.wtf endpoints or Firebase project identifiers.
  canonicalWebsite: "",
  legacyWebsite: "",
  backendBaseUrl: "",
  downloads: Object.freeze({
    windows: Object.freeze({
      available: false,
      version: "",
      url: "",
      sha256: "",
    }),
    macos: Object.freeze({
      available: false,
      version: "",
      url: "",
      sha256: "",
      comingSoonMessage: "keyboard.wtf for macOS is coming soon. The Windows download is not compatible with macOS.",
    }),
  }),
  pricingDisplay: Object.freeze({
    asOf: "2026-08-16",
    baseAmountMinor: 99900,
    baseCurrency: "INR",
    approximateEquivalents: Object.freeze([
      "$10.50 USD",
      "€9.10 EUR",
      "£7.80 GBP",
      "CHF 8.50",
      "AED 38.50",
    ]),
  }),
  firebase: {
    apiKey: "",
    authDomain: "",
    projectId: "",
    storageBucket: "",
    messagingSenderId: "",
    appId: "",
  },
});
