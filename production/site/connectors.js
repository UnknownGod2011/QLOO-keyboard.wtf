import { api, auth, listenForAuth, googleSignIn, googleSignOut } from "./app.js";
import { ConnectorPanel } from "./connectors-panel.js";

const query = new URLSearchParams(location.search);
const attemptId = query.get("attempt"), callbackState = query.get("state");
// One bounded, session-only navigation marker. It contains no OAuth state,
// credential or service content, and restores the desktop return hint without
// trusting callback query parameters as proof that a grant was connected.
const returnKey = "keyboard.wtf.connector-return";
function readReturn() {
  try {
    const value = JSON.parse(sessionStorage.getItem(returnKey) || "null");
    if (value && /^[a-f0-9]{32}$/.test(value.attemptId) && ["desktop", "website"].includes(value.source)
        && (value.owner === "" || /^[a-f0-9]{64}$/.test(value.owner)) && typeof value.opened === "boolean"
        && Number.isFinite(value.expiresAt) && value.expiresAt > Date.now() && value.expiresAt <= Date.now() + 600000)
      return value;
    sessionStorage.removeItem(returnKey);
  } catch { /* Storage may be unavailable; server ownership remains authoritative. */ }
  return null;
}
function writeReturn(value) { try { sessionStorage.setItem(returnKey, JSON.stringify(value)); } catch { } }
function clearReturn(id) { try { if (readReturn()?.attemptId === id) sessionStorage.removeItem(returnKey); } catch { } }
const savedReturn = readReturn(), matchingReturn = savedReturn?.attemptId === attemptId ? savedReturn : null;
const fromDesktop = query.get("source") === "desktop" || matchingReturn?.source === "desktop";
const expectedOwner = query.get("owner") || matchingReturn?.owner || "";
if (fromDesktop && /^[a-f0-9]{32}$/.test(attemptId || "") && /^[a-f0-9]{64}$/.test(expectedOwner))
  writeReturn({ attemptId, source: "desktop", owner: expectedOwner, opened: matchingReturn?.opened || false, expiresAt: matchingReturn?.expiresAt || Date.now() + 600000 });
