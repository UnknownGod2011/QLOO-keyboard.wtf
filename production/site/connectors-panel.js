// Shared view only. Catalog, availability and account truth come from the
// authenticated gateway; no independent connector registry or token storage.
export class ConnectorPanel {
  constructor(root, transport) {
    this.root = root; this.transport = transport; this.snapshot = null;
    this.generation = 0; this.refreshId = 0; this.expanded = null; this.info = null;
    this.pending = new Map(); this.pollTimer = null; this.busy = new Set(); this.mutating = new Set();
    this.suspended = false; this.destroyed = false;
    this.root.classList.add("connector-panel");
    this.root.innerHTML = `<div class="connector-toolbar"><input class="connector-search" type="search" placeholder="Search connectors…" aria-label="Search connectors"><button class="connector-refresh" type="button" aria-label="Refresh connector state">↻</button></div><div class="connector-status" role="status" aria-live="polite">Loading connectors…</div><div class="connector-list" role="region" aria-label="Connector catalog" tabindex="0"></div>`;
    this.search = this.root.querySelector("input"); this.list = this.root.querySelector(".connector-list");
    this.status = this.root.querySelector(".connector-status");
    this.search.addEventListener("input", () => this.render());
    this.root.querySelector(".connector-refresh").addEventListener("click", () => this.focus());
    this.focus = async () => {
      if (this.destroyed || this.suspended || document.hidden || !this.root.getClientRects().length) return;
      try { await this.transport.refreshIdentity?.(); await this.refresh(); }
      catch (error) {
        if (!this.destroyed && !this.suspended) {
          this.reset(); this.status.textContent = error.message || "Unable to verify your current account.";
        }
      }
    };
    this.visibility = () => { if (document.hidden) this.stopPolling(); else { this.focus(); this.schedulePoll(); } };
    this.pagehide = event => event.persisted ? this.suspend() : this.destroy();
    this.pageshow = event => { if (event.persisted) this.resume(); };
    window.addEventListener("focus", this.focus); document.addEventListener("visibilitychange", this.visibility);
    window.addEventListener("pagehide", this.pagehide); window.addEventListener("pageshow", this.pageshow);
  }
  reset() {
    ++this.generation; ++this.refreshId; this.snapshot = null; this.pending.clear(); this.busy.clear(); this.mutating.clear();
    this.stopPolling(); this.list.replaceChildren(); this.expanded = null; this.info = null;
    this.status.textContent = "Sign in to manage your connected accounts.";
  }
  async refresh() {
    if (this.destroyed || this.suspended) return;
    const generation = this.generation, request = ++this.refreshId;
    try {
      const snapshot = await this.transport.load();
      if (generation !== this.generation || request !== this.refreshId) return;
      this.snapshot = snapshot;
      this.status.textContent = snapshot.notice || (snapshot.enabled ? "Only accounts you connect are accessible to Jarvis." : "Connectors are not enabled in this environment yet.");
      this.render();
    } catch (error) {
      if (generation === this.generation && request === this.refreshId) {
        this.status.textContent = error.message || "Unable to refresh connectors.";
        if (this.snapshot) { this.snapshot = { ...this.snapshot, accounts:this.snapshot.accounts.map(account=>({...account,state:"unavailable",verifiedAt:null})) }; this.render(); }
      }
    }
  }
  render() {
    if (!this.snapshot || this.destroyed) return;
    const query = this.search.value.trim().toLocaleLowerCase();
    const items = this.snapshot.catalog.filter(item => [item.name, item.id, ...item.searchTerms].join(" ").toLocaleLowerCase().includes(query));
    const active = this.list.contains(document.activeElement) ? document.activeElement : null;
    const focusKey = active?.dataset.connectorFocus, focusProvider = active?.dataset.connectorProvider;
    const scrollTop = this.list.scrollTop;
    this.list.replaceChildren();
    const accountsFor = item => this.snapshot.accounts.filter(account => account.family === item.family && account.state !== "disconnected");
    const groups = [
      ["Connected", items.filter(item => accountsFor(item).some(account => account.state === "connected"))],
      ["Available", items.filter(item => item.availability === "available" && !accountsFor(item).some(account => account.state === "connected"))],
      ["Coming soon", items.filter(item => item.availability !== "available")]
    ];
    for (const [title, entries] of groups) {
      if (!entries.length) continue;
      const heading = this.element("h3", "connector-group-label", title); this.list.append(heading);
      for (const item of entries) {
        const accounts = accountsFor(item), connected = accounts.some(value => value.state === "connected");
        const connecting = this.busy.has(item.family) || this.pending.has(item.family);
        const familyBusy = connecting || this.mutating.has(item.family);
        const row = this.element("div", "connector-row");
        const logo = this.element("img", "connector-logo"); logo.alt = "";
        // IDs are validated for local assets; never fetch a provider-supplied URL.
        if (/^[a-z][a-z0-9_-]{0,63}$/.test(item.icon)) logo.src = `${this.transport.assets || "./connector-icons/"}${item.icon}.svg`;
        const name = this.element("div", "connector-name", item.name);
        const badge = connecting ? "Connecting…" : this.mutating.has(item.family) ? "Updating…" : connected ? `${accounts.filter(value => value.state === "connected").length} connected` : accounts.some(a=>a.state==="disconnecting") ? "Disconnect pending" : accounts.some(a=>a.state==="unavailable") ? "Status unavailable" : accounts.length ? "Reconnect needed" : item.availability !== "available" ? "Coming soon" : "";
        if (badge) name.append(this.element("span", "connector-badge", badge));
        const control = this.button(connecting ? "◌" : connected ? "✓" : "+", async () => {
          if (accounts.length) { this.expanded = this.expanded === item.id ? null : item.id; this.render(); }
          else await this.connect(item);
        });
        control.classList.add("connector-symbol"); if (connected) control.classList.add("connected");
        if (connecting) control.classList.add("pending");
        control.dataset.connectorFocus = `control-${item.id}`; control.dataset.connectorProvider = item.id;
        control.setAttribute("aria-label", connected || accounts.length ? `Manage ${item.name} accounts` : `Connect ${item.name}`);
        control.setAttribute("aria-expanded", String(this.expanded === item.id));
        control.disabled = familyBusy;
        const info = this.button("ⓘ", () => {
          this.info = this.info === item.id ? null : item.id; this.render();
          this.list.querySelector(`#connector-info-${item.id}`)?.focus({preventScroll:true});
        });
        info.id = `connector-info-${item.id}`;
        info.dataset.connectorFocus = `info-${item.id}`; info.dataset.connectorProvider = item.id;
        info.classList.add("connector-info"); info.setAttribute("aria-label", `What ${item.name} can do`);
        info.setAttribute("aria-expanded", String(this.info === item.id));
        info.setAttribute("aria-controls", `connector-help-${item.id}`);
        row.append(logo, name, info, control); this.list.append(row);
        const help = this.element("div", "connector-help"); help.id = `connector-help-${item.id}`;
        help.hidden = this.info !== item.id;
        help.append(this.element("p", "connector-description", item.description));
        const abilities = Array.isArray(item.capabilities) ? item.capabilities.filter(value => typeof value === "string").slice(0, 12) : [];
        if (abilities.length) {
          const list = this.element("ul", "connector-capabilities");
          for (const ability of abilities) list.append(this.element("li", "", ability));
          help.append(list);
        }
        if (item.availability !== "available") help.append(this.element("p", "connector-description", "Coming soon in this environment."));
        this.list.append(help);
        info.title = [item.description, ...abilities].join("\n");
        info.addEventListener("keydown", event => {
          if (event.key === "Escape") { this.info = null; help.hidden = true; info.setAttribute("aria-expanded", "false"); }
        });
        if (this.expanded === item.id) {
          const manage = this.element("div", "connector-manage");
          const accountAction = (text, key, action) => {
            const button = this.button(text, action); button.disabled = familyBusy;
            button.dataset.connectorFocus = `${item.id}-${key}`; button.dataset.connectorProvider = item.id;
            return button;
          };
          manage.append(this.element("p", "connector-description", item.description));
          const shared = this.snapshot.catalog.filter(value => value.family === item.family);
          if (item.family === "google" && shared.length > 1)
            manage.append(this.element("p", "connector-description connector-family-note", `This Google account is shared by ${shared.map(value => value.name).join(", ")}. Default and disconnect changes apply to all of them.`));
          for (const account of accounts) {
            const line = this.element("div", "connector-account");
            const identity = this.element("span", "connector-account-identity", account.label);
            identity.append(this.element("span", "connector-account-state", `${account.state === "disconnecting" ? "Disconnect pending" : account.state.replaceAll("_", " ")}${account.isDefault ? " · Default" : ""}`));
            line.append(identity);
            if (account.state === "unavailable") line.append(accountAction("Refresh", `refresh-${account.accountId}`, () => this.refresh()));
            else if (account.state !== "connected" && account.state !== "disconnecting") line.append(accountAction("Reconnect", `reconnect-${account.accountId}`, () => this.connect(item)));
            if (!account.isDefault && account.state === "connected") line.append(accountAction("Use by default", `default-${account.accountId}`, () => this.mutate(item.family, () => this.transport.setDefault(account.accountId))));
            line.append(accountAction(account.state === "disconnecting" ? "Retry disconnect" : "Disconnect", `disconnect-${account.accountId}`, () => this.mutate(item.family, () => this.transport.disconnect(account.accountId))));
            manage.append(line);
          }
          manage.append(accountAction("+ Add account", "add", () => this.connect(item))); this.list.append(manage);
        }
      }
    }
    if (!items.length) this.list.append(this.element("p", "connector-empty", "No matching connectors."));
    if (focusKey) {
      const controls = [...this.list.querySelectorAll("button[data-connector-focus]")];
      const replacement = controls.find(node => node.dataset.connectorFocus === focusKey && !node.disabled)
        || controls.find(node => node.dataset.connectorProvider === focusProvider && node.classList.contains("connector-info"));
      replacement?.focus({ preventScroll:true });
    }
    this.list.scrollTop = scrollTop;
  }
  async connect(item) {
    if (this.destroyed || this.suspended) return;
    if (item.availability !== "available") { this.status.textContent = `${item.name} is coming soon.`; return; }
    if (this.busy.has(item.family) || this.pending.has(item.family) || this.mutating.has(item.family)) return;
    const generation = this.generation; this.busy.add(item.family); this.render();
    try {
      const requestId = crypto.randomUUID().replaceAll("-", "");
      const result = await this.transport.connect(item.id, requestId);
      if (generation !== this.generation) return;
      if (result.state === "connecting" || result.state === "creating" || result.state === "outcome_unknown") {
        this.pending.set(item.family, result); this.status.textContent = "Finish the provider sign-in in your browser.";
        this.schedulePoll();
        if (result.redirectUrl) await this.transport.open(result);
      } else if (result.state === "connected") { this.status.textContent = "Connected ✓"; await this.refresh(); }
      else this.status.textContent = "Sign-in did not complete. No connected account has been confirmed.";
    } catch (error) { if (generation === this.generation) this.status.textContent = error.message || "Unable to begin sign-in."; }
    finally { if (generation === this.generation) { this.busy.delete(item.family); this.render(); } }
  }
  async mutate(family, action) {
    if (this.destroyed || this.suspended) return;
    if (this.busy.has(family) || this.pending.has(family) || this.mutating.has(family)) return;
    const generation = this.generation;
    this.mutating.add(family); this.render();
    try { await action(); if (generation === this.generation) await this.refresh(); }
    catch (error) {
      if (generation === this.generation) {
        // A lost mutation response is not proof that the account stayed
        // connected. Remove that stale claim until the gateway verifies it.
        if (this.snapshot) {
          this.snapshot = { ...this.snapshot, accounts:this.snapshot.accounts.map(account => account.family === family
            ? { ...account, state:"unavailable", verifiedAt:null } : account) };
          this.render();
        }
        await this.refresh();
        if (generation === this.generation) this.status.textContent = error.message || "Unable to verify the account action. Refresh to check its status.";
      }
    }
    finally { if (generation === this.generation) { this.mutating.delete(family); this.render(); } }
  }
  schedulePoll() {
    this.stopPolling(); if (!this.pending.size || document.hidden || this.suspended || this.destroyed) return;
    this.pollTimer = setTimeout(async () => {
      const generation = this.generation;
      for (const [family, attempt] of this.pending) {
        if (Date.parse(attempt.expiresAt) <= Date.now()) { this.pending.delete(family); this.status.textContent = "Sign-in expired. You can start again."; continue; }
        try {
          const current = await this.transport.attempt(attempt.attemptId);
          if (generation !== this.generation) return;
          if (!["creating", "connecting", "outcome_unknown"].includes(current.state)) {
            this.pending.delete(family);
            this.status.textContent = current.state === "connected" ? "Connected ✓" : "Sign-in did not complete.";
            this.transport.completedAttempt?.(current, attempt.attemptId);
            await this.refresh();
          }
        } catch (error) { if (generation !== this.generation) return; this.status.textContent = error.message || "Unable to verify sign-in."; }
      }
      if (generation !== this.generation) return;
      this.render(); this.schedulePoll();
    }, 4000);
  }
  stopPolling() { clearTimeout(this.pollTimer); this.pollTimer = null; }
  suspend() {
    if (this.destroyed) return;
    this.suspended = true; ++this.generation; ++this.refreshId; this.busy.clear(); this.mutating.clear(); this.stopPolling();
  }
  async resume() {
    if (this.destroyed) return;
    // A cached page is not fresh account truth. Revalidate identity before using
    // its pending attempt IDs, and remove stale green checks while refreshing.
    try { await this.transport.refreshIdentity?.(); }
    catch (error) { this.reset(); this.suspended = false; this.status.textContent = error.message || "Unable to verify your current account."; return; }
    this.suspended = false;
    if (this.snapshot) { this.snapshot = { ...this.snapshot, accounts:this.snapshot.accounts.map(account=>({...account,state:"unavailable",verifiedAt:null})) }; this.render(); }
    await this.refresh(); this.schedulePoll();
  }
  destroy() {
    if (this.destroyed) return;
    this.reset(); this.destroyed = true;
    window.removeEventListener("focus", this.focus); document.removeEventListener("visibilitychange", this.visibility);
    window.removeEventListener("pagehide", this.pagehide); window.removeEventListener("pageshow", this.pageshow);
  }
  element(tag, className, text = "") { const node = document.createElement(tag); node.className = className; node.textContent = text; return node; }
  button(text, action) { const button = this.element("button", "connector-action", text); button.type = "button"; button.addEventListener("click", action); return button; }
}
