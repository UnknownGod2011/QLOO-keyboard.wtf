const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const event = () => {
  const listeners = [];
  return { addListener(fn) { listeners.push(fn); }, emit(...args) { listeners.forEach(fn => fn(...args)); } };
};
const flush = async () => { for (let index = 0; index < 30; index++) await Promise.resolve(); };
async function until(predicate) {
  for (let index = 0; index < 1000 && !predicate(); index++) await Promise.resolve();
  assert.ok(predicate(), "expected protocol event did not arrive");
}

function harness(workerSource = fs.readFileSync(path.join(__dirname, "..", "service-worker.js"), "utf8"), commandWaitSupported = false) {
  const messages = [];
  const timers = new Map();
  let nextTimer = 1;
  let clockMs = 0;
  let receive;
  let rendererMessage;
  let resolveStatus;
  let resolvePrepare;
  let command = null;
  let nativeObserver;
  let heldPoll = null;
  const cancelled = new Set();
  let stallStatus = false;
  const pageMessages = [];
  const port = {
    onMessage: { addListener(fn) { receive = fn; } }, onDisconnect: event(), disconnect() {},
    postMessage(message) {
      messages.push(message);
      nativeObserver?.(message);
      const permitted = message.helper?.extensionId === "gojdebdikepkpapmnggeljneamjdigbh"
        && message.helper?.serviceWorkerVersion === "2.1.21"
        && message.helper?.commandAuthoritySupported === true;
      const reply = { ok: true, commandWaitSupported, command: message.type === "poll" && permitted ? command : null,
        commandAuthorized: permitted && !cancelled.has(message.commandId) };
      if (reply.command) command = null;
      if (message.type === "poll" && permitted && !reply.command && commandWaitSupported && message.waitForCommand) {
        assert.equal(heldPoll, null, "there must be only one pending native command pull");
        const timer = context.setTimeout(() => { heldPoll = null; receive(reply); }, 900);
        heldPoll = () => {
          context.clearTimeout(timer);
          heldPoll = null;
          reply.command = command;
          command = null;
          queueMicrotask(() => receive(reply));
        };
        return;
      }
      queueMicrotask(() => receive(reply));
    },
  };
  const context = vm.createContext({
    console, URL, Date: { now: () => clockMs }, performance: { now: () => clockMs },
    crypto: { randomUUID: () => "lifecycle-test" },
    setTimeout(fn, delay) { const id = nextTimer++; timers.set(id, { fn, delay, due: clockMs + delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    chrome: {
      action: { setBadgeText() {}, setBadgeBackgroundColor() {} },
      alarms: { create() {}, onAlarm: event() },
      runtime: { id: "gojdebdikepkpapmnggeljneamjdigbh", getManifest: () => ({ version: "2.1.28" }),
        connectNative: () => port, onInstalled: event(), onStartup: event(),
        onMessage: { addListener(fn) { rendererMessage = fn; } }, onMessageExternal: event() },
      tabs: { onRemoved: event(), onActivated: event(), onUpdated: event(),
        query: async () => [{ id: 1, windowId: 1, url: "https://example.org/" }],
        get: async () => ({ id: 1, windowId: 1, url: "https://example.org/" }),
        update: async (id) => { pageMessages.push(`activate_tab:${id}`); return { id, windowId: 1 }; },
        sendMessage(tabId, message) {
          pageMessages.push(message.type);
          if (message.type === "helper_status" && stallStatus) {
            stallStatus = false;
            return new Promise(resolve => { resolveStatus = resolve; });
          }
          if (message.type === "prepare_action")
            return new Promise(resolve => { resolvePrepare = resolve; });
          return Promise.resolve({ ok: true, version: "2.1.21" });
        } },
      webNavigation: { getAllFrames: async () => [] },
    },
  });
  vm.runInContext(workerSource
    + "\nglobalThis.probe = { pollOnce, wakeBridge, ownTab, endTask, resetNativePort, connectNative, snapshotDirectTiming, timing: () => pollBusy?.timing, taskCount: () => taskTabs.size, currentCommand: () => pollBusy?.command?.commandId, readiness: () => lastVerifiedHelperState };", context);
  return {
    messages, pageMessages, context,
    stallStatus: () => { stallStatus = true; },
    resumeStatus: () => resolveStatus?.({ ok: true, version: "2.1.21" }),
    now: () => clockMs,
    elapse: ms => { clockMs += ms; },
    delay: ms => new Promise(resolve => context.setTimeout(resolve, ms)),
    async advance(ms) {
      const target = clockMs + ms;
      await flush();
      while (true) {
        const next = [...timers.entries()].filter(([, timer]) => timer.due <= target)
          .sort((left, right) => left[1].due - right[1].due)[0];
        if (!next) break;
        timers.delete(next[0]);
        clockMs = next[1].due;
        next[1].fn();
        await flush();
      }
      clockMs = target;
      await flush();
    },
    queue: value => { command = value; heldPoll?.(); },
    cancel: (id = "click") => { cancelled.add(id); },
    onNative: fn => { nativeObserver = fn; },
    async execute(value) {
      command = value;
      await context.probe.pollOnce();
      return messages.findLast(message => message.type === "command_result" && message.commandId === value.commandId)?.result;
    },
    preparing: () => Boolean(resolvePrepare),
    preparedReply: () => resolvePrepare,
    resumePrepare: (supportsAuthority = true) => resolvePrepare({ ok: true, resolutionToken: "prepared-on-old-turn", commandAuthoritySupported: supportsAuthority }),
    rendererAuthority: (message = {}, sender = {}) => new Promise(resolve => rendererMessage({
      type: "helper_command_authority", taskId: "task", commandId: "click", ...message,
    }, { id: "gojdebdikepkpapmnggeljneamjdigbh", tab: { id: 1 }, frameId: 0, ...sender }, resolve)),
    async tickPoll() {
      const timer = [...timers.entries()].find(([, timer]) => timer.delay === 900 || timer.delay === 220);
      assert.ok(timer, "poll heartbeat must stay scheduled during page work");
      timers.delete(timer[0]);
      timer[1].fn();
      await flush();
    },
  };
}

async function directTabLatencyCases() {
  for (const page of ["loaded", "loading", "unavailable", "navigated"]) {
    for (const count of [1, 4]) {
      const h = harness(); await flush();
      let active = { id: 1, windowId: 1, url: "https://old.example/",
        status: page === "loading" ? "loading" : "complete" };
      const created = [];
      h.context.chrome.tabs.query = async () => [active];
      h.context.chrome.tabs.sendMessage = async (_tab, message) => {
        h.pageMessages.push(message.type);
        if (message.type === "helper_status") {
          if (page === "unavailable") return new Promise(() => {});
          await h.delay(400);
          return { ok: true, version: "2.1.21" };
        }
        return { ok: true };
      };
      h.context.chrome.scripting = { executeScript: () => new Promise(() => {}) };
      h.context.chrome.tabs.create = async ({ url, active: selected }) => {
        assert.equal(selected, true);
        await h.delay(20);
        active = { id: created.length + 2, windowId: 1, url, pendingUrl: url,
          status: page === "loading" ? "loading" : "complete" };
        created.push(active);
        return active;
      };
      if (page === "navigated") active.url = "https://changed.example/";
      for (let index = 0; index < count; index++) {
        const commandId = `${page}-${count}-${index}`;
        const started = h.now();
        const probesBeforeAction = h.pageMessages.filter(type => type === "helper_status").length;
        let probesAtReceipt;
        h.onNative(message => {
          if (message.type === "command_result" && message.commandId === commandId)
            probesAtReceipt = h.pageMessages.filter(type => type === "helper_status").length;
        });
        const pending = h.execute({ taskId: "direct", commandId, type: "open_direct_tab",
          requiresAuthorityCheck: true, url: `https://new${index}.example/` });
        await h.advance(20);
        const receipt = h.messages.find(message => message.type === "command_result"
          && message.commandId === commandId);
        if (!receipt) await h.advance(7000); // Let the old implementation finish before asserting.
        await pending;
        assert.ok(receipt, `${page}/${count}: tab receipt must need only the simulated 20 ms Chrome tab create`);
        assert.equal(receipt.result.ok, true);
        assert.equal(h.now() - started, 20, "page-status delays must not enter the direct-tab path");
        assert.equal(probesAtReceipt, probesBeforeAction,
          "tab-only commands must not probe an unrelated renderer before dispatch or receipt");
      }
      assert.equal(created.length, count, "each requested direct tab must open once");
      assert.equal(h.context.probe.taskCount(), 0, "direct tabs must not create DOM task leases");
      for (const message of h.messages.filter(item => ["poll", "command_check", "command_result"].includes(item.type))) {
        assert.equal(message.helper.extensionId, h.context.chrome.runtime.id);
        assert.equal(message.helper.serviceWorkerVersion, "2.1.21");
        assert.equal(message.helper.commandAuthoritySupported, true);
      }
    }
  }
}

async function directTabScheduledHandoffCases() {
  for (const count of [1, 4]) for (const dispatchDelay of [0, 60]) {
    const h = harness(undefined, true); await flush();
    const created = [];
    const command = index => ({ taskId: "handoff", commandId: `handoff-${index}`,
      type: "open_direct_tab", requiresAuthorityCheck: true, url: `https://handoff${index}.example/` });
    h.context.chrome.tabs.create = async ({ url }) => {
      await h.delay(20);
      created.push(url);
      return { id: created.length + 1, windowId: 1, pendingUrl: url };
    };
    h.onNative(message => {
      if (message.type !== "command_result") return;
      assert.equal(h.pageMessages.includes("helper_status"), false,
        "direct handoffs must precede idle DOM-status work");
      const next = Number(message.commandId.split("-")[1]) + 1;
      if (next < count) h.context.setTimeout(() => h.queue(command(next)), dispatchDelay);
    });
    // Only the first request is explicitly awakened. Later requests arrive
    // through the real receipt -> next desktop tool -> scheduled poll handoff.
    h.queue(command(0));
    h.context.probe.wakeBridge();
    await h.advance(count * 20 + (count - 1) * dispatchDelay);
    assert.equal(created.length, count, "queued follow-up tools must not wait an idle 900 ms after each receipt");
    const receipts = h.messages.filter(message => message.type === "command_result");
    assert.equal(receipts.length, count);
    assert.equal(new Set(receipts.map(message => message.commandId)).size, count);
    const polls = h.messages.filter(message => message.type === "poll").length;
    await h.advance(899);
    assert.equal(h.messages.filter(message => message.type === "poll").length, polls,
      "an empty follow-up poll must wait for work during the existing idle window without a busy loop");
  }
}

async function directTabIdleAndAuthorityCases() {
  // An idle DOM-status probe must not own the next queued native tab action.
  for (const count of [1, 4]) {
    const h = harness(); await flush();
    h.stallStatus();
    await h.context.probe.pollOnce();
    await until(() => h.pageMessages.includes("helper_status"));
    const created = [];
    h.context.chrome.tabs.create = async ({ url }) => {
      await h.delay(20);
      created.push(url);
      return { id: created.length + 1, windowId: 1, pendingUrl: url };
    };
    h.context.probe.wakeBridge();
    for (let index = 0; index < count; index++) {
      const pending = h.execute({ taskId: "idle-direct", commandId: `idle-${index}`,
        type: "open_direct_tab", requiresAuthorityCheck: true, url: `https://idle${index}.example/` });
      await h.advance(20);
      assert.equal((await pending)?.ok, true);
    }
    assert.equal(h.now(), count * 20, "idle renderer work must not delay the tab receipt");
    assert.equal(created.length, count);
    h.resumeStatus(); await flush();
    assert.ok(!h.messages.some(message => message.type === "heartbeat" && message.helper.commandChannelReady),
      "an idle probe begun before a newer command must not overwrite its result state");
  }

  // The two native checks still own direct mutation; cancelling any requested
  // tab in a four-action sequence prevents that tab and every later cancelled tab.
  for (const count of [1, 4]) {
    for (const cancelIndex of [0, count - 1]) {
      for (const cancelCheck of [1, 2]) {
        const h = harness(); await flush();
        const created = [];
        const checks = new Map();
        h.context.chrome.tabs.create = async ({ url }) => {
          created.push(url); return { id: created.length + 1, windowId: 1, pendingUrl: url };
        };
        h.onNative(message => {
          if (message.type !== "command_check") return;
          const seen = (checks.get(message.commandId) || 0) + 1;
          checks.set(message.commandId, seen);
          if (message.commandId === `cancel-${cancelIndex}` && seen === cancelCheck)
            for (let index = cancelIndex; index < count; index++) h.cancel(`cancel-${index}`);
        });
        for (let index = 0; index < count; index++) {
          const result = await h.execute({ taskId: "cancel-direct", commandId: `cancel-${index}`,
            type: "open_direct_tab", requiresAuthorityCheck: true, url: `https://cancel${index}.example/` });
          assert.equal(result?.ok === true, index < cancelIndex);
        }
        assert.equal(created.length, cancelIndex, "revoked direct requests must never create a tab");
        assert.equal(h.context.probe.taskCount(), 0);
      }
    }
  }
  {
    const h = harness(); await flush();
    h.context.chrome.runtime.id = "untrusted-extension";
    let created = false;
    h.context.chrome.tabs.create = async () => { created = true; return { id: 2, windowId: 1 }; };
    await h.execute({ taskId: "untrusted", commandId: "untrusted", type: "open_direct_tab",
      requiresAuthorityCheck: true, url: "https://example.org/" });
    assert.equal(created, false, "a basic poll must retain the desktop's extension-identity gate");
  }
}

async function verifiedReadinessCases() {
  for (const rendererDelay of [40, 400]) {
    // A real renderer reply arrives asynchronously while the next native poll
    // is already held. Idle waiting is not DOM work and must not starve the
    // independently verified readiness cache.
    const h = harness(undefined, true); await flush();
    h.context.chrome.tabs.sendMessage = async (_tab, message) => {
      h.pageMessages.push(message.type);
      await h.delay(rendererDelay);
      return { ok: true, version: "2.1.21" };
    };
    await h.advance(2800);
    assert.ok(h.messages.some(message => message.type === "poll" && message.helper.commandChannelReady),
      "held native polls must publish an independently verified idle renderer on a subsequent poll");
    assert.ok(!h.messages.some(message => message.type === "heartbeat"),
      "idle refresh must not insert a heartbeat ahead of a held native poll response");
  }
  for (const invalidation of ["reload", "tab", "window", "url", "disconnect", "command"]) {
    const h = harness(undefined, true); await flush();
    h.stallStatus();
    await h.advance(1800);
    assert.ok(h.pageMessages.includes("helper_status"));
    if (invalidation === "reload") h.context.chrome.tabs.onUpdated.emit(1, { status: "loading" });
    else if (invalidation === "disconnect") h.context.probe.resetNativePort("held poll replaced");
    else if (invalidation === "command") {
      h.context.chrome.tabs.create = async ({ url }) => ({ id: 2, windowId: 1, pendingUrl: url });
      h.queue({ taskId: "held-readiness", commandId: "superseding-command", type: "open_direct_tab",
        requiresAuthorityCheck: true, url: "https://new.example/" });
      await flush();
    } else {
      h.context.chrome.tabs.query = async () => [{ id: invalidation === "tab" ? 2 : 1,
        windowId: invalidation === "window" ? 2 : 1,
        url: invalidation === "url" ? "https://changed.example/" : "https://example.org/" }];
    }
    h.resumeStatus(); await flush();
    assert.equal(h.context.probe.readiness(), null,
      `${invalidation}: a delayed idle probe must not cache obsolete readiness during a held poll`);
  }
  {
    const h = harness(); await flush();
    const tabs = [{ id: 1, windowId: 1, url: "https://ready.example/", status: "complete", active: true },
      { id: 2, windowId: 1, url: "https://other.example/", status: "complete", active: false }];
    h.context.chrome.tabs.query = async options => options.active ? tabs.filter(tab => tab.active) : tabs.slice();
    h.context.chrome.tabs.remove = async id => tabs.splice(tabs.findIndex(tab => tab.id === id), 1);
    await h.context.probe.pollOnce();
    await until(() => h.messages.some(message => message.type === "heartbeat" && message.helper.commandChannelReady));
    h.context.chrome.tabs.onUpdated.emit(2, { status: "loading" }, { active: false });
    h.context.chrome.tabs.onActivated.emit({ tabId: 91, windowId: 7 });
    h.context.chrome.tabs.onUpdated.emit(91, { status: "loading" }, { active: true, windowId: 7 });
    const probes = h.pageMessages.filter(type => type === "helper_status").length;
    const result = await h.execute({ taskId: "direct-close", commandId: "ready-close", type: "close_direct_tab",
      requiresAuthorityCheck: true, expectedSessionId: "lifecycle-test", tabId: 2, windowId: 1 });
    assert.equal(result.ok, true);
    const messages = h.messages.filter(message => message.commandId === "ready-close" || message.type === "poll").slice(-4);
    assert.ok(messages.filter(message => message.type === "poll" || message.type === "command_result")
      .every(message => message.helper.commandChannelReady), "unchanged loaded page readiness must not flap during a tab-only command");
    assert.equal(h.pageMessages.filter(type => type === "helper_status").length, probes);

    h.context.chrome.tabs.create = async ({ url }) => {
      tabs[0].active = false;
      const tab = { id: 3, windowId: 1, url, status: "complete", active: true }; tabs.push(tab); return tab;
    };
    await h.execute({ taskId: "direct-open", commandId: "new-page", type: "open_direct_tab",
      requiresAuthorityCheck: true, url: "https://new.example/" });
    assert.equal(h.messages.find(message => message.commandId === "new-page" && message.type === "command_result")
      .helper.commandChannelReady, false, "a new tab must not borrow the old tab's verified content status");
  }
  for (const invalidation of ["reload", "loading", "tab", "window", "url", "version", "unavailable", "identity", "disconnect", "expiry"]) {
    const h = harness(); await flush();
    await h.context.probe.pollOnce();
    await until(() => h.messages.some(message => message.type === "heartbeat" && message.helper.commandChannelReady));
    if (invalidation === "version" || invalidation === "unavailable") {
      h.context.chrome.tabs.sendMessage = async () => invalidation === "version"
        ? { ok: true, version: "stale" } : { ok: false };
      await h.context.probe.pollOnce(); await flush();
      assert.equal(h.messages.findLast(message => message.type === "heartbeat").helper.commandChannelReady, false);
    } else if (invalidation === "reload") {
      h.context.chrome.tabs.onUpdated.emit(1, { status: "loading" });
    } else if (invalidation === "identity") {
      h.context.chrome.runtime.id = "untrusted-extension";
    } else if (invalidation === "disconnect") {
      h.context.probe.resetNativePort("test reconnect"); h.context.probe.connectNative(); await flush();
    } else if (["loading", "tab", "window", "url"].includes(invalidation)) {
      h.context.chrome.tabs.query = async () => [{ id: invalidation === "tab" ? 2 : 1,
        windowId: invalidation === "window" ? 2 : 1,
        url: invalidation === "url" ? "https://changed.example/" : "https://example.org/",
        status: invalidation === "loading" ? "loading" : "complete" }];
    } else {
      h.context.chrome.tabs.sendMessage = () => new Promise(() => {});
      h.elapse(4001); // A suspended idle worker must not renew readiness through a basic poll.
    }
    h.queue({ taskId: "readiness", commandId: invalidation, type: "open_direct_tab",
      requiresAuthorityCheck: true, url: "https://fresh.example/" });
    h.context.chrome.tabs.create = async ({ url }) => ({ id: 2, windowId: 1, pendingUrl: url });
    await h.context.probe.pollOnce();
    assert.equal(h.messages.findLast(message => message.type === "poll").helper.commandChannelReady, false,
      `${invalidation} must invalidate the previous verified page status`);
  }
  for (const invalidation of ["reload", "disconnect"]) {
    const h = harness(); await flush();
    h.stallStatus();
    await h.context.probe.pollOnce(); await until(() => h.pageMessages.includes("helper_status"));
    if (invalidation === "reload") h.context.chrome.tabs.onUpdated.emit(1, { status: "loading" });
    else { h.context.probe.resetNativePort("old status"); h.context.probe.connectNative(); }
    await flush(); h.resumeStatus(); await flush();
    assert.ok(!h.messages.some(message => message.type === "heartbeat" && message.helper.commandChannelReady),
      "an old idle status response must not publish readiness after reload or native replacement");
  }
  for (const invalidation of ["reload", "disconnect"]) {
    const h = harness(); await flush();
    const tab = { id: 1, windowId: 1, url: "https://example.org/", status: "complete" };
    h.context.chrome.tabs.query = async () => [tab];
    h.context.chrome.windows = { getLastFocused: async () => ({ id: 1, focused: true }) };
    h.context.chrome.scripting = { executeScript: async () => [{ result: { text: "selected source", totalLength: 15 } }] };
    h.stallStatus();
    const pending = h.execute({ taskId: "selection", commandId: `result-${invalidation}`, type: "read_selection",
      requiresAuthorityCheck: true, windowId: 1, tabId: 1, expectedSessionId: "lifecycle-test" });
    await until(() => h.pageMessages.includes("helper_status"));
    if (invalidation === "reload") {
      tab.status = "loading";
      h.context.chrome.tabs.onUpdated.emit(1, { status: "loading" });
    } else {
      h.context.probe.resetNativePort("replaced while collecting result");
      h.context.probe.connectNative();
    }
    await flush(); h.resumeStatus();
    const result = await pending;
    const receipt = h.messages.find(message => message.type === "command_result"
      && message.commandId === `result-${invalidation}`);
    if (invalidation === "reload") {
      assert.equal(result.ok, true, "the earlier owned selection receipt remains intact");
      assert.equal(receipt.helper.commandChannelReady, false,
        "reload during a full result status probe must not publish the old page as ready");
    } else {
      assert.equal(receipt, undefined, "an old result collector must not publish on a replacement native connection");
    }
  }
}

async function domNavigationAcknowledgementCases() {
  {
    const h = harness(); await flush();
    h.context.chrome.tabs.query = async () => [{ id: 17, windowId: 7, url: "chrome://newtab/", status: "complete" }];
    const result = await h.execute({ taskId: "protected-bootstrap", commandId: "bootstrap", type: "begin_task",
      requiresAuthorityCheck: true });
    assert.equal(result.tabId, 17);
    assert.equal(result.windowId, 7);
    assert.equal(result.domEpoch, "protected");
    assert.equal(h.context.probe.taskCount(), 1);
    assert.equal(h.pageMessages.includes("helper_status"), false,
      "protected-tab BA bootstrap must retain tab metadata without attempting unavailable DOM work");
    const helper = h.messages.find(message => message.type === "command_result").helper;
    assert.equal(helper.tabId, 17);
    assert.equal(helper.windowId, 7);
    assert.equal(helper.activeUrl, "chrome://newtab/");
    assert.equal(helper.commandChannelReady, false);
  }
  for (const type of ["navigate", "open_tab"]) {
    for (const page of ["loaded", "loading", "unavailable"]) {
      for (const count of [1, 4]) {
        const h = harness(); await flush();
        const tabs = [{ id: 1, windowId: 1, url: "https://old.example/", status: "complete", active: true }];
        let scanReply;
        let navigationCount = 0;
        h.context.probe.ownTab("dom", 1);
        h.context.chrome.tabs.query = async options => options.active ? tabs.filter(tab => tab.active) : tabs.slice();
        h.context.chrome.tabs.get = async id => tabs.find(tab => tab.id === id);
        const applyNavigation = (tab, url) => {
          navigationCount++;
          tab.url = url;
          tab.status = page === "loading" ? "loading" : "complete";
          h.context.chrome.tabs.onUpdated.emit(tab.id, { status: "loading", url });
          if (page === "loading") h.context.setTimeout(() => { tab.status = "complete"; }, 180);
          return tab;
        };
        h.context.chrome.tabs.update = async (id, changes) => {
          const tab = tabs.find(item => item.id === id);
          if (changes.url) applyNavigation(tab, changes.url);
          return tab;
        };
        h.context.chrome.tabs.create = async ({ url }) => {
          tabs.forEach(tab => { tab.active = false; });
          const tab = { id: tabs.length + 1, windowId: 1, active: true };
          tabs.push(tab); return applyNavigation(tab, url);
        };
        h.context.chrome.scripting = { executeScript: async () => { throw new Error("renderer unavailable"); } };
        h.context.chrome.tabs.sendMessage = (_tabId, message) => {
          h.pageMessages.push(message.type);
          if (message.type === "helper_status") return page === "unavailable"
            ? Promise.reject(new Error("no content script"))
            : Promise.resolve({ ok: true, version: "2.1.21", graphVersion: navigationCount,
              domEpoch: `document-${navigationCount}` });
          if (message.type === "scan") return new Promise(resolve => { scanReply = resolve; });
          return Promise.resolve({ ok: true });
        };
        for (let index = 0; index < count; index++) {
          scanReply = null;
          const commandId = `${type}-${page}-${index}`;
          const pending = h.execute({ taskId: "dom", commandId, type, tabId: 1,
            requiresAuthorityCheck: true, url: `https://destination${index}.example/` });
          await until(() => navigationCount === index + 1);
          await h.advance(page === "loading" ? 180 : 0);
          if (page === "unavailable") {
            assert.equal((await pending).ok, false, "navigation must not claim DOM verification without a live renderer");
            assert.equal(scanReply, null);
          } else {
            await until(() => scanReply);
            assert.ok(!h.messages.some(message => message.type === "command_result" && message.commandId === commandId),
              "DOM navigation must wait for its independent page snapshot");
            const active = tabs.find(tab => tab.active);
            scanReply({ ok: true, tabId: type === "navigate" ? 1 : active.id, windowId: 1,
              url: type === "navigate" ? tabs[0].url : active.url,
              graphVersion: index + 1, domEpoch: `document-${index + 1}`, controls: [] });
            const result = await pending;
            assert.equal(result.ok, true);
            assert.equal(result.graphVersion, index + 1);
            assert.equal(result.domEpoch, `document-${index + 1}`);
          }
        }
        assert.equal(navigationCount, count);
        assert.ok(h.pageMessages.includes("helper_status"), "DOM navigation retains its live content-version check");
      }
    }
  }
}

async function directTimingMetadataCases() {
  const h = harness(); await flush();
  const timersBefore = h.messages.length;
  let queries = 0;
  let queriesAtReceipt = 0;
  h.onNative(message => {
    if (message.type === "command_result") queriesAtReceipt = queries;
  });
  h.context.chrome.tabs.query = async () => {
    queries++;
    await h.delay(10);
    return [{ id: 111, windowId: 222, url: "https://private.example/secret", title: "PRIVATE_TITLE", status: "complete" }];
  };
  h.context.chrome.tabs.create = async ({ url }) => {
    await h.delay(40);
    return { id: 333, windowId: 222, url };
  };
  const pending = h.execute({ taskId: "private-task-value", commandId: "private-command-value",
    type: "open_direct_tab", requiresAuthorityCheck: true, url: "https://private.example/secret" });
  await h.advance(60);
  const result = await pending;
  assert.equal(result.ok, true);
  assert.equal(queriesAtReceipt, 2, "timing metadata must not add any Chrome query before the receipt");
  const traffic = h.messages.slice(timersBefore);
  assert.deepEqual(traffic.map(message => message.type), ["poll", "command_check", "command_check", "command_result"],
    "timing metadata must not create a native command, heartbeat or continuation");
  assert.ok(traffic.every(message => message.directTiming?.source === "direct-tab-fastpath-1"));
  const trace = traffic.at(-1).directTiming;
  assert.equal(trace.prePollQueryMs, 10);
  assert.equal(trace.createMs, 40);
  assert.equal(trace.resultQueryMs, 10);
  assert.equal(trace.workerTotalMs, 60);
  assert.equal(trace.authority1Ms, 0);
  assert.equal(trace.authority2Ms, 0);
  assert.equal(trace.stage, "result_send");
  assert.equal(Object.hasOwn(result, "directTiming"), false, "diagnostics must never enter the tool receipt/model payload");
  const allowed = new Set(["source", "stage", "cycle", "stageMs", "prePollQueryMs", "pollRoundTripMs",
    "authority1Ms", "authority2Ms", "createMs", "resultQueryMs", "resultQueryRetryMs", "nativeQueueDepth", "workerTotalMs"]);
  for (const message of traffic) {
    for (const [key, value] of Object.entries(message.directTiming)) {
      assert.ok(allowed.has(key));
      if (!["source", "stage"].includes(key)) assert.ok(Number.isFinite(value) && value >= 0 && value <= 120000);
    }
    assert.doesNotMatch(JSON.stringify(message.directTiming), /private|PRIVATE|111|222|333|https/);
  }

  // A receipt that never completes still exposes only its current bounded
  // stage on the already scheduled authority heartbeat.
  const stalled = harness(); await flush();
  let finishCreate;
  stalled.context.chrome.tabs.create = () => new Promise(resolve => { finishCreate = resolve; });
  const waiting = stalled.execute({ taskId: "stall", commandId: "stall", type: "open_direct_tab",
    requiresAuthorityCheck: true, url: "https://private.example/secret" });
  await until(() => finishCreate);
  stalled.context.probe.timing().metrics.privateUrl = "https://must-not-leak.example/";
  stalled.context.probe.timing().metrics.prePollQueryMs = "PRIVATE_STRING";
  stalled.context.probe.timing().metrics.createMs = Number.POSITIVE_INFINITY;
  const sanitized = stalled.context.probe.snapshotDirectTiming("heartbeat");
  assert.equal(Object.hasOwn(sanitized, "privateUrl"), false);
  assert.equal(Object.hasOwn(sanitized, "prePollQueryMs"), false);
  assert.equal(Object.hasOwn(sanitized, "createMs"), false);
  assert.doesNotMatch(JSON.stringify(sanitized), /must-not-leak|PRIVATE_STRING/);
  await stalled.advance(900);
  const heartbeat = stalled.messages.find(message => message.type === "heartbeat");
  assert.equal(heartbeat.directTiming.stage, "create");
  assert.equal(heartbeat.directTiming.stageMs, 900);
  finishCreate({ id: 444, windowId: 555 });
  assert.equal((await waiting).ok, true);
  assert.equal(stalled.messages.filter(message => message.type === "command_result").length, 1);
}

async function main() {
  await directTimingMetadataCases();
  await directTabLatencyCases();
  await directTabScheduledHandoffCases();
  await directTabIdleAndAuthorityCases();
  await verifiedReadinessCases();
  await domNavigationAcknowledgementCases();
  {
    const h = harness(); await flush();
    const selection = "s".repeat(23029);
    let focused = true;
    let activeTabId = 31;
    let scriptCalls = 0;
    h.context.window = { getSelection: () => ({ toString: () => selection }) };
    h.context.chrome.windows = { getLastFocused: async () => ({ id: 9, focused }) };
    h.context.chrome.tabs.query = async () => [{ id: activeTabId, windowId: 9,
      url: "https://example.org/", active: true }];
    h.context.chrome.scripting = { executeScript: async ({ func }) => {
      scriptCalls++;
      return [{ result: func() }];
    } };
    const read = (id, overrides = {}) => h.execute({
      taskId: "selection-read", commandId: id, type: "read_selection",
      windowId: 9, tabId: 31, expectedSessionId: "lifecycle-test",
      requiresAuthorityCheck: true, ...overrides,
    });
    const result = await read("fresh-selection");
    assert.equal(result.ok, true);
    assert.equal(result.text.length, 23029);
    assert.equal(result.totalLength, 23029);
    assert.equal(result.truncated, false);
    assert.equal(h.context.probe.taskCount(), 0, "selection read must not start BA");
    focused = false;
    assert.equal((await read("background-window")).ok, false);
    focused = true;
    activeTabId = 32;
    assert.equal((await read("changed-tab")).ok, false);
    activeTabId = 31;
    assert.equal((await read("changed-session", { expectedSessionId: "old" })).ok, false);
    assert.equal(scriptCalls, 1, "identity rejection must happen before page read");
    h.context.chrome.scripting.executeScript = async ({ func }) => {
      scriptCalls++;
      const result = [{ result: func() }];
      focused = false;
      return result;
    };
    assert.equal((await read("lost-focus-after-read")).ok, false,
      "a completed read must be discarded if Chrome lost foreground ownership");
    focused = true;
    h.context.probe.ownTab("other-browser-task", 31);
    assert.equal((await read("active-ba-task")).ok, false);
    assert.equal(scriptCalls, 2, "an active BA task must not expose its selection through this fallback");
  }
  {
    const h = harness(); await flush();
    let selection = "";
    h.context.window = { getSelection: () => ({ toString: () => selection }) };
    h.context.chrome.windows = { getLastFocused: async () => ({ id: 9, focused: true }) };
    h.context.chrome.tabs.query = async () => [{ id: 31, windowId: 9, url: "https://example.org/", active: true }];
    h.context.chrome.scripting = { executeScript: async ({ func }) => [{ result: func() }] };
    for (const text of [5999, 6000, 6001, 32000, 49999, 50000, 50001, 250000].map(n => "x".repeat(n))
      .concat(["\r\nमराठी 日本語 👋\n".repeat(5000), "x".repeat(49998) + "😀", "x".repeat(49999) + "😀"])) {
      selection = text;
      const result = await h.execute({ taskId: "selection-read", commandId: `size-${text.length}`,
        type: "read_selection", windowId: 9, tabId: 31, expectedSessionId: "lifecycle-test", requiresAuthorityCheck: true });
      assert.equal(result.ok, true);
      assert.equal(result.totalLength, text.length);
      assert.equal(result.text, text, "no dropped newlines, split surrogate or partial prefix");
      assert.equal(result.truncated, false);
      assert.equal(h.context.probe.taskCount(), 0);
    }
  }
  {
    const h = harness(); await flush();
    const tabs = [
      { id: 41, windowId: 7, index: 0, active: true, url: "https://one.example/" },
      { id: 42, windowId: 7, index: 1, active: false, url: "https://two.example/" },
      { id: 43, windowId: 7, index: 2, active: false, url: "https://three.example/" },
      { id: 44, windowId: 7, index: 3, active: false, url: "https://four.example/" },
    ];
    const activated = [];
    h.context.chrome.tabs.query = async ({ windowId }) => windowId === 7 ? tabs.slice().reverse() : [];
    h.context.chrome.tabs.update = async id => {
      activated.push(id);
      tabs.forEach(tab => { tab.active = tab.id === id; });
      return tabs.find(tab => tab.id === id);
    };
    for (const [selection, expectedId] of [
      ["index:3", 43], ["from_end:1", 44], ["index:1", 41], ["from_end:2", 43],
    ]) {
      const result = await h.execute({ taskId: "tab-selection", commandId: selection,
        type: "activate_existing_tab", windowId: 7, text: selection, requiresAuthorityCheck: true });
      assert.equal(result.ok, true);
      assert.equal(result.tabId, expectedId, `selection ${selection} must use the stable ordered tab ID`);
      assert.equal(tabs.find(tab => tab.active)?.id, expectedId);
      assert.equal(result.tabCount, 4);
    }
    const missing = await h.execute({ taskId: "tab-selection", commandId: "index:5",
      type: "activate_existing_tab", windowId: 7, text: "index:5", requiresAuthorityCheck: true });
    assert.equal(missing.ok, false);
    assert.deepEqual(activated, [43, 44, 41, 43], "an absent ordinal must not activate another tab");
  }
  {
    const h = harness(); await flush();
    const tabs = [
      { id: 71, windowId: 7, index: 0, active: true },
      { id: 72, windowId: 7, index: 1, active: false },
      { id: 73, windowId: 7, index: 2, active: false },
      { id: 81, windowId: 8, index: 0, active: true },
      { id: 82, windowId: 8, index: 1, active: false },
    ];
    const activated = [];
    h.context.chrome.tabs.query = async ({ windowId }) =>
      tabs.filter(tab => tab.windowId === windowId).reverse();
    h.context.chrome.tabs.update = async id => {
      const target = tabs.find(tab => tab.id === id);
      activated.push(id);
      tabs.filter(tab => tab.windowId === target.windowId)
        .forEach(tab => { tab.active = tab.id === id; });
      return target;
    };
    const select = (windowId, selection) => h.execute({
      taskId: "tab-selection", commandId: `${windowId}-${selection}`,
      type: "activate_existing_tab", windowId, text: selection,
      requiresAuthorityCheck: true,
    });
    assert.equal((await select(7, "index:3")).tabId, 73);
    assert.equal((await select(8, "from_end:1")).tabId, 82);
    assert.equal((await select(8, "index:3")).ok, false,
      "an absent ordinal in one window must not select a tab from another window");
    assert.deepEqual(activated, [73, 82]);
    assert.equal(tabs.find(tab => tab.id === 73).active, true);
    assert.equal(tabs.find(tab => tab.id === 82).active, true);
  }
  {
    const h = harness(); await flush();
    const tabs = [{ id: 1, windowId: 1 }, { id: 2, windowId: 1 }, { id: 3, windowId: 2 }];
    const removed = [];
    h.context.chrome.tabs.query = async () => tabs.slice();
    h.context.chrome.tabs.create = async ({ url }) => { const tab = { id: 4, windowId: 1, pendingUrl: url }; tabs.push(tab); return tab; };
    h.context.chrome.tabs.remove = async id => { removed.push(id); tabs.splice(tabs.findIndex(tab => tab.id === id), 1); };
    const opened = await h.execute({ taskId: "direct", commandId: "open", type: "open_direct_tab", requiresAuthorityCheck: true, url: "https://example.org/requested" });
    assert.equal(opened.ok, true); assert.equal(opened.tabId, 4); assert.ok(opened.sessionId);
    assert.equal(h.context.probe.taskCount(), 0, "direct tabs must not create a BA task/lease");
    const close = { taskId: "direct", type: "close_direct_tab", requiresAuthorityCheck: true, tabId: 4, windowId: 1, expectedSessionId: opened.sessionId };
    assert.equal((await h.execute({ ...close, commandId: "wrong-session", expectedSessionId: "expired" })).ok, false);
    assert.equal((await h.execute({ ...close, commandId: "wrong-window", windowId: 2 })).ok, false);
    assert.deepEqual(removed, []);
    assert.equal((await h.execute({ ...close, commandId: "close" })).ok, true);
    assert.equal((await h.execute({ ...close, commandId: "already" })).alreadyClosed, true);
    assert.deepEqual(removed, [4]);
    assert.deepEqual(tabs.map(tab => tab.id), [1, 2, 3], "unrelated tabs/windows must remain intact");
  }
  {
    const h = harness(); await flush();
    let windowTouched = false;
    h.context.chrome.tabs.create = async ({ url, active }) => {
      assert.equal(active, true);
      return { id: 91, windowId: 17, pendingUrl: url };
    };
    h.context.chrome.windows = {
      get: async () => { windowTouched = true; throw new Error("direct open must not inspect window"); },
      update: async () => { windowTouched = true; throw new Error("direct open must not focus window"); },
    };
    const result = await h.execute({ taskId: "direct", commandId: "open-without-window-focus",
      type: "open_direct_tab", requiresAuthorityCheck: true, url: "https://example.org/" });
    assert.equal(result.ok, true);
    assert.equal(result.tabId, 91);
    assert.equal(windowTouched, false, "direct open must leave native window state unchanged");
    assert.equal(h.context.probe.taskCount(), 0, "direct open must not acquire a BA task");
  }
  {
    const h = harness(); await flush();
    h.context.chrome.tabs.query = async () => [{ id: 1, windowId: 1,
      url: "https://slow.example/", status: "loading" }];
    h.context.chrome.tabs.create = async ({ url }) => ({ id: 92, windowId: 1, pendingUrl: url });
    h.stallStatus();
    const pending = h.execute({ taskId: "direct", commandId: "open-during-load",
      type: "open_direct_tab", requiresAuthorityCheck: true, url: "https://new.example/" });
    await flush();
    const receiptBeforePageStatus = h.messages.some(message =>
      message.type === "command_result" && message.commandId === "open-during-load");
    h.resumeStatus();
    await pending;
    assert.equal(receiptBeforePageStatus, true,
      "a loading page's content status must not delay a service-worker-only tab open");
    assert.equal(h.pageMessages.includes("helper_status"), false,
      "a loading page cannot yet answer content-status probes");
  }
  {
    const h = harness(); await flush();
    let listed = false; let finishQuery;
    h.context.chrome.tabs.query = options => Object.keys(options).length
      ? Promise.resolve([{ id: 1, windowId: 1, url: "https://example.org/" }])
      : new Promise(resolve => { listed = true; finishQuery = resolve; });
    const removed = [];
    h.context.chrome.tabs.remove = async id => removed.push(id);
    const pending = h.execute({ taskId: "direct", commandId: "cancel-close", type: "close_direct_tab", requiresAuthorityCheck: true, tabId: 1, windowId: 1, expectedSessionId: "lifecycle-test" });
    await until(() => listed);
    h.cancel("cancel-close"); finishQuery([{ id: 1, windowId: 1 }]);
    assert.notEqual((await pending)?.ok, true);
    assert.deepEqual(removed, [], "cancellation during validation must prevent tab closure");
  }
  {
    const h = harness(); await flush();
    let exists = true;
    h.context.chrome.tabs.query = async () => exists ? [{ id: 1, windowId: 1 }] : [];
    h.context.chrome.tabs.remove = async () => { exists = false; throw new Error("No tab with id: 1 (user closed it)"); };
    const result = await h.execute({ taskId: "direct", commandId: "manual-close", type: "close_direct_tab", requiresAuthorityCheck: true, tabId: 1, windowId: 1, expectedSessionId: "lifecycle-test" });
    assert.equal(result.ok, true, "verify the final tab set even when the user closes the target during validation");
    assert.equal(result.alreadyClosed, true);
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("same-task", 1);
    const cleanup = h.context.probe.endTask({ taskId: "same-task" });
    h.context.probe.ownTab("same-task", 1);
    await cleanup;
    assert.ok(!h.pageMessages.includes("end_task"), "a resumed lease cannot receive an old asynchronous cleanup");
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("same-task", 1);
    h.queue({ taskId: "same-task", commandId: "old", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const old = h.context.probe.pollOnce();
    await until(h.preparing);
    const finishOld = h.preparedReply();
    const heartbeat = h.context.probe.pollOnce();
    h.context.probe.resetNativePort("disconnect while preparing");
    assert.equal(h.context.probe.taskCount(), 0, "native reset immediately revokes the old leases");
    h.context.probe.connectNative();
    h.context.probe.ownTab("same-task", 1);
    await heartbeat;
    assert.equal(h.context.probe.taskCount(), 1, "an old heartbeat failure cannot clear a newly adopted lease");
    await flush();
    h.queue({ taskId: "same-task", commandId: "fresh", type: "click", tabId: 1,
      controlId: "g2c1", requiresAuthorityCheck: true });
    const fresh = h.context.probe.pollOnce();
    await until(() => h.preparedReply() !== finishOld);
    finishOld({ ok: true, resolutionToken: "old", commandAuthoritySupported: true });
    await old;
    assert.equal(h.context.probe.taskCount(), 1, "a late old-port failure cannot clear a replacement task lease");
    h.resumePrepare();
    await fresh;
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("task", 1);
    h.queue({ taskId: "task", commandId: "click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const pending = h.context.probe.pollOnce();
    await until(h.preparing);
    assert.equal((await h.rendererAuthority()).ok, true);
    for (const sender of [{ tab: { id: 2 } }, { frameId: 2 }, { id: "untrusted-extension" }])
      assert.equal((await h.rendererAuthority({}, sender)).ok, false, "only the owned renderer can request mutation authority");
    assert.equal((await h.rendererAuthority({ commandId: "old-command" })).ok, false);
    assert.equal((await h.rendererAuthority({ taskId: "old-task" })).ok, false);
    h.cancel();
    assert.equal((await h.rendererAuthority()).ok, false, "desktop cancellation revokes renderer authority immediately");
    assert.equal(h.context.probe.taskCount(), 0);
    h.resumePrepare();
    await pending;
    assert.ok(!h.pageMessages.includes("commit_action"));
  }
  {
    const h = harness();
    await flush();
    h.stallStatus();
    const pending = h.context.probe.pollOnce();
    await flush();
    await h.tickPoll();
    assert.ok(h.messages.filter(message => message.type === "poll").length >= 2,
      "a blocked idle status probe must not block native polling");
    h.resumeStatus();
    await pending;
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("task", 1);
    h.queue({ taskId: "task", commandId: "click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const pending = h.context.probe.pollOnce();
    await until(h.preparing);
    h.cancel();
    await h.tickPoll();
    assert.equal(h.context.probe.taskCount(), 0, "cancellation must clear local task authority while preparation is blocked");
    assert.ok(h.pageMessages.includes("end_task"), "cancellation clears the task indicator without waiting for preparation");
    h.queue({ taskId: "fresh-task", commandId: "fresh", type: "activate_existing_tab",
      windowId: 1, text: "index:1", requiresAuthorityCheck: true });
    await h.context.probe.pollOnce();
    assert.equal(h.pageMessages.filter(type => type === "activate_tab:1").length, 1,
      "a new command must work while the cancelled page response is still pending");
    h.resumePrepare();
    await pending;
    assert.ok(!h.pageMessages.includes("commit_action"),
      "cancelled prepared work must never commit after the page responds");
    assert.ok(h.messages.some(message => message.type === "command_check"));
  }
  {
    const h = harness();
    await flush();
    h.stallStatus();
    const old = h.context.probe.pollOnce();
    await flush();
    h.context.probe.resetNativePort("test disconnect");
    h.context.probe.connectNative();
    await flush();
    h.context.probe.ownTab("new", 1);
    h.queue({ taskId: "new", commandId: "new-click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const current = h.context.probe.pollOnce();
    await until(h.preparing);
    h.resumeStatus();
    await old;
    assert.equal(h.context.probe.currentCommand(), "new-click",
      "old transport completion cannot clear the replacement command owner");
    h.resumePrepare();
    await current;
    assert.equal(h.pageMessages.filter(type => type === "commit_action").length, 1);
  }
  for (const requiresAuthorityCheck of [false, true]) {
    const h = harness();
    await flush();
    h.context.probe.ownTab("task", 1);
    h.queue({ taskId: "task", commandId: "click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck });
    const pending = h.context.probe.pollOnce();
    await until(h.preparing);
    h.resumePrepare();
    await pending;
    assert.equal(h.pageMessages.filter(type => type === "commit_action").length, 1,
      "both original and lease-aware desktop commands commit exactly once");
    assert.equal(h.messages.filter(message => message.type === "command_result").length, 1);
    assert.equal(h.messages.some(message => message.type === "command_check"), requiresAuthorityCheck);
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("task", 1);
    h.queue({ taskId: "task", commandId: "click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const pending = h.context.probe.pollOnce();
    await until(h.preparing);
    h.resumePrepare(false);
    await pending;
    assert.ok(!h.pageMessages.includes("commit_action"), "a stale pre-update content script cannot bypass renderer cancellation");
    const result = h.messages.find(message => message.type === "command_result").result;
    assert.equal(result.ok, false);
    assert.match(result.error, /Refresh this tab/);
  }
  console.log("Helper direct-tab latency: 20 fake ms/action; loaded/loading/unavailable/navigated x 1/4: PASS");
  console.log("Helper idle wake, verified readiness, direct cancellation and DOM navigation acknowledgement: PASS");
  console.log("Helper heartbeat and cancelled preparation: PASS");
}
const watchdog = setTimeout(() => {
  console.error("Helper lifecycle test stalled before reaching its assertions.");
  process.exit(1);
}, 10000);
main().catch(error => { console.error(error); process.exitCode = 1; })
  .finally(() => clearTimeout(watchdog));