// Remove OAuth state/provider query parameters from visible history immediately.
if (location.search) history.replaceState(null, "", location.pathname);
const identity = document.getElementById("connector-identity"), signin = document.getElementById("connector-signin"), signout = document.getElementById("connector-signout");
let generation = 0, handledAttempt = false, identityUid = null, authorizedUid = "";
const call = async (path, method = "GET", body) => {
  if (!authorizedUid || auth.currentUser?.uid !== authorizedUid)
    throw new Error("Sign in with the same keyboard.wtf account to manage this connection.");
  try { return await api(`/v1/connectors${path}`, { authenticated: true, method, ...(body ? { body: JSON.stringify(body) } : {}) }); }
  catch (error) {
    // The existing product transport intentionally stays untouched. Its
    // problem+json fallback is text; present only the safe connector detail.
    try {
      const problem = JSON.parse(error.message);
      if (typeof problem.detail === "string") error.message = problem.detail.slice(0, 500);
    } catch { /* Already an ordinary safe transport message. */ }
    throw error;
  }
};
const authorizationHosts = new Set(["backend.composio.dev", "connect.composio.dev", "accounts.google.com", "github.com", "slack.com", "discord.com"]);
function openAuthorization(result) {
  const url = new URL(result.redirectUrl);
  if (url.protocol !== "https:" || url.username || url.password || !authorizationHosts.has(url.hostname)) throw new Error("This sign-in destination is unavailable.");
  if (!/^[a-f0-9]{32}$/.test(result.attemptId || "")) throw new Error("This sign-in attempt is unavailable.");
  const expiry = Date.parse(result.expiresAt);
  writeReturn({ attemptId: result.attemptId, source: fromDesktop ? "desktop" : "website", owner: fromDesktop ? expectedOwner : "",
    opened: true, expiresAt: Number.isFinite(expiry) ? Math.min(expiry, Date.now() + 600000) : Date.now() + 600000 });
  location.assign(url.href);
}
function completedAttempt(result, id) {
  if (result.state === "connected" && fromDesktop && id === attemptId)
    document.getElementById("connector-return").hidden = false;
  clearReturn(id);
}
async function synchronizeIdentity(user = auth.currentUser) {
  const uid = user?.uid || "";
  if (uid !== identityUid) {
    identityUid = uid; authorizedUid = ""; ++generation; panel.reset();
    document.getElementById("connector-return").hidden = true;
  }
  const current = generation;
  signin.hidden = !!user; signout.hidden = !user;
  identity.textContent = user ? user.email || "Signed in" : "Sign in with your keyboard.wtf account.";
  if (!user) return false;
  if (expectedOwner) {
    const digest = [...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(user.uid)))].map(value => value.toString(16).padStart(2, "0")).join("");
    if (current !== generation) return false;
    if (digest !== expectedOwner) { authorizedUid = ""; panel.status.textContent = "This browser is signed into a different account than keyboard.wtf. Sign out here and choose the same account."; return false; }
  }
  if (current !== generation) return false;
  authorizedUid = uid;
  return true;
}
const panel = new ConnectorPanel(document.getElementById("connector-panel"), {
  load: () => call("/"), connect: (connectorId, requestId) => call("/connect", "POST", { connectorId, requestId }),
  attempt: id => call(`/attempts/${encodeURIComponent(id)}`),
  disconnect: id => call(`/accounts/${encodeURIComponent(id)}`, "DELETE"),
  setDefault: id => call(`/accounts/${encodeURIComponent(id)}/default`, "POST"), open: openAuthorization, completedAttempt,
  refreshIdentity: async () => {
    if (!await synchronizeIdentity()) throw new Error(expectedOwner && auth.currentUser
      ? "Choose the same account that is signed into keyboard.wtf." : "Sign in to manage your connected accounts.");
  }
});
signin.addEventListener("click", async () => { try { await googleSignIn(); } catch { panel.status.textContent = "Sign-in did not finish. Try again when ready."; } });
signout.addEventListener("click", () => googleSignOut());
listenForAuth(async user => {
  const authorized = await synchronizeIdentity(user);
  if ((user?.uid || "") !== identityUid) return;
  const current = generation;
  if (!user) {
    try { const catalog = await api("/v1/connectors/catalog"); if (current === generation) { panel.snapshot = { enabled: true, catalog, accounts: [] }; panel.render(); } }
    catch { if (current === generation) panel.status.textContent = "The connector catalog is temporarily unavailable."; }
    return;
  }
  if (!authorized) return;
  await panel.refresh();
  if (attemptId && /^[a-f0-9]{32}$/.test(attemptId) && !handledAttempt && current === generation) {
    try {
      const result = callbackState
        ? await call(`/attempts/${attemptId}/complete`, "POST", { state: callbackState })
        : await call(`/attempts/${attemptId}`);
      if (current !== generation) return;
      handledAttempt = true;
      if (result.state === "connected") {
        panel.status.textContent = "Connected ✓"; document.getElementById("connector-return").hidden = !fromDesktop;
        completedAttempt(result, attemptId); await panel.refresh();
      } else if (!callbackState && result.redirectUrl && (readReturn()?.attemptId !== attemptId || !readReturn()?.opened)) openAuthorization(result);
      else if (["creating", "connecting", "outcome_unknown"].includes(result.state)) {
        panel.pending.set(`return-${attemptId}`, result); panel.status.textContent = "Waiting to verify provider sign-in…"; panel.schedulePoll();
      } else { clearReturn(attemptId); panel.status.textContent = "Sign-in did not complete. You can connect again when ready."; }
    } catch (error) { if (current === generation) panel.status.textContent = error.message || "Unable to verify this sign-in return."; }
  }
});
