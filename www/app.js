/* Purpose: categorized English manager using the resident's XiControl backend.
   Dependencies: bridge.js, trusted local WebView. Outputs: confirmed controls and saved preferences.
   Command: app/PCManager.exe --manager. */
"use strict";
const $ = id => document.getElementById(id);
const pages = { home: ["This PC", "home"], performance: ["Performance", "speed"], battery: ["Battery", "battery"], display: ["Display", "screen"], touchpad: ["Touch and input", "touchpad"], keyboard: ["Keyboard", "keyboard"], notifications: ["Notifications", "message"], monitor: ["Monitor", "cpu"], tools: ["Toolbox", "tools"], settings: ["Settings", "settings"] };
let snapshot, settingState, customization, awake = false, sleepOff = false, busy = false, reading = false, dirty = false, pendingRefresh = false, toastTimer;
let components;
let cachedState = false, lastCacheWrite = 0;
const cacheKey = "manager-last-known-state-v1";
let page = pages[location.hash.slice(1)] ? location.hash.slice(1) : "home";
const h = () => snapshot?.hardware || {};
const prefs = () => h().preferences || {};
const off = supported => !Native.connected || busy || !supported ? " disabled" : "";
const selected = (a, b) => String(a ?? "") === String(b ?? "") ? " selected" : "";
function button(label, action, args = {}, supported = true, secondary = true) { return `<button class="MiButton${secondary ? " secondary" : ""}" data-action="${action}" data-args="${UI.escape(JSON.stringify(args))}"${off(supported)}>${UI.escape(label)}</button>`; }
function card(title, content, icon = "device") { return `<article class="MiCard"><h2 class="card-title">${UI.icon(icon)}${UI.escape(title)}</h2>${content}</article>`; }
function row(label, description, control, help = "", key = "") { return `<div class="MiSettingRow"${key ? ` data-search-key="${UI.escape(key)}"` : ""}><div><div class="setting-label">${UI.escape(label)}${help ? `<span class="setting-help" tabindex="0" aria-label="Help for ${UI.escape(label)}">?<span role="tooltip">${UI.escape(help)}</span></span>` : ""}</div>${description ? `<div class="setting-description">${UI.escape(description)}</div>` : ""}</div><div class="setting-control">${control}</div></div>`; }
function toggle(id, value, supported = true) { return `<label class="MiToggle"><input id="${id}" type="checkbox" aria-label="${UI.escape(id)}"${value ? " checked" : ""}${off(supported)}><span class="toggle-track"></span></label>`; }
function options(values, current) { return values.map(([value, label]) => `<option value="${UI.escape(value)}"${selected(value, current)}>${UI.escape(label)}</option>`).join(""); }
function select(id, values, current, supported = true) { return `<select id="${id}" class="field-select"${off(supported)}>${options(values, current)}</select>`; }
function readout(label, value, icon) { return `<div><div class="readout-label">${UI.escape(label)}</div><div class="readout-value${icon ? " mode-readout" : ""}">${icon ? UI.icon(icon) : ""}${UI.escape(value ?? "Unavailable")}</div></div>`; }
function metric(label, value, detail, icon = "cpu") { return card(label, `<div class="metric-value">${UI.escape(value)}</div><div class="metric-detail">${UI.escape(detail)}</div>`, icon); }
const categoryDefaults = { performance: "Smart on AC and Auto on battery; separate source profiles enabled.", battery: "80% care limit; travel charging off.", display: "50% brightness; lowest supported refresh rate; automatic display policies off.", touchpad: "Input devices enabled; Medium haptics; Standard click force; restore inputs after restart.", keyboard: "Mi opens quick controls; F9 opens Windows Settings; AI and F7 open the newest installed XiaoAI.", notifications: "Bottom-center placement with lock notifications enabled.", monitor: "Large view with battery power as the compact metric.", settings: "Light theme; close to tray; startup and automatic restart enabled; API off. App links and tokens are retained." };
function defaultText(s) { const v = s.defaultValue; if (s.kind === "curve") return "Default: standard light curve."; if (s.kind === "keymap") return "Default: built-in firmware key codes."; if (Array.isArray(v)) return "Default: " + (v.length ? v.join(", ") : "all supported rates") + "."; return "Default: " + (typeof v === "boolean" ? (v ? "On" : "Off") : v === "" ? (s.key === "MonitorView" ? "Large" : "Empty / automatic") : friendly(String(v))) + "."; }
function settingHelp(s) {
  const explanations = {
    RefreshRateFeature: "On: show and allow manual rate selection, automatic AC/battery switching, and rate cycling.\nOff: hide those controls, stop automatic switching, and restore the saved AC rate.",
    TouchpadKeepOff: "On: leave a disabled touchpad off after restart.\nOff: restore it when this app starts again.",
    TouchscreenKeepOff: "On: leave a disabled touchscreen off after restart.\nOff: restore it when this app starts again.",
    HandleScreenshotKey: "On: F7 opens Windows Snipping Tool.\nOff: F7 opens the installed XiaoAI app; the Mi key still opens quick controls.",
    FlyoutTheme: "Choose one theme for the manager, hardware monitor, and notifications. System follows Windows app appearance.",
    AutoBrightnessRevert: "Choose when automatic brightness may return to its learned curve after you move the slider: always, only on battery, or never.",
    AutoBrightnessPointsAc: "Maps measured ambient light to target brightness while plugged in. The horizontal scale is logarithmic so dim-room values remain visible.",
    AutoBrightnessPointsBattery: "Maps measured ambient light to target brightness on battery. The horizontal scale is logarithmic so dim-room values remain visible.",
    ForceAcpiTemperature: "Read the fallback ACPI temperature instead of the preferred Intel DPTF sensor. Use this if DPTF reports no valid reading.",
    TrayMetricPeriodSec: "How often the tray readout samples the selected hardware metric. Shorter intervals use slightly more CPU.",
    MonitorCompactMetric: "The single value shown in the smallest monitor view. It does not change what the large view records.",
    StartupStrategy: "Profiles applies your separate AC and battery modes; Restore uses the last mode; Pin forces one selected mode at startup."
  };
  return [explanations[s.key] || s.description || "", defaultText(s)].filter(Boolean).join("\n");
}
function heading(title, description) { return `<div class="page-heading"><div><h1>${UI.escape(title)}</h1><p>${UI.escape(description)}</p></div><div class="actions">${categoryDefaults[page] ? button("Reset category to defaults", "settings.reset", { group: page }, settingState != null) : ""}${button("Quick controls", "window.quick")}</div></div>`; }
function profiles(id, value, battery = false) { const source = battery ? "Offline" : "Online"; return select(id, [["", "Leave unchanged"], ...UI.modes(source).map(mode => [mode, UI.mode(mode, source)])], value || "", h().mode != null); }
function eta() {
  const result = UI.chargeEstimate(h(), snapshot?.telemetry || {});
  if (result.status !== "Charging") return result.status;
  const minutes = Math.max(1, Math.round(result.minutes));
  const duration = minutes >= 60 ? `${Math.floor(minutes / 60)} h ${minutes % 60} min` : `${minutes} min`;
  return `About ${duration} to ${result.target}%`;
}
function homePage() {
  const d = h(), t = snapshot?.telemetry || {}, power = UI.power(t);
  return heading(d.deviceName || "This PC", d.model || "Your computer") +
    card("Device", `<div class="readout-grid">${readout("Model", d.model)}${readout("BIOS", d.bios)}${readout("Performance", d.mode ? UI.mode(d.mode, d.powerSource) : "Unavailable", d.mode ? UI.modeIcon(d.mode) : null)}${readout("Power source", d.powerSource === "Online" ? "AC" : d.powerSource === "Offline" ? "Battery" : "Unknown")}${readout("Battery", UI.number(d.batteryPercent, "%"))}${readout("Charge estimate", eta())}</div><p class="inline-note">Charging time uses remaining energy and the current battery charge rate. Charging slows near full, so this is an estimate.</p>`) +
    `<div class="grid cols-3">${metric("CPU", UI.number(t.cpuPercent, "%"), "Current processor load")}${metric("Memory", UI.number(t.memoryUsedGiB, " GiB", 1), UI.number(t.memoryTotalGiB, " GiB", 1) + " available capacity")}${metric("Power draw", UI.number(power.watts, " W", 1), power.detail)}</div>` +
    specsCards(d.specs) + (prefs().developerMode ? card("Background manager", `<p class="inline-note">${UI.escape(isolationStatus || "Checking background status")}</p>`) : "");
}
function specsCards(specs) {
  if (!specs) return card("PC specifications", '<p class="inline-note">Reading Windows hardware inventory in the background…</p>');
  const names = { NumberOfCores: "Physical cores", NumberOfLogicalProcessors: "Logical processors", MaxClockSpeed: "Reported maximum clock", L2CacheSize: "L2 cache", L3CacheSize: "L3 cache", ConfiguredClockSpeed: "Configured memory clock", Speed: "Reported speed", LastBootUpTime: "Last Windows boot", OSArchitecture: "Windows architecture", NetConnectionStatus: "Connection status code" };
  const blocks = rows => rows.map(values => `<div class="spec-block"><div class="readout-grid">${Object.entries(values).map(([key, value]) => readout(names[key] || key.replace(/([a-z])([A-Z])/g, "$1 $2"), value)).join("")}</div></div>`).join("") || '<p class="inline-note">Not reported by Windows.</p>';
  return specs.sections.map(section => {
    if (section.title === "Memory modules") {
      const groups = new Map();
      for (const values of section.rows) {
        const key = JSON.stringify(Object.entries(values).filter(([name]) => name !== "DeviceLocator"));
        const group = groups.get(key) || { values, count: 0 }; group.count++; groups.set(key, group);
      }
      const rows = [...groups.values()].map(({ values, count }) => {
        const { DeviceLocator, ...summary } = values;
        if (count > 1) { summary.Modules = String(count); summary.Capacity = (parseFloat(values.Capacity) * count).toFixed(1) + " GiB total"; }
        return summary;
      });
      return card("Memory", blocks(rows) + `<details data-persist="memory"><summary>Module details</summary>${blocks(section.rows)}</details>`);
    }
    if (section.title === "Volumes") {
      const valid = section.rows.filter(r => /GiB$/.test(r.Size) && /GiB$/.test(r.FreeSpace));
      const capacity = valid.reduce((n, r) => n + parseFloat(r.Size), 0), free = valid.reduce((n, r) => n + parseFloat(r.FreeSpace), 0);
      return card("Storage space", `<div class="readout-grid">${readout("Occupied", (capacity - free).toFixed(1) + " GiB")}${readout("Free", free.toFixed(1) + " GiB")}${readout("Volume capacity", capacity.toFixed(1) + " GiB")}</div><details data-persist="volumes"><summary>Volume details</summary>${blocks(section.rows)}</details>`, "device");
    }
    if (section.title === "Storage devices") return `<details data-persist="disks"><summary>Storage device details</summary>${card(section.title, blocks(section.rows))}</details>`;
    return card(section.title, blocks(section.rows));
  }).join("") + (specs.errors.length && prefs().developerMode ? card("Inventory notes", `<p class="inline-note">${UI.escape(specs.errors.join(" "))}</p>`) : "");
}
function performancePage() {
  const d = h(), p = prefs(), icons = { Quiet: "quiet", Auto: "auto", FullSpeed: "speed", Turbo: "speed", Eco: "battery" };
  const modes = UI.modes(d.powerSource).filter(mode => !d.visibleModes || d.visibleModes.includes(mode));
  return heading("Performance", "Separate choices for AC and battery, with actual firmware readback.") +
    card("Current mode", `<div class="MiSegmentedControl profiles">${modes.map(mode => `<button class="segment${d.mode === mode ? " selected" : ""}" aria-pressed="${d.mode === mode}" data-action="performance.set" data-args="${UI.escape(JSON.stringify({ value: mode, source: d.powerSource }))}"${off(d.mode != null && ["Online", "Offline"].includes(d.powerSource))}>${UI.icon(icons[mode])}${UI.escape(UI.mode(mode, d.powerSource))}</button>`).join("")}</div><p class="inline-note">A successful manual selection enables separate AC and battery memory. The selected card follows the firmware reading.</p>`, "speed") +
    card("Power-source profiles", row("On AC", "Applied when you connect the charger.", profiles("ac-mode", p.acMode)) + row("On battery", "Applied after you unplug.", profiles("battery-mode", p.batteryMode, true)), "auto") + catalog("performance");
}
function batteryPage() {
  const d = h();
  return heading("Battery", "Persistent care limits live here. Travel charging remains a quick-panel toggle.") +
    card("Charging", row("Battery-care limit", "The desired target is retained if another controller overwrites the firmware.", select("charge-limit", [40, 50, 60, 70, 80, 100].map(n => [String(n), n === 100 ? "100% · Full charging" : n + "%"]), String(d.requestedChargeLimit ?? d.chargeLimit), d.chargeLimit != null)) +
      row("Travel charging", d.travel ? "Cancel now to restore your saved care target." : "Temporarily allow 100%, then restore care after unplugging.", toggle("travel", d.travel, d.travel || (d.requestedChargeLimit < 100 && d.powerSource === "Online"))) +
      `<div class="readout-grid">${d.chargeConflict ? readout("Firmware conflict", UI.number(d.chargeLimit, "%")) : ""}${readout("Desired target", UI.number(d.requestedChargeLimit, "%"))}${readout("Charge estimate", eta())}${readout((snapshot?.telemetry?.batteryWatts || 0) > 0 ? "Charging power" : "Battery power draw", UI.number(Math.abs(snapshot?.telemetry?.batteryWatts), " W", 1))}</div><p class="inline-note">Battery watts show the magnitude of charging or discharge. The estimate assumes the current rate; it does not model the slower final charging phase.</p>`, "battery") +
    card("Battery details", `<div class="readout-grid">${readout("Charge", UI.number(d.batteryPercent, "%"))}${readout("Health", UI.number(d.batteryHealth, "%"))}${readout("Cycles", UI.number(d.cycles))}${readout("Adapter rating", UI.number(d.adapterWatts, " W"))}${readout("Design capacity", UI.number(d.designWh, " Wh", 1))}${readout("Full capacity", UI.number(d.fullWh, " Wh", 1))}</div>`, "battery") + catalog("battery");
}
function displayPage() {
  const d = h(), p = prefs(), available = d.refreshRates || [], rates = available.map(n => [String(n), n + " Hz"]);
  const rateControl = available.includes(60) && available.includes(120)
    ? `<label class="rate-switch"><span>60 Hz</span><input id="refresh-rate" type="checkbox" aria-label="120 Hz refresh rate"${d.refreshRate === 120 ? " checked" : ""}${off(d.refreshRate != null)}><span class="rate-switch-track"></span><span>120 Hz</span></label>`
    : select("refresh-rate-select", rates, String(d.refreshRate), rates.length > 0);
  return heading("Display", "Brightness and internal-panel display rates.") + card("Display controls",
    row("Brightness", "Changes are coalesced while dragging.", `<span id="brightness-value">${UI.number(d.brightness, "%")}</span><input class="MiSlider" id="brightness" type="range" min="1" max="100" value="${d.brightness || 50}"${off(d.brightness != null)}>`)+
    row("Current refresh rate", "Left is 60 Hz; right is 120 Hz. Other panels use a rate list.", rateControl)+
    row("Automatic refresh rate", "Use a separate rate for each power source.", toggle("auto-refresh", p.autoRefresh, rates.length > 1))+
    row("AC refresh rate", "While plugged in.", select("ac-refresh", rates, String(p.acRefreshRate), rates.length > 1))+
    row("Battery refresh rate", "While unplugged.", select("battery-refresh", rates, String(p.batteryRefreshRate), rates.length > 1))+
    `<div class="actions">${button("Screen off now", "window.screenOff")}</div><p class="inline-note">Uses a temporary one-second Windows display timeout and restores your saved value after the display wakes, a restart, or an unsuccessful request. Prevent sleep requests continued work without holding the OLED on, but this S0 laptop may still enter Modern Standby. Test long-running work before relying on remote access. Windows may lock the session. Keyboard backlight control is unavailable through the confirmed TM2424 interfaces.</p><p class="inline-note">Changes apply automatically.</p>`, "screen") + catalog("display");
}
function touchpadPage() {
  const d = h();
  return heading("Touch and input", "The same HID and Windows controls used by XiControl.") + card("Input devices",
    row("Touchpad", "Keep another input device available before disabling.", toggle("touchpad", d.touchpadEnabled, d.touchpadEnabled != null))+
    row("Touchscreen", "Disabling its HID device makes Xiaomi's driver scanner offer to enable it. No driver is removed.", toggle("touchscreen", d.touchscreenEnabled, d.touchscreenEnabled != null))+
    row("Haptic strength", "Read back from the supported haptic touchpad.", select("vibration", ["Low", "Medium", "High"].map(n => [n, n]), d.vibration, d.hapticsAvailable))+
    row("Click force", "A stored value can differ from these presets.", select("pressure", [...(d.pressure != null && ![100,125,140].includes(d.pressure) ? [[String(d.pressure), "Current: " + d.pressure]] : []), ["100", "Light"], ["125", "Standard"], ["140", "Firm"]], String(d.pressure), d.hapticsAvailable)), "touchpad") + catalog("touchpad");
}
function shortcutActions() { return ["panel", ...Object.keys(pages).map(name => "page." + name), "windowssettings", "modes", "hz", "screenoff", "travel", "touchpad", "touchscreen", "monitor", "owl", "projection", "screenshot", "calc", "xiaoai", ...(h().customization?.quickLinks || []).map(link => "app:" + link.id)]; }
function shortcutEditor() {
  const actions = shortcutActions();
  return card("Custom shortcuts", `<p class="inline-note">Firmware mode and display keys work only if this laptop emits their events. Windows does not expose Fn as a shortcut modifier. You can assign Cycle modes or Cycle display rate to Ctrl+Alt+K / Ctrl+Alt+S instead. Choose an application directly in its shortcut row. Selected apps also appear in quick controls. Completed shortcuts apply automatically; unfinished chords remain drafts. Screen off uses your normal Windows lock and sleep rules.</p><div id="shortcut-list">${(settingState?.shortcuts || []).map(item => shortcutRow(item, actions)).join("")}</div><div class="actions">${button("Add shortcut", "shortcut-add")}</div>${settingState?.shortcutError ? `<p class="error">${UI.escape(settingState.shortcutError)}</p>` : ""}`, "keyboard");
}
function shortcutRow(item, actions) { const selected = item.action === "app" ? "app:" + item.appId : item.action; return `<div class="shortcut-row"><input class="field-input" data-chord placeholder="Ctrl+Alt+K" aria-label="Shortcut keys" value="${UI.escape(item.chord || "")}"><select class="field-select" data-shortcut-action data-committed="${UI.escape(selected)}" aria-label="Shortcut action">${options([...actions.map(n => [n, n.startsWith("app:") ? "Open " + (h().customization?.quickLinks || []).find(l => l.id === n.slice(4))?.label : friendly(n)]), ["pick-app", "Choose an application…"]], selected)}</select><button class="neutral-link" data-shortcut-remove>Remove</button></div>`; }
function copilotEditor() {
  const current = settingState?.copilot || { action: "none" }, links = h().customization?.quickLinks || [];
  const actions = [["system", "Windows default"], ["none", "Do nothing"], ...shortcutActions().map(n => [n, n.startsWith("app:") ? "Open " + links.find(l => l.id === n.slice(4))?.label : friendly(n)])];
  const selected = current.action === "app" ? "app:" + current.appId : current.action;
  return card("Copilot key (optional)", `<p class="inline-note">If this keyboard sends Win+Shift+F23, this resident intercepts it before Windows opens its default action. The mapping is reversible and only works while PC Manager runs. Choose Windows default to give control back to Windows. Other keyboard implementations may not emit this chord.</p><div class="copilot-editor"><span class="field-input copilot-chord">Win + Shift + F23</span><select class="field-select" id="copilot-action" data-committed="${UI.escape(selected)}" aria-label="Copilot key action">${options([...actions, ["pick-app", "Choose an application…"]], selected)}</select></div><p class="inline-note">Detected ${Number(settingState?.copilotInterceptCount || 0)} matching key press(es) this session.</p>${settingState?.copilotError ? `<p class="error">${UI.escape(settingState.copilotError)}</p>` : ""}`, "keyboard");
}
function keyboardPage() {
  const k = settingState?.keys || {};
  const status = !k.enabled ? "Firmware key handling is disabled." : k.running ? "Subscribed directly to Xiaomi firmware events." : "Firmware event subscription is not running.";
  return heading("Keyboard", "Map keys to apps, manager pages or device actions. Mi opens quick controls; F9 opens Windows Settings; AI/F7 opens XiaoAI.") + copilotEditor() + shortcutEditor() + (prefs().developerMode ? card("Key diagnostics", `<p>${UI.escape(status)}</p><p class="inline-note">${UI.escape(k.error || "")}</p><div class="readout-grid">${readout("Last firmware event", k.lastEvent || "No event received yet")}</div>`, "keyboard") : "") + catalog("keyboard");
}
function notificationsPage() { return heading("Notifications", "One position setting for performance and other notifications.") + card("Preview", `<div class="actions">${button("Performance preview", "window.osdPreview", { number: 1 })}${button("Caps Lock preview", "window.lockPreview")}</div><p class="inline-note">Previewing a card does not change the hardware mode or lock state.</p>`, "message") + catalog("notifications"); }
function monitorPage() { return heading("Hardware monitor", "The XiControl monitor, running inside this resident.") + card("Monitor views", `<div class="actions">${["small", "medium", "large"].map(size => button(size[0].toUpperCase()+size.slice(1), "window.monitor", { size })).join("")}</div><p class="inline-note">Visible views sample at 1 Hz. Large graphs show 30 seconds by default; click 30 sec to show three minutes. Log starts a CSV only when checked and turns off when the monitor closes. On AC, power shows CPU package only; full laptop draw is unavailable. NPU load and fan RPM are unavailable.</p>`, "cpu") + catalog("monitor"); }
function toolsPage() {
  const official = [["drivers", "Driver scan", "Open the supplied Xiaomi driver scanner."], ["tools", "Xiaomi Toolbox", "Cleanup, driver management and OEM tools."], ["home", "Original Xiaomi Manager", "Display color, content-aware brightness, meetings, repair and other proprietary settings."], ["store", "Xiaomi Store", "Open the standalone Xiaomi Store when available."]];
  return heading("Toolbox", "Only explicit OEM actions restore and open Xiaomi software.") + official.map(([section, title, description]) => card(title, `<p class="inline-note">${UI.escape(description)}</p>${button("Open", "xiaomi.open", { section })}`, "tools")).join("") +
    card("Meeting subtitles", `<p class="inline-note">Xiaomi hosts this in its original quick panel through SubtitleTranscriptor.dll. Open that panel and choose Subtitles Translation. A Xiaomi account is required; microphone/system audio is sent to Xiaomi for transcription and translation.</p>${button("Open original quick panel", "xiaomi.open", { section: "popup" })}`, "translate")+
    card("Official downloads and support", `<div class="actions">${button("Driver website", "official.open", { page: "drivers" })}${button("Official help", "official.open", { page: "help" })}${button("Windows display", "windows.open", { page: "display" })}${button("Windows touchpad", "windows.open", { page: "touchpad" })}${button("Windows sound", "windows.open", { page: "sound" })}</div><p class="inline-note">With automatic cleanup enabled, closing Xiaomi's main window returns to daily isolation after any installer has ended.</p>`, "update");
}
function friendly(value) { if (value === "xiaoai") return "Open XiaoAI"; if (["blue", "red", "pink", "green", "custom", "xiaomi", "xicontrol"].includes(value)) return ({ xiaomi: "Xiaomi", xicontrol: "XiControl" })[value] || value[0].toUpperCase() + value.slice(1); if (value.startsWith("page.")) return "Open " + (pages[value.slice(5)]?.[0] || value.slice(5)); if (value === "app") return "Choose Windows app…"; return ({ "": "Default", None: "Leave unchanged", Profiles: "Separate AC/battery profiles", Restore: "Restore the last mode", Pin: "Pin a startup mode", power: "Battery / fallback package power", mini: "Medium", temp: "Temperature", ram: "Memory", owl: "Stay awake", modes: "Cycle modes", panel: "Open quick panel", settings: "Open manager settings", windowssettings: "Open Windows Settings", touchscreen: "Toggle touchscreen", touchpad: "Toggle touchpad", projection: "Windows projection", copilot: "Windows Copilot", charge: "Switch battery care / full charging", monitor: "Open hardware monitor", screenoff: "Turn screen off", screenshot: "Take screenshot", taskview: "Windows task view", calc: "Open calculator", play: "Play / pause media", next: "Next track", prev: "Previous track", stop: "Stop media", launch: "Custom command", autobright: "Automatic brightness", hz: "Cycle display rate", none: "No action", travel: "Travel charging", battery: "Only on battery", off: "Off" }[value]) || value.replace(/([a-z])([A-Z])/g, "$1 $2"); }
function settingControl(s) {
  if (s.kind === "text" && s.key.endsWith("Command")) {
    const slot = s.key.slice(0, -7), action = settingState.rows.find(r => r.key === slot + "Action")?.value;
    if (action === "app") {
      const link = settingState.keyApps?.[slot];
      return `<div><p class="small break-path">${UI.escape(link ? link.label + " · " + link.path : "No app selected")}</p>${button("Choose app…", "settings.keyApp", { slot })}</div>`;
    }
  }
  const attr = `data-setting="${UI.escape(s.key)}" aria-label="${UI.escape(s.label)}"${off(settingState != null && (s.key !== "AutoBrightness" || settingState.alsAvailable || s.value))}`;
  if (s.kind === "toggle") return `<label class="MiToggle"><input type="checkbox" ${attr}${s.value ? " checked" : ""}><span class="toggle-track"></span></label>`;
  if (s.kind === "choice") return `<select class="field-select" ${attr}>${options(s.options.map(n => [n, s.key === "MonitorView" && n === "" ? "Large" : s.key === "AutoBrightnessRevert" && n === "" ? "Always" : friendly(n)]), s.value)}</select>`;
  if (s.kind === "number" || s.kind === "decimal") return `<input class="field-input number-field" type="number" min="${s.min}" max="${s.max}" step="${s.kind === "decimal" ? "0.01" : "1"}" value="${s.value ?? ""}" ${attr}>`;
  if (s.kind === "text") return `<input class="field-input" type="text" maxlength="1024" value="${UI.escape(s.value || "")}" ${attr}>`;
  if (s.kind === "curve") return `<div class="curve-editor" data-curve="${UI.escape(s.key)}"><div class="curve-chart" aria-label="Brightness by ambient light">${curveChart(s.value || [])}</div><div class="curve-points"><table><thead><tr><th>Light (lux)</th><th>Brightness (%)</th><th></th></tr></thead><tbody>${[...(s.value || [])].sort((a, b) => a.lux - b.lux).map(p => curvePoint(p.lux, p.percent)).join("")}</tbody></table><div class="actions">${button("Add point", "curve-add", { key: s.key })}${button("Reset this curve", "curve-reset", { key: s.key })}</div></div></div>`;
  return `<textarea class="field-input json-field" rows="3" data-json-setting="${UI.escape(s.key)}" aria-label="${UI.escape(s.label)}"${off(settingState != null)}>${UI.escape(JSON.stringify(s.value ?? (s.kind === "keymap" ? {} : []), null, 2))}</textarea>`;
}
function curvePoint(lux = 0, percent = 50) { return `<tr><td><input type="number" min="0" max="200000" value="${lux}" aria-label="Light level"></td><td><input type="number" min="1" max="100" value="${percent}" aria-label="Brightness percent"></td><td><button class="neutral-link" data-curve-remove>Remove</button></td></tr>`; }
function curveChart(points) {
  const valid = points.filter(p => Number.isFinite(+p.lux) && Number.isFinite(+p.percent)).map(p => ({ lux: Math.max(0, +p.lux), percent: Math.max(0, Math.min(100, +p.percent)) })).sort((a, b) => a.lux - b.lux);
  const maxLux = Math.max(100, ...valid.map(p => p.lux)), x = lux => 42 + Math.log1p(lux) / Math.log1p(maxLux) * 464, y = percent => 210 - percent * 1.9;
  const grid = [0,25,50,75,100].map(v => `<line x1="42" y1="${y(v)}" x2="506" y2="${y(v)}"/><text x="34" y="${y(v)+4}" text-anchor="end">${v}</text>`).join("");
  const ticks = [0,10,100,1000,maxLux].filter((v, i, all) => all.indexOf(v) === i && v <= maxLux).map(v => `<text x="${x(v)}" y="238" text-anchor="middle">${v}</text>`).join("");
  const line = valid.length > 1 ? `<polyline class="curve-line" points="${valid.map(p => `${x(p.lux)},${y(p.percent)}`).join(" ")}"/>` : "";
  return `<svg viewBox="0 0 540 250" role="img" aria-label="Ambient light in lux versus target brightness in percent"><g class="curve-grid">${grid}${ticks}</g>${line}${valid.map(p => `<circle class="curve-dot" cx="${x(p.lux)}" cy="${y(p.percent)}" r="4"/>`).join("")}</svg><div class="curve-axis">Ambient light (lux) <span>Target brightness (%)</span></div>`;
}
function renderCurvePreview(editor) {
  const points = Array.from(editor.querySelectorAll("tbody tr"), node => ({ lux: Number(node.querySelectorAll("input")[0].value), percent: Number(node.querySelectorAll("input")[1].value) }));
  editor.querySelector(".curve-chart").innerHTML = curveChart(points);
}
function catalog(group) {
  if (!settingState) return `<p class="inline-note">${UI.escape(settingsError || "Loading settings backend…")}</p>`;
  const fields = settingState.rows.filter(s => s.group === group && (prefs().developerMode || !s.key.startsWith("Api."))), advanced = fields.filter(s => /Ramp|Converge|Backoff|Divisor|Snap|Deadband|Settle|LearnMs|Hysteresis|LearnBlend|FineStep|RevertMs/.test(s.key));
  const ordinary = fields.filter(s => !advanced.includes(s) && (!s.key.endsWith("Command") || ["launch", "app"].includes(fields.find(r => r.key === s.key.slice(0, -7) + "Action")?.value)));
  const content = values => values.map(s => row(s.key.endsWith("Command") && fields.find(r => r.key === s.key.slice(0, -7) + "Action")?.value === "app" ? "Selected app" : s.label, "", settingControl(s), settingHelp(s), s.key)).join("");
  return card("Preferences", content(ordinary), pages[group]?.[1] || "settings") + (advanced.length ? `<details data-persist="tuning"><summary>Brightness response tuning</summary>${card("Response tuning", content(advanced), "screen")}</details>` : "");
}
function customizationPage() {
  const c = customization || {}, icons = c.icons || [], position = ["TopLeft","Top","TopRight","Left","Center","Right","BottomLeft","Bottom","BottomRight"];
  const selectedOrder = (c.quickSystemActions || DefaultQuickSystemActions).filter(id => QuickSystemTools.some(tool => tool.id === id));
  const actions = new Set(selectedOrder);
  const orderedTools = [...selectedOrder.map(id => QuickSystemTools.find(tool => tool.id === id)), ...QuickSystemTools.filter(tool => !actions.has(tool.id))];
  const systemChoices = `<div class="system-pick-grid" id="system-pick-grid">${orderedTools.map(tool => {
    const rank = selectedOrder.indexOf(tool.id);
    const move = (direction, label, disabled) => `<button type="button" class="system-order-button" data-action="system-move" data-args="${UI.escape(JSON.stringify({ id: tool.id, direction }))}" aria-label="Move ${UI.escape(tool.label)} ${label}" title="Move ${label}"${disabled ? " disabled" : ""}>${direction < 0 ? "↑" : "↓"}</button>`;
    return `<div class="system-pick" data-system-choice="${tool.id}"><label><input type="checkbox" data-quick-action="${tool.id}" ${actions.has(tool.id) ? "checked" : ""}><span>${UI.icon(tool.icon)}${UI.escape(tool.label)}</span></label><span class="system-order">${rank < 0 ? "" : move(-1, "up", rank === 0) + move(1, "down", rank === selectedOrder.length - 1)}</span></div>`;
  }).join("")}</div>`;
  const links = (c.quickLinks || []).map(link => `<div class="app-link-edit" data-link="${UI.escape(link.id)}"><label class="link-visible"><input type="checkbox" data-link-visible ${link.showInPanel !== false ? "checked" : ""}> Show in quick panel</label><input class="field-input" data-link-label value="${UI.escape(link.label)}" maxlength="50" aria-label="App or file label"><select class="field-select" data-link-icon aria-label="App icon">${options([["app", "App / custom PNG"], ...icons.map(n => [n, friendly(n)])], link.icon)}</select><span class="small break-path">${UI.escape(link.path)}</span><div class="actions">${button("Choose PNG", "settings.appIcon", { id: link.id })}${button("Move up", "link-up", { id: link.id })}${button("Remove link", "link-remove", { id: link.id })}</div></div>`).join("");
  return card("Quick panel customization", row("Position", "On the screen containing the pointer, clamped to its work area.", select("panel-position", position.map(n => [n, friendly(n)]), c.popupPosition || "BottomRight"))+
    row("Horizontal offset", "Logical pixels; positive moves right.", `<input id="panel-x" class="field-input number-field" type="number" min="-2000" max="2000" value="${c.popupOffsetX || 0}">`)+
    row("Vertical offset", "Logical pixels; positive moves down.", `<input id="panel-y" class="field-input number-field" type="number" min="-2000" max="2000" value="${c.popupOffsetY || 0}">`)+
    row("Panel scale (%)", "85% is the default.", `<input id="panel-scale" class="field-input number-field" type="number" min="75" max="110" value="${c.popupScale || 85}">`)+
    row("Compact layout", "Reduce spacing while keeping the same controls.", toggle("panel-compact", c.compactPanel !== false))+
    row("Opening and closing animation", "A short fade and movement while opening or dismissing the panel; idle animations are not used.", toggle("panel-animations", c.popupAnimations !== false))+
    `<h3>System controls</h3><p class="inline-note">Choose up to eight controls. Move selected controls up or down to set their order in the quick panel.</p>${systemChoices}<details data-persist="icons"><summary>Change built-in button icons</summary>${icons.map(n => row(friendly(n), "", `<select class="field-select" data-panel-icon="${n}">${options(icons.map(icon => [icon, friendly(icon)]), c.quickIcons?.[n] || n)}</select>`)).join("")}${button("Restore button icons", "icons-default")}</details><h3>App and file links</h3><p class="inline-note">Show up to four links below the system controls. Other saved links remain available for keyboard shortcuts.</p>${links}<div class="actions">${button("Add app or file", "settings.addApp")}${button("Add folder", "settings.addFolder")}</div><p class="inline-note">Changes apply automatically. Links open through your normal Windows desktop. Removing a link preserves its copied icon file.</p>`, "tools");
}
function settingsPage() {
  const p = prefs();
  const palette = UI.palette(p);
  const presetOptions = ["blue","red","pink","green","custom"].map(n => [n, friendly(n)]).concat((p.themePalettes || []).map(saved => [saved.id, saved.name]));
  const selectedPalette = (p.themePalettes || []).find(saved => saved.id === p.themePreset);
  const savePalette = `<span id="palette-save" class="palette-save"${p.themePreset !== "custom" ? " hidden" : ""}><input id="palette-name" class="field-input" type="text" maxlength="40" placeholder="Palette name" aria-label="New palette name">${button("Save preset", "save-palette")}</span>`;
  const managePalette = `<span id="palette-manage" class="palette-save"${selectedPalette ? "" : " hidden"}>${button("Rename", "begin-rename-palette")}${button("Delete", "delete-palette")}</span>`;
  const renameEditor = `<span id="palette-rename-editor" class="palette-save" hidden><input id="palette-rename" class="field-input" type="text" maxlength="40" value="${UI.escape(selectedPalette?.name || "")}" aria-label="Saved palette name">${button("Save name", "rename-palette")}${button("Cancel", "cancel-rename-palette")}</span>`;
  return heading("Settings", "Appearance, quick-panel layout and background behavior.") + card("Appearance",
    row("Theme", "Applies immediately to both windows.", select("appearance", [["light","Light"],["dark","Dark"],["system","Follow Windows"]], p.appearance || "light"))+
    row("Color preset", "Built-in palettes adapt to the light or dark theme. Save edited colors under a new name. Select a saved palette to rename or delete it.", `<span class="palette-picker">${select("theme-preset", presetOptions, p.themePreset || "blue")}${savePalette}${managePalette}${renameEditor}</span>`)+
    row("Accent", "", `<input id="theme-accent" type="color" value="${palette.accent}" aria-label="Accent">`)+
    row("Background", "", `<input id="theme-background" type="color" value="${palette.background}" aria-label="Background">`)+
    row("Card color", "Choose readable colors with enough text contrast.", `<input id="theme-surface" type="color" value="${palette.surface}" aria-label="Card color">`)+
    row("Notification style", "Original Xiaomi cards where available; XiControl is an alternative preset.", select("osd-style", [["xiaomi","Xiaomi original"],["xicontrol","XiControl"]], p.osdStyle || "xiaomi"))+
    row("Profile picture", "Only changes the picture in the two windows.", button("Choose picture…", "settings.profile")+button("Reset picture", "settings.profileReset"))+
    row("Asset pack", "Import PNG button icons, Xiaomi OSD images, an app logo or icon, and optional custom CSS. Native icons and OSD changes appear after restart.", button("Import asset pack…", "settings.assetPack"))+
    row("Developer mode", "Show routine request messages and diagnostic details.", toggle("developer-mode", p.developerMode))+
    row("Also open the original Xiaomi popup", "Optional OEM session. Our popup moves left; cleanup follows when the original closes.", toggle("original-popup", p.originalPopupEnabled))+
    `<p class="inline-note">All three colors preview and save immediately. Editing a color switches to Custom; Save preset stores it as a named palette.</p>`, "settings")+
    card("Background behavior", row("Prevent sleep", "On AC, request continued background work while the display can turn off. On battery Modern Standby, Windows can end this request about five minutes after the sleep timeout. Lid close and explicit Sleep still work.", toggle("sleepoff", sleepOff)) + row("Stay awake", "XiControl wake control. Depending on its display setting, this can also keep the screen on; use Prevent sleep to let the OLED turn off.", toggle("awake", awake)) + row("Minimize to tray", "Also hide the large manager when minimized from its button or the taskbar. Off keeps it on the taskbar.", toggle("minimize-tray", p.minimizeToTray)) + `<p class="inline-note">Closing either window keeps the app in the tray. Startup at sign-in: ${settingState?.startupRegistered ? "registered" : "not confirmed"}.</p>`)+
    card("Settings backup", `<p class="inline-note">Export a ZIP with settings, profile picture and custom icons or artwork to a location you choose. Keep it outside app data so it survives uninstall. Import replaces current settings and restarts PC Manager. The file may contain personal app paths, so keep it private.</p><div class="actions">${button("Export backup…", "settings.backupExport", {}, settingState != null)}${button("Import backup…", "settings.backupImport", {}, settingState != null)}${button("Save local snapshot", "settings.snapshot", {}, settingState != null)}</div>`, "settings")+
    customizationPage() + card("Optional Xiaomi apps", [["manager", "Xiaomi PC Manager", "Driver scan and original manager"], ["store", "Xiaomi Store", "Standalone app store"], ["ai", "XiaoAI", "AI/F7 key action"]].map(([kind, label, use]) =>
      row(label, `${use}. ${components?.[kind] ? "Installed path found." : "Component not installed or not found."}`,
        `<div class="companion-control"><p class="small break-path">${UI.escape(components?.[kind] || "No executable found")}</p>${button("Choose executable…", "settings.selectXiaomi", { kind })}</div>`)).join("")+
      `<p class="inline-note">Missing apps do not disable the resident controls. Opening a missing shortcut also offers a file picker.</p>`, "tools")+
    catalog("settings") + (p.developerMode ? card("HTTP API token", `<p class="inline-note">The API requires a bearer token; only its hash is stored.</p>${button("Generate new token", "settings.apiToken")}<output id="api-token" class="break-path"></output>`) : "");
}
let isolationStatus = "", settingsError = "";
const openDetails = new Set();
function render() {
  document.querySelectorAll("details[data-persist]").forEach(node => { if (node.open) openDetails.add(node.dataset.persist); else openDetails.delete(node.dataset.persist); });
  UI.applyAppearance(prefs().appearance);
  UI.applyPreferences(prefs());
  UI.quickIcons = h().customization?.quickIcons || {};
  $("navigation").innerHTML = Object.entries(pages).map(([key,[label,icon]]) => `<button class="nav-button${key === page ? " active" : ""}" data-page="${key}">${UI.icon(icon)}${label}</button>`).join("");
  $("page").innerHTML = ({ home: homePage, performance: performancePage, battery: batteryPage, display: displayPage, touchpad: touchpadPage, keyboard: keyboardPage, notifications: notificationsPage, monitor: monitorPage, tools: toolsPage, settings: settingsPage }[page])();
  document.querySelectorAll("details[data-persist]").forEach(node => { node.open = openDetails.has(node.dataset.persist); });
  $("connection").textContent = Native.connected ? cachedState ? "Last known state · updating" : snapshot ? "Device connected" : "Connecting" : "Layout preview";
  $("connection").classList.toggle("connected", Boolean(snapshot));
  const conflict = h().chargeConflict ? `Charging conflict: desired ${h().requestedChargeLimit}%, firmware ${h().chargeLimit}%. The resident will retry; check OEM isolation if this persists.` : h().modeConflict ? "Another controller changed the performance mode. The resident will retry the saved selection." : null;
  $("status-banner").hidden = Native.connected && !cachedState && !h().firmwareError && !conflict;
  $("status-banner").textContent = !Native.connected ? "Layout preview. Open the desktop app for device controls." : h().firmwareError || conflict || (cachedState ? "Showing last known values while the resident updates this page." : "");
}
function showPage(name) {
  document.querySelectorAll("details[data-persist]").forEach(node => { node.open = false; }); openDetails.clear();
  const target = ({ device: "display", official: "tools", ai: "tools", updates: "tools" }[name]) || name;
  page = pages[target] ? target : "home"; dirty = false; location.hash = page; render(); refresh(true);
  const content = document.querySelector("main"); if (content) content.scrollTop = 0;
  globalThis.scrollTo?.(0, 0);
}
let searchHits = [];
function searchSettings(query) {
  const aliases = { fps: "refresh rate", hz: "refresh rate", luminosity: "brightness", light: "brightness", charger: "battery", charging: "battery", fan: "performance", speed: "performance", sleep: "prevent sleep", drivers: "driver", apps: "shortcuts", gesture: "touchpad", display: "screen" };
  const words = query.toLowerCase().trim().split(/\s+/).filter(Boolean);
  if (!words.length) return [];
  const terms = [...words, ...words.map(word => aliases[word]).filter(Boolean)];
  const direct = [
    ["display", "Brightness", "#brightness", "luminosity screen light"],
    ["display", "Refresh rate", "#refresh-rate, #refresh-rate-select", "60 120 hz fps automatic"],
    ["battery", "Battery-care limit", "#charge-limit", "charging target percentage"],
    ["battery", "Travel charging", "#travel", "full charge 100 percent"],
    ["touchpad", "Touchscreen", "#touchscreen", "touch screen hid input"],
    ["touchpad", "Touchpad", "#touchpad", "trackpad input"],
    ["settings", "Prevent sleep", "#sleepoff", "stay running screen off remote work"],
    ["settings", "Appearance", "#appearance", "theme dark light color"],
    ["tools", "Driver scan", "", "update repair firmware xiaomi"],
    ["tools", "Xiaomi Store", "", "shop app store"],
  ].map(([targetPage, label, selector, hints]) => ({ page: targetPage, label, selector, hints }));
  const entries = [...direct, ...Object.entries(pages).map(([targetPage, [label]]) => ({ page: targetPage, label, selector: "", hints: label })),
    ...(settingState?.rows || []).filter(s => pages[s.group] && (prefs().developerMode || !s.key.startsWith("Api."))).map(s => ({ page: s.group, label: s.label, key: s.key, hints: s.key + " " + (s.description || "") }))];
  return entries.map(entry => {
    const title = entry.label.toLowerCase(), body = (entry.hints + " " + pages[entry.page][0]).toLowerCase();
    const score = terms.reduce((sum, term) => sum + (title.includes(term) ? 8 : body.includes(term) ? 2 : 0), 0) + (title.startsWith(query.toLowerCase()) ? 5 : 0);
    return { ...entry, score };
  }).filter(entry => entry.score > 0).sort((a, b) => b.score - a.score || a.label.localeCompare(b.label)).slice(0, 8);
}
function renderSearch() {
  const query = $("setting-search").value.trim(), results = $("setting-search-results");
  searchHits = query ? searchSettings(query) : [];
  results.hidden = !query;
  results.innerHTML = searchHits.length ? searchHits.map((hit, index) => `<button class="search-hit" data-search-result="${index}" role="option">${UI.escape(hit.label)}<small>${UI.escape(pages[hit.page][0])}</small></button>`).join("") : `<div class="search-empty">${settingState ? "No matching setting" : "Loading settings…"}</div>`;
}
function openSearchHit(hit) {
  $("setting-search-results").hidden = true;
  $("setting-search").value = "";
  showPage(hit.page);
  if (hit.selector || hit.key) setTimeout(() => {
    const target = hit.key ? document.querySelector(`[data-search-key="${CSS.escape(hit.key)}"]`) : document.querySelector(hit.selector);
    target?.scrollIntoView({ behavior: "smooth", block: "center" });
  }, 250);
}
function notice(message, error = false) { $("toast").textContent = message; $("toast").hidden = false; $("toast").classList.toggle("error", error); clearTimeout(toastTimer); toastTimer = setTimeout(() => { $("toast").hidden = true; }, 8000); }
async function refresh(force = false) {
  if (!Native.connected || busy || BrightnessInput.pending || document.hidden) return false;
  if (reading) { if (force) pendingRefresh = true; return false; }
  if (!force && (dirty || document.activeElement.matches("input,select,textarea"))) return false;
  reading = true;
  try {
    const state = await Native.call(page === "home" || page === "battery" ? "state.read" : "quick.read");
    // Quick reads omit inventory, and a newly started WMI scan can return null.
    // Keep the last complete inventory visible until a newer one is ready.
    const specs = state.hardware?.specs ?? snapshot?.hardware?.specs ?? null;
    snapshot = { ...snapshot, ...state, hardware: { ...snapshot?.hardware, ...state.hardware, specs } };
    const w = await Native.call("window.state"); awake = w.awake; sleepOff = w.preventSleep; isolationStatus = w.isolationStatus;
    if (!settingState || force || page === "keyboard") { try { settingState = await Native.call("settings.read"); settingsError = ""; } catch (error) { settingsError = error.message; } }
    if (page === "settings" && (!customization || force)) customization = await Native.call("settings.customization");
    if ((page === "settings" || page === "tools") && (!components || force)) components = await Native.call("xiaomi.components");
    updateHistory(await Native.call("settings.history"));
    cachedState = false;
    $("last-updated").textContent = "Updated " + new Date().toLocaleTimeString("en-GB"); if (force || !dirty) render();
    if (Date.now() - lastCacheWrite > 30000 || force) {
      try { localStorage.setItem(cacheKey, JSON.stringify({ snapshot, settingState, customization, savedAt: Date.now() })); lastCacheWrite = Date.now(); } catch { /* Keep live state if browser storage is unavailable. */ }
    }
    if ($("setting-search").value) renderSearch();
    return true;
  } catch (error) { notice(error.message, true); return false; }
  finally { reading = false; if (pendingRefresh) { pendingRefresh = false; refresh(true); } }
}
let writeTail = Promise.resolve(), pendingWrites = 0;
function updateHistory(state = {}) {
  for (const [id, label, chord] of [["undo", state.undoLabel, "Ctrl+Z"], ["redo", state.redoLabel, "Ctrl+Y"]]) {
    $(id).disabled = id === "undo" ? !state.canUndo : !state.canRedo;
    $(id).title = label ? `${id === "undo" ? "Undo" : "Redo"} ${label} (${chord})` : `Nothing to ${id} (${chord})`;
    $(id).setAttribute("aria-label", $(id).title);
  }
}
function change(method, args = {}) {
  busy = true; pendingWrites++;
  const task = writeTail.then(async () => {
  let result, failed = false;
  try { result = await Native.call(method, args); if (result?.message && (method === "settings.snapshot" || method === "settings.backupExport" || method === "settings.backupImport" || prefs().developerMode || result.complete === false)) notice(result.message); }
  catch (error) { failed = true; notice(error.message, true); }
  finally { if (--pendingWrites === 0) { busy = false; dirty = failed; if (!failed) await refresh(true); } }
  if (result?.token && $("api-token")) { $("api-token").textContent = result.token; dirty = true; }
  });
  writeTail = task.catch(error => notice(error.message, true)); return task;
}
function performAction(action, args = {}) {
  if (action === "page") return showPage(args.page);
  if (action === "save-display") return change("display.automatic", { on: $("auto-refresh").checked, ac: Number($("ac-refresh").value), battery: Number($("battery-refresh").value) });
  if (action === "save-profiles") return change("performance.profiles", { ac: $("ac-mode").value || null, battery: $("battery-mode").value || null });
  if (action === "save-appearance") return change("settings.appearance", { preset: $("theme-preset").value, accent: $("theme-accent").value, background: $("theme-background").value, surface: $("theme-surface").value, osdStyle: $("osd-style").value, developerMode: $("developer-mode").checked, originalPopup: $("original-popup").checked });
  if (action === "save-palette") {
    const name = $("palette-name").value.trim();
    if (!name) return notice("Enter a name before saving this palette.", true);
    return change("settings.paletteSave", { name });
  }
  if (action === "begin-rename-palette") {
    const palette = (prefs().themePalettes || []).find(saved => saved.id === $("theme-preset").value);
    if (!palette) return notice("Select a saved palette to rename.", true);
    $("palette-rename").value = palette.name;
    $("palette-manage").hidden = true;
    $("palette-rename-editor").hidden = false;
    $("palette-rename").focus?.();
    return;
  }
  if (action === "cancel-rename-palette") {
    $("palette-rename-editor").hidden = true;
    $("palette-manage").hidden = false;
    return;
  }
  if (action === "rename-palette") {
    const id = $("theme-preset").value, name = $("palette-rename").value.trim();
    if (!name) return notice("Enter a name before renaming this palette.", true);
    return change("settings.paletteRename", { id, name });
  }
  if (action === "delete-palette") {
    const id = $("theme-preset").value;
    const palette = (prefs().themePalettes || []).find(saved => saved.id === id);
    if (!palette) return notice("Select a saved palette to delete.", true);
    if (!confirm(`Delete the saved palette “${palette.name}”? Its current colors will remain as Custom.`)) return;
    return change("settings.paletteDelete", { id });
  }
  if (action === "shortcut-add") { $("shortcut-list").insertAdjacentHTML("beforeend", shortcutRow({ action: "panel" }, shortcutActions())); dirty = true; return; }
  if (action === "save-copilot") {
    const value = $("copilot-action").value;
    return change("settings.copilot", { action: value.startsWith("app:") ? "app" : value, appId: value.startsWith("app:") ? value.slice(4) : null });
  }
  if (action === "shortcut-save") {
    const shortcuts = Array.from(document.querySelectorAll(".shortcut-row"), node => { const value = node.querySelector("[data-shortcut-action]").value; return { chord: node.querySelector("[data-chord]").value, action: value.startsWith("app:") ? "app" : value, appId: value.startsWith("app:") ? value.slice(4) : null }; });
    if (shortcuts.some(item => !item.chord.trim())) return; // Keep incomplete editor rows; never erase the live bindings.
    return change("settings.shortcuts", { shortcuts });
  }
  if (action === "icons-default") { document.querySelectorAll("[data-panel-icon]").forEach(node => { node.value = node.dataset.panelIcon; }); return performAction("save-panel"); }
  if (action === "system-move") {
    const selected = Array.from(document.querySelectorAll("#system-pick-grid [data-system-choice]"), node => node.querySelector("[data-quick-action]")?.checked ? node : null).filter(Boolean);
    const rank = selected.findIndex(node => node.dataset.systemChoice === args.id);
    const neighbor = selected[rank + args.direction];
    if (rank < 0 || !neighbor) return;
    if (args.direction < 0) neighbor.before(selected[rank]); else selected[rank].before(neighbor);
    return performAction("save-panel");
  }
  if (action === "save-panel") {
    const icons = Object.fromEntries(Array.from(document.querySelectorAll("[data-panel-icon]"), node => [node.dataset.panelIcon, node.value]));
    const actions = Array.from(document.querySelectorAll("[data-quick-action]:checked"), node => node.dataset.quickAction);
    const links = Array.from(document.querySelectorAll("[data-link]"), node => ({ id: node.dataset.link, label: node.querySelector("[data-link-label]").value, icon: node.querySelector("[data-link-icon]").value, showInPanel: node.querySelector("[data-link-visible]").checked }));
    return change("settings.customize", { position: $("panel-position").value, x: Number($("panel-x").value), y: Number($("panel-y").value), scale: Number($("panel-scale").value), compact: $("panel-compact").checked, animations: $("panel-animations").checked, icons, actions, links });
  }
  if (action === "link-remove" || action === "link-up") {
    const node = Array.from(document.querySelectorAll("[data-link]")).find(item => item.dataset.link === args.id);
    if (action === "link-remove") node.remove(); else if (node.previousElementSibling?.hasAttribute("data-link")) node.parentNode.insertBefore(node, node.previousElementSibling); return performAction("save-panel");
  }
  if (action === "curve-add" || action === "curve-save") {
    const editor = Array.from(document.querySelectorAll("[data-curve]")).find(node => node.dataset.curve === args.key);
    if (action === "curve-add") { const last = editor.querySelector("tr:last-child input")?.value || 0; editor.querySelector("tbody").insertAdjacentHTML("beforeend", curvePoint(Math.min(200000, Math.max(1, Number(last) * 2)), 50)); renderCurvePreview(editor); return performAction("curve-save", args); }
    const value = Array.from(editor.querySelectorAll("tbody tr"), node => ({ lux: Number(node.querySelectorAll("input")[0].value), percent: Number(node.querySelectorAll("input")[1].value) }));
    renderCurvePreview(editor);
    return change("settings.apply", { key: args.key, value });
  }
  if (action === "curve-reset") {
    const setting = settingState.rows.find(item => item.key === args.key && item.kind === "curve");
    if (setting) return change("settings.apply", { key: args.key, value: setting.defaultValue });
  }
  if (action === "json-save") { try { const field = Array.from(document.querySelectorAll("[data-json-setting]")).find(node => node.dataset.jsonSetting === args.key); return change("settings.apply", { key: args.key, value: JSON.parse(field.value) }); } catch { return notice("Incomplete JSON. Finish editing to apply it.", true); } }
  change(action, args);
}
document.addEventListener("click", event => {
  const control = event.target.closest("button"); if (!control || control.disabled) return;
  if (control.dataset.searchResult != null) return openSearchHit(searchHits[Number(control.dataset.searchResult)]);
  if (control.hasAttribute("data-curve-remove")) { const key = control.closest("[data-curve]").dataset.curve; control.closest("tr").remove(); return performAction("curve-save", { key }); }
  if (control.hasAttribute("data-shortcut-remove")) { control.closest(".shortcut-row").remove(); return performAction("shortcut-save"); }
  if (control.dataset.page) return showPage(control.dataset.page);
  const action = control.dataset.action; if (action) performAction(action, JSON.parse(control.dataset.args || "{}"));
});
document.addEventListener("input", event => {
  if (event.target.id === "setting-search") return renderSearch();
  if (event.target.closest?.("[data-curve]")) renderCurvePreview(event.target.closest("[data-curve]"));
  if (event.target.id !== "brightness") { dirty = true; return; }
  $("brightness-value").textContent = event.target.value + "%";
  BrightnessInput.set(Number(event.target.value), value => { if (snapshot) snapshot.hardware.brightness = value; if ($("brightness-value")) $("brightness-value").textContent = value + "%"; }, message => notice(message, true));
});
async function chooseShortcutApp(select, saveAction) {
  const previous = select.dataset.committed || "none";
  try {
    const link = await Native.call("settings.shortcutApp");
    if (!link?.id) { select.value = previous; return; }
    const value = "app:" + link.id;
    if (!Array.from(select.options).some(option => option.value === value)) select.add(new Option("Open " + link.label, value));
    select.value = value;
    select.dataset.committed = value;
    performAction(saveAction);
  } catch (error) { select.value = previous; notice(error.message, true); }
}
document.addEventListener("change", event => {
  const node = event.target, { id, value, checked } = node;
  if (node.dataset.quickAction && checked && document.querySelectorAll("[data-quick-action]:checked").length > 8) { node.checked = false; return notice("Choose at most eight system controls.", true); }
  if (node.hasAttribute?.("data-link-visible") && checked && document.querySelectorAll("[data-link-visible]:checked").length > 4) { node.checked = false; return notice("Show at most four app or file links.", true); }
  if (node.dataset.quickAction || node.dataset.panelIcon || node.hasAttribute?.("data-link-visible") || node.hasAttribute?.("data-link-label") || node.hasAttribute?.("data-link-icon") || id?.startsWith("panel-")) return performAction("save-panel");
  if (node.matches?.("[data-shortcut-action]")) {
    if (value === "pick-app") return chooseShortcutApp(node, "shortcut-save");
    node.dataset.committed = value;
    return performAction("shortcut-save");
  }
  if (node.closest?.(".shortcut-row")) return performAction("shortcut-save");
  if (id === "copilot-action") {
    if (value === "pick-app") return chooseShortcutApp(node, "save-copilot");
    node.dataset.committed = value;
    return performAction("save-copilot");
  }
  if (node.closest?.("[data-curve]")) return performAction("curve-save", { key: node.closest("[data-curve]").dataset.curve });
  if (node.dataset.jsonSetting) return performAction("json-save", { key: node.dataset.jsonSetting });
  if (["auto-refresh", "ac-refresh", "battery-refresh"].includes(id)) return performAction("save-display");
  if (["ac-mode", "battery-mode"].includes(id)) return performAction("save-profiles");
  if (["theme-preset", "theme-accent", "theme-background", "theme-surface", "osd-style", "developer-mode", "original-popup"].includes(id)) {
    if (id === "theme-preset" && value !== "custom") {
      const colors = UI.palette({ ...prefs(), themePreset: value });
      $("theme-accent").value = colors.accent;
      $("theme-background").value = colors.background;
      $("theme-surface").value = colors.surface;
    }
    if (["theme-accent", "theme-background", "theme-surface"].includes(id)) $("theme-preset").value = "custom";
    $("palette-save").hidden = $("theme-preset").value !== "custom";
    const selectedPalette = (prefs().themePalettes || []).find(saved => saved.id === $("theme-preset").value);
    $("palette-manage").hidden = !selectedPalette;
    $("palette-rename-editor").hidden = true;
    $("palette-rename").value = selectedPalette?.name || "";
    UI.applyPreferences({ ...prefs(), themePreset: $("theme-preset").value,
      themeAccent: $("theme-accent").value, themeBackground: $("theme-background").value,
      themeSurface: $("theme-surface").value });
    return performAction("save-appearance");
  }
  if (node.dataset.setting) { const s = settingState.rows.find(item => item.key === node.dataset.setting); if (s.key.endsWith("Action") && value === "app") return change("settings.keyApp", { slot: s.key.slice(0, -6) }); return change("settings.apply", { key: s.key, value: s.kind === "toggle" ? checked : ["number","decimal"].includes(s.kind) ? Number(value) : value }); }
  if (id === "refresh-rate") change("display.refresh", { value: checked ? 120 : 60 });
  if (id === "refresh-rate-select") change("display.refresh", { value: Number(value) });
  if (id === "charge-limit") change("battery.limit", { value: Number(value) });
  if (id === "vibration") change("touchpad.vibration", { value });
  if (id === "pressure") change("touchpad.pressure", { value: Number(value) });
  if (id === "travel") change("battery.travel", { on: checked });
  if (id === "awake") change("window.awake", { on: checked });
  if (id === "sleepoff") change("window.preventSleep", { on: checked });
  if (id === "touchpad" || id === "touchscreen") change("input.enabled", { device: id, on: checked });
  if (id === "appearance") { UI.applyAppearance(value); change("settings.save", { appearance: value, closeToTray: true }); }
  if (id === "minimize-tray") change("settings.minimizeToTray", { on: checked });
});
$("refresh").addEventListener("click", async () => {
  const control = $("refresh");
  control.classList.add("refreshing");
  control.disabled = true;
  await writeTail;
  while (reading) await new Promise(resolve => setTimeout(resolve, 20));
  try { const result = await Native.call("window.reconnect"); await refresh(true); notice(result.message); }
  catch (error) { notice(error.message, true); }
  finally { control.disabled = false; control.classList.remove("refreshing"); }
});
$("setting-search").addEventListener("keydown", event => {
  if (event.key === "Escape") { event.currentTarget.value = ""; renderSearch(); }
  if (event.key === "Enter" && searchHits.length) { event.preventDefault(); openSearchHit(searchHits[0]); }
});
document.addEventListener("pointerdown", event => {
  if (!event.target.closest(".header-search")) $("setting-search-results").hidden = true;
});
$("refresh").innerHTML = UI.icon("reload");
$("store-shortcut").innerHTML = UI.icon("store");
$("minimize").addEventListener("click", () => Native.call("window.minimize").catch(error => notice(error.message, true)));
$("maximize").innerHTML = UI.icon("maximize");
$("maximize").addEventListener("click", () => Native.call("window.maximize").catch(error => notice(error.message, true)));
for (const action of ["undo", "redo"]) { $(action).innerHTML = UI.icon(action); $(action).addEventListener("click", () => change("settings." + action)); }
document.addEventListener("keydown", event => {
  if (event.target.id === "palette-rename" && ["Enter", "Escape"].includes(event.key)) {
    event.preventDefault();
    return performAction(event.key === "Enter" ? "rename-palette" : "cancel-rename-palette");
  }
  if (!event.ctrlKey || event.altKey || event.metaKey || event.target.matches("input,textarea,[contenteditable=true]")) return;
  const action = event.key.toLowerCase() === "y" || event.shiftKey && event.key.toLowerCase() === "z" ? "redo" : event.key.toLowerCase() === "z" ? "undo" : null;
  if (action && !$(action).disabled) { event.preventDefault(); change("settings." + action); }
});
$("hide-manager").addEventListener("click", () => Native.call("window.hide").catch(error => notice(error.message, true)));
document.querySelector(".app-header")?.addEventListener("pointerdown", event => {
  if (event.button === 0 && !event.target.closest("button,a,input,select")) Native.call("window.drag").catch(() => {});
});
window.addEventListener("hashchange", () => { const target = location.hash.slice(1); if (target !== page) showPage(target); });
document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(true); });
Native.listen(message => { if (message.appearance) { if (snapshot?.hardware?.preferences) snapshot.hardware.preferences.appearance = message.appearance; UI.applyAppearance(message.appearance); } if (message.managerPage) showPage(message.managerPage); if (message.activated) refresh(true); if (message.notice) notice(message.notice); });
if (Native.connected) {
  try {
    const previous = JSON.parse(localStorage.getItem(cacheKey) || "null");
    if (previous?.snapshot?.hardware && Date.now() - previous.savedAt < 7 * 24 * 3600 * 1000) {
      snapshot = previous.snapshot; settingState = previous.settingState; customization = previous.customization;
      cachedState = true;
      $("last-updated").textContent = "Last known values · " + new Date(previous.savedAt).toLocaleTimeString("en-GB");
    }
  } catch { /* A corrupt or unavailable cache must never block the manager. */ }
}
render(); refresh(true); setInterval(() => refresh(), 5000);
