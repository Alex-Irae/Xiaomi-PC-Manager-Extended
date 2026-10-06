/* Purpose: primary white quick panel. Dependencies: bridge.js and the desktop host.
   Output: confirmed device controls. Launch with PCManager.exe --toggle. */
"use strict";
let snapshot = null;
let awake = false;
let sleepOff = false;
let busy = false;
let reading = false;
let pendingRefresh = false;
let changeTail = Promise.resolve();
let queuedChanges = 0;
const pendingControls = new Set();
const controlKey = element => element.id || element.dataset.toggle || element.dataset.mode || element.dataset.tool || element.dataset.oem || element.dataset.system || element.dataset.native;
let toastTimer;
const $ = id => document.getElementById(id);

document.querySelectorAll("[data-icon]").forEach(element => { element.innerHTML = UI.icon(element.dataset.icon); });
function notice(message) {
  $("notification").textContent = message;
  $("notification").hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $("notification").hidden = true; }, 6000);
}
function enable(element, condition) {
  const pending = pendingControls.has(controlKey(element));
  element.disabled = pending || !Native.connected || !condition;
  element.setAttribute("aria-busy", String(pending));
}
function update() {
  const h = snapshot?.hardware || {};
  const p = h.preferences || {};
  const selectedSystems = new Set(h.customization?.quickSystemActions || DefaultQuickSystemActions);
  UI.applyAppearance(p.appearance);
  UI.applyPreferences(p);
  UI.quickIcons = h.customization?.quickIcons || {};
  const systemOrder = [...selectedSystems].filter(id => QuickSystemTools.some(tool => tool.id === id));
  systemOrder.push(...QuickSystemTools.filter(tool => !selectedSystems.has(tool.id)).map(tool => tool.id));
  const systemGrid = $("system-tools");
  if (systemGrid.dataset.order !== systemOrder.join("|")) {
    systemGrid.dataset.order = systemOrder.join("|");
    systemGrid.innerHTML = systemOrder.map(id => {
      const tool = QuickSystemTools.find(item => item.id === id);
      return `<button class="circle-tile" data-system="${tool.id}"${tool.toggle ? ` data-toggle="${tool.toggle}" aria-pressed="false"` : tool.shortcut ? ` data-shortcut="${tool.shortcut}"` : ` data-native="${tool.method}"`}${tool.title ? ` title="${UI.escape(tool.title)}"` : ""} disabled><span class="circle" data-icon="${tool.icon}">${UI.icon(tool.icon)}</span><span class="system-label">${UI.escape(tool.label)}</span></button>`;
    }).join("");
  }
  document.querySelectorAll("[data-icon]").forEach(element => { element.innerHTML = UI.icon(element.dataset.icon); });
  $("device-name").textContent = h.deviceName || "This PC";
  $("panel-title").textContent = h.testMode ? "Quick controls -test" : "Quick controls";
  $("current-mode").textContent = h.mode == null ? "Unavailable" : UI.mode(h.mode, h.powerSource);
  const ac = h.powerSource === "Online";
  const knownSource = ac || h.powerSource === "Offline";
  $("power-source").textContent = ac ? "AC" : knownSource ? "Battery" : "Unknown";
  const modeIcons = { Quiet: "quiet", Auto: "auto", FullSpeed: "speed", Turbo: "speed", Eco: "battery" };
  const order = UI.modes(h.powerSource);
  const visible = order.filter(mode => (h.visibleModes || order).includes(mode));
  const signature = h.powerSource + ":" + visible.join(",");
  // Preserve focused buttons while refreshing. Rebuild only when the source's visible set changes.
  if ($("performance-modes").dataset.modes !== signature) {
    $("performance-modes").dataset.modes = signature;
    $("performance-modes").innerHTML = visible.map(mode => `<button data-mode="${mode}" class="mode-tile" disabled aria-pressed="false">${UI.icon(modeIcons[mode])}<span>${UI.escape(UI.mode(mode, h.powerSource))}</span></button>`).join("");
  }
  $("battery-status").textContent = UI.number(h.batteryPercent, "%") + (h.powerSource === "Online" ? " · AC" : h.powerSource === "Offline" ? " · On battery" : "");
  document.querySelectorAll("[data-mode]").forEach(button => {
    button.hidden = Boolean(h.visibleModes && !h.visibleModes.includes(button.dataset.mode));
    button.setAttribute("aria-pressed", String(h.mode === button.dataset.mode));
    enable(button, knownSource && h.mode != null && (button.dataset.mode !== "FullSpeed" || ac));
  });
  const care = h.requestedChargeLimit != null && h.requestedChargeLimit < 100;
  $("charge-summary").textContent = "Battery care";
  const estimate = UI.chargeEstimate(h, snapshot?.telemetry || {});
  const chargeTime = estimate.status === "Charging" ? " · " + Math.max(1, Math.round(Math.abs(estimate.minutes))) + " min" : "";
  $("charge-detail").textContent = (h.travel ? "Travel to 100%" : care ? "Limit " + h.requestedChargeLimit + "%" : "Full charge · 100%") + chargeTime;
  $("battery-care").setAttribute("aria-pressed", String(care || h.travel));
  enable($("battery-care"), h.firmwareAvailable && h.requestedChargeLimit != null);
  $("travel-label").textContent = h.travel ? "Cancel travel" : "Travel charge";
  $("travel").setAttribute("aria-pressed", String(Boolean(h.travel)));
  enable($("travel"), h.travel || (care && h.powerSource === "Online"));
  const refreshLabel = document.querySelector('[data-toggle="refresh"] .system-label');
  if (refreshLabel) refreshLabel.textContent = h.refreshRate != null ? h.refreshRate + " Hz" : "Refresh rate";
  for (const [key, current] of Object.entries({ touchpad: h.touchpadEnabled, touchscreen: h.touchscreenEnabled, refresh: h.refreshRate, autorefresh: p.autoRefresh, awake, sleepoff: sleepOff })) {
    const button = document.querySelector(`[data-toggle="${key}"]`);
    const feature = key === "refresh" || key === "autorefresh" ? p.refreshFeature : key === "awake" ? p.owlFeature : key === "sleepoff" ? true : p[key + "Feature"];
    button.hidden = !selectedSystems.has(key) || feature === false;
    button.setAttribute("aria-pressed", String(key === "refresh" ? current != null : Boolean(current)));
    enable(button, feature !== false && (key === "refresh" ? h.refreshRates?.length > 1 : current != null));
    if (current == null && key !== "refresh") button.title = "This control is unavailable on this device.";
  }
  const brightness = $("brightness");
  if (document.activeElement !== brightness && h.brightness != null) brightness.value = String(h.brightness);
  if (!BrightnessInput.pending && document.activeElement !== brightness) $("brightness-value").textContent = UI.number(h.brightness, "%");
  enable(brightness, h.brightness != null);
  document.querySelectorAll("[data-oem],[data-tool],[data-shortcut]").forEach(button => enable(button, true));
  document.querySelectorAll("[data-native]").forEach(button => { button.disabled = !Native.connected; });
  document.querySelectorAll("[data-system]").forEach(button => { if (!button.dataset.toggle) button.hidden = !selectedSystems.has(button.dataset.system); });
  $("system-tools").hidden = selectedSystems.size === 0;
  const tools = [...QuickTools, ...(h.customization?.quickLinks || []).filter(link => link.showInPanel !== false).slice(0, 4).map(link => ({ id: link.id, label: link.label, icon: link.icon, method: "app.open", args: { id: link.id } }))];
  $("quick-tools").innerHTML = tools.map(tool => `<button class="circle-tile" data-tool="${UI.escape(tool.id)}"${pendingControls.has(tool.id) || !Native.connected ? " disabled" : ""}><span class="circle">${tool.icon === "app" ? `<img alt="" src="https://xiaomi-icons.local/${encodeURIComponent(tool.id)}.png">` : UI.icon(tool.icon)}</span><span>${UI.escape(tool.label)}</span></button>`).join("");
  $("quick-tools").hidden = tools.length === 0;
}
async function refresh(force = false) {
  if (!Native.connected || (busy && force !== true) || BrightnessInput.pending || document.hidden) return false;
  if (reading) { pendingRefresh = true; return false; }
  reading = true;
  try {
    const state = await Native.call("quick.read");
    const windowState = await Native.call("window.state");
    snapshot = state;
    awake = windowState.awake;
    sleepOff = windowState.preventSleep;
    const problem = state.hardware.firmwareError || (state.hardware.chargeConflict ? `Charging conflict: requested ${state.hardware.requestedChargeLimit}%, firmware reports ${state.hardware.chargeLimit}%. The resident will retry; check OEM isolation if this persists.` : null) || (windowState.isolationStatus?.includes("failed") ? windowState.isolationStatus : null);
    $("panel-status").textContent = problem || "Device controls are ready";
    $("panel-status").classList.toggle("error", Boolean(problem));
    $("panel-status").hidden = !problem;
    update();
    return true;
  } catch (error) {
    $("panel-status").textContent = error.message;
    $("panel-status").classList.add("error");
    $("panel-status").hidden = false;
    return false;
  } finally { reading = false; if (pendingRefresh) { pendingRefresh = false; refresh(); } }
}
function change(method, args, control = method) {
  queuedChanges++; busy = true; pendingControls.add(control);
  update();
  const run = async () => {
    try {
      while (reading) await new Promise(resolve => setTimeout(resolve, 16));
      // Resolve toggles at execution time from the last confirmed read, not a stale queued click.
      const response = await Native.call(method, typeof args === "function" ? args() : args);
      if (snapshot?.hardware.preferences?.developerMode && response?.message) notice(response.message);
    } catch (error) { notice(error.message); }
    finally {
      while (reading) await new Promise(resolve => setTimeout(resolve, 16));
      await refresh(true);
      pendingControls.delete(control); queuedChanges--; busy = queuedChanges > 0; update();
    }
  };
  const result = changeTail.then(run, run);
  changeTail = result.catch(() => {});
  return result;
}
document.addEventListener("click", event => {
  const button = event.target.closest("button");
  if (!button || button.disabled) return;
  const control = controlKey(button);
  if (button.dataset.mode) change("performance.set", () => ({ value: button.dataset.mode, source: snapshot?.hardware.powerSource }), control);
  else if (button.dataset.limit) change("battery.limit", { value: Number(button.dataset.limit) });
  else if (button.dataset.tool) {
    const tool = QuickTools.find(item => item.id === button.dataset.tool) || (snapshot?.hardware.customization?.quickLinks || []).find(item => item.id === button.dataset.tool && item.showInPanel !== false);
    if (tool) change(tool.method || "app.open", tool.args || { id: tool.id }, control);
  }
  else if (button.dataset.oem) change("xiaomi.open", { section: button.dataset.oem }, control);
  else if (button.dataset.shortcut) Native.call("shortcut.open", { name: button.dataset.shortcut }).catch(error => notice(error.message));
  else if (button.dataset.native) Native.call(button.dataset.native, button.dataset.page ? { page: button.dataset.page } : {}).catch(error => notice(error.message));
  else if (button.dataset.toggle) {
    const h = snapshot?.hardware || {};
    const key = button.dataset.toggle;
    if (key === "travel") change("battery.travel", () => ({ on: !snapshot.hardware.travel }), control);
    if (key === "care") change("battery.care", () => ({ on: !(snapshot.hardware.travel || snapshot.hardware.requestedChargeLimit < 100) }), control);
    if (key === "awake") change("window.awake", () => ({ on: !awake }), control);
    if (key === "sleepoff") change("window.preventSleep", () => ({ on: !sleepOff }), control);
    if (key === "touchpad" || key === "touchscreen") change("input.enabled", () => ({ device: key, on: !snapshot.hardware[key + "Enabled"] }), control);
    if (key === "refresh") change("display.cycle", {}, control);
    if (key === "autorefresh") change("display.automatic", () => ({ on: !snapshot.hardware.preferences.autoRefresh, ac: snapshot.hardware.preferences.acRefreshRate, battery: snapshot.hardware.preferences.batteryRefreshRate }), control);
  }
});
$("refresh-state").addEventListener("click", async () => {
  const control = $("refresh-state");
  control.classList.add("refreshing");
  while (reading) await new Promise(resolve => setTimeout(resolve, 20));
  try { if (await refresh(true)) notice("Device state checked at " + new Date().toLocaleTimeString("en-GB")); }
  finally { control.classList.remove("refreshing"); }
});
$("brightness").addEventListener("input", event => {
  $("brightness-value").textContent = event.target.value + "%";
  BrightnessInput.set(Number(event.target.value), value => {
    if (snapshot) snapshot.hardware.brightness = value;
    $("brightness-value").textContent = value + "%";
  }, notice);
});
document.addEventListener("keydown", event => {
  if (event.key === "Escape") Native.call("window.hide").catch(() => {});
});
Native.listen(message => {
  if (message.appearance) { if (snapshot?.hardware?.preferences) snapshot.hardware.preferences.appearance = message.appearance; UI.applyAppearance(message.appearance); }
  if (Number.isInteger(message.brightness) && !BrightnessInput.pending) {
    if (snapshot) snapshot.hardware.brightness = message.brightness;
    $("brightness").value = String(message.brightness);
    $("brightness-value").textContent = message.brightness + "%";
  }
  if (message.activated) refresh();
  if (message.notice) notice(message.notice);
});
document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
$("quick-tools").innerHTML = QuickTools.map(tool => `<button class="circle-tile" data-tool="${UI.escape(tool.id)}" disabled><span class="circle">${UI.icon(tool.icon)}</span><span>${UI.escape(tool.label)}</span></button>`).join("");
$("quick-tools").hidden = QuickTools.length === 0;
if (!Native.connected) $("panel-status").textContent = "Layout preview · Open the desktop app for controls";
update();
refresh();
setInterval(refresh, 5000);

