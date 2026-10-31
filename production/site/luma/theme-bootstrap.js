/* Runs before styles paint. This stores a theme preference, never account data. */
(() => {
  let choice;
  try { choice = localStorage.getItem('keyboardwtf-theme'); } catch { /* Storage can be disabled. */ }
  const theme = choice === 'dark' || choice === 'light'
    ? choice : (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;
})();
