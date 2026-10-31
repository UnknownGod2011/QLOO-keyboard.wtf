import { ConnectorPanel } from "/connectors/connectors-panel.js";
const root = document.getElementById("settings-connectors-panel");
const identityNode = document.getElementById("paidAccountIdentity");
let identity = null;
const resetForIdentity = (refresh = true) => {
  // This opaque epoch comes from the existing credential owner. Human display
  // names can be identical across accounts and are never an ownership signal.
  const current = identityNode?.dataset.connectorIdentity || "";
  if (current !== identity) { identity = current; panel.reset(); if (refresh !== false && root.getClientRects().length) panel.refresh(); }
};
async function refreshIdentity() {
  const response = await fetch("/api/settings", { cache: "no-store" });
  if (!response.ok) throw new Error("Unable to verify your current desktop account. Refresh Settings.");
  const settings = await response.json();
  const current = settings.connectorIdentity;
  if (typeof current !== "string" || current && !/^[a-f0-9]{64}$/.test(current))
    throw new Error("Your desktop account identity is unavailable. Refresh Settings.");
  if (identityNode) identityNode.dataset.connectorIdentity = current;
  resetForIdentity(false);
}
async function call(path = "", method = "GET", body) {
  const response = await fetch(`/api/connectors${path}`, { method, headers: { "Content-Type": "application/json" },
    ...(body ? { body: JSON.stringify(body) } : {}), cache: "no-store" });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || data.detail || "Connector request failed.");
  return data;
}
const panel = new ConnectorPanel(root, {
  assets: "/connectors/icons/", load: () => call(), refreshIdentity,
  connect: (connectorId, requestId) => call("/connect", "POST", { connectorId, requestId }),
  attempt: id => call(`/attempts/${encodeURIComponent(id)}`),
  disconnect: id => call(`/accounts/${encodeURIComponent(id)}`, "DELETE"),
  setDefault: id => call(`/accounts/${encodeURIComponent(id)}/default`, "POST"),
  open: () => {} // Native service opens the default browser after backend claim.
});
const observer = new MutationObserver(() => resetForIdentity());
if (identityNode) observer.observe(identityNode, { attributes: true, attributeFilter: ["data-connector-identity"] });
const route = () => { if (location.hash === "#connectors") panel.focus(); };
// Existing Settings scroll navigation uses pushState, which emits no hashchange.
// Observe only its connector link; do not replace history or the Settings router.
const sidebarLink = document.querySelector('#sidebar-nav a[href="#connectors"]');
const navigate = () => queueMicrotask(route);
sidebarLink?.addEventListener("click", navigate);
window.addEventListener("hashchange", route);
resetForIdentity(false);
route();
window.addEventListener("pagehide", event => {
  if (!event.persisted) {
    observer.disconnect(); window.removeEventListener("hashchange", route);
    sidebarLink?.removeEventListener("click", navigate);
  }
});
