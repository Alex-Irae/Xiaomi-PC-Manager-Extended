/* Purpose: explicitly requested runtime validation through the production WebView bridge.
   Dependencies: bridge.js/app.js in the resident. Outputs: native saved report; no network or installs.
   Command: app/XiaomiAIManager.exe --validate-controls. Native host independently restores the baseline. */
globalThis.runControlValidation = async function (token, phase) {
  const rows = [], trace = [], original = Native.call;
  const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
  const post = value => chrome.webview.postMessage({ validationToken: token, ...value });
  Native.call = async function (method, args = {}) {
    const start = performance.now();
    try {
      const result = await original(method, args);
      const captured = structuredClone(result);
      if (captured?.hardware) delete captured.hardware.specs;
      trace.push({ method, args, elapsedMs: performance.now() - start, result: captured });
      return result;
    } catch (error) { trace.push({ method, args, elapsedMs: performance.now() - start, error: error.message }); throw error; }
  };
  let baseline;
  async function validateSettings() {
    const toRgb = hex => `rgb(${[1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16)).join(", ")})`;
    const settings = await Native.call("settings.read");
    rows.push({ name: "settings-catalog", status: settings.rows.length >= 80 && (baseline.testMode ? !settings.keys.running : settings.keys.enabled && settings.keys.running) ? "pass" : "fail", rows: settings.rows.length, keys: settings.keys,
      keyActions: Object.fromEntries(settings.rows.filter(s => /^(MiClick|SettingsKey|AiKey|ProjKey)Action$/.test(s.key)).map(s => [s.key, s.value])) });
    rows.push({ name: "catalog-defaults-valid", status: settings.defaultErrors?.length === 0 && settings.rows.every(s => Object.hasOwn(s, "defaultValue")) ? "pass" : "fail", errors: settings.defaultErrors });
    await check("settings-snapshot", "settings.snapshot", {}, (_, result) => /[\\/]settings-snapshots[\\/]settings-.*\.json$/i.test(result.path || ""));
    for (const theme of ["dark", "light"]) {
      await Native.call("settings.save", { appearance: theme, closeToTray: baseline.preferences.closeToTray !== false });
      await pause(100);
      const expectedBackground = baseline.preferences.themePreset === "custom" ? baseline.preferences.themeBackground : UI.presetColors().background;
      rows.push({ name: "theme-" + theme, status: document.documentElement.dataset.theme === theme && getComputedStyle(document.body).backgroundColor === toRgb(expectedBackground) ? "pass" : "fail", theme: document.documentElement.dataset.theme, background: getComputedStyle(document.body).backgroundColor });
    }
    await check("reset-invalid-category", "settings.reset", { group: "invalid" }, null, true);
    await check("reset-notifications", "settings.reset", { group: "notifications" }, (_, r) => r.complete === true);
    await check("custom-shortcut-registration", "settings.shortcuts", { shortcuts: [{ chord: "Ctrl+Alt+F24", action: "page.battery" }] }, null);
    rows.push({ name: "custom-shortcut-persisted", status: (await Native.call("settings.read")).shortcuts?.some(item => item.chord === "Ctrl+Alt+F24" && item.action === "page.battery") ? "pass" : "fail" });
    await check("reset-keyboard", "settings.reset", { group: "keyboard" }, (_, r) => r.complete === true);
    const reset = await Native.call("settings.read");
    rows.push({ name: "reset-key-defaults", status: reset.rows.find(s => s.key === "SettingsKeyAction").value === "windowssettings" && reset.rows.find(s => s.key === "AiKeyAction").value === "xiaoai" && !reset.rows.find(s => s.key === "HandleScreenshotKey").value ? "pass" : "fail" });
    rows.push({ name: "reset-custom-shortcuts", status: reset.shortcuts?.length === 0 ? "pass" : "fail" });
    const ai = settings.rows.find(s => s.key === "AiKeyAction");
    rows.push({ name: "key-remap-options", status: ai?.options.includes("app") && ai.options.includes("page.battery") && ai.options.includes("page.display") ? "pass" : "fail" });
    await check("reject-unknown-key-app", "settings.keyApp", { slot: "invalid" }, null, true);
    for (const position of ["Top", "Bottom"])
      await check("notification-position-" + position, "settings.apply", { key: "OsdPosition", value: position }, (_, r) => r.value === position);
    await check("notification-preview", "window.lockPreview", {}, (_, r) => Boolean(r.message));
    await check("reject-haptic-outside-presets", "settings.apply", { key: "SlideStrength", value: "255" }, null, true);
    rows.push({ name: "companion-resolvers", status: settings.companions?.store?.toLowerCase().endsWith("miappstore.exe") && settings.companions?.xiaoai?.toLowerCase().endsWith("xiaoaiagent.exe") ? "pass" : "fail", paths: settings.companions, limitations: "Resolver read only; the companions were not launched." });
    settingState = await Native.call("settings.read"); customization = await Native.call("settings.customization"); snapshot = await Native.call("quick.read");
    page = "settings"; render();
    const searchInput = $("setting-search");
    searchInput.value = "luminosity";
    searchInput.dispatchEvent(new Event("input", { bubbles: true }));
    const brightnessHit = [...$("setting-search-results").querySelectorAll(".search-hit")].find(node => node.textContent.includes("Brightness"));
    rows.push({ name: "settings-search-related-term", status: brightnessHit && !$("setting-search-results").hidden ? "pass" : "fail", matches: searchHits.map(hit => hit.label) });
    brightnessHit?.click();
    rows.push({ name: "settings-search-navigation", status: page === "display" && searchInput.value === "" && $("setting-search-results").hidden ? "pass" : "fail", page });
    const rateControl = $("refresh-rate");
    rows.push({ name: "display-rate-switch", status: snapshot.hardware.refreshRates?.includes(60) && snapshot.hardware.refreshRates?.includes(120)
      ? rateControl?.type === "checkbox" && rateControl.checked === (snapshot.hardware.refreshRate === 120) && rateControl.closest("label")?.textContent.includes("60 Hz") && rateControl.closest("label")?.textContent.includes("120 Hz") ? "pass" : "fail"
      : $("refresh-rate-select") ? "pass" : "fail", supported: snapshot.hardware.refreshRates });
    rows.push({ name: "store-header-shortcut", status: $("store-shortcut").dataset.action === "xiaomi.open" && JSON.parse($("store-shortcut").dataset.args).section === "store" && Boolean($("store-shortcut").querySelector("svg")) ? "pass" : "fail" });
    const reconnectCalls = trace.length, reconnectStart = performance.now(), refreshButton = $("refresh");
    refreshButton.click();
    while (refreshButton.disabled && performance.now() - reconnectStart < 10000) await pause(20);
    const reconnectTrace = trace.slice(reconnectCalls).find(item => item.method === "window.reconnect");
    const reconnected = (await Native.call("quick.read")).hardware;
    rows.push({ name: "firmware-reconnect-button", status: reconnectTrace?.result?.message && !reconnectTrace.error && !refreshButton.disabled && !reconnected.firmwareError ? "pass" : "fail",
      elapsedMs: performance.now() - reconnectStart, error: reconnectTrace?.error });
    showPage("keyboard");
    const shortcutList = $("shortcut-list");
    shortcutList.innerHTML = shortcutRow({ chord: "Ctrl+Alt+K", action: "modes" }, shortcutActions()) + shortcutRow({ chord: "Ctrl+Alt+S", action: "hz" }, shortcutActions());
    const shortcutControls = [...shortcutList.querySelector(".shortcut-row").children].map(node => node.getBoundingClientRect());
    const shortcutNote = shortcutList.previousElementSibling.getBoundingClientRect();
    const appChoices = [...shortcutList.querySelectorAll("[data-shortcut-action]")].every(select => [...select.options].some(option => option.value === "pick-app"));
    rows.push({ name: "shortcut-row-alignment", status: shortcutControls.length === 3 && shortcutControls.every(rect => Math.abs(rect.top - shortcutControls[0].top) < 4) && shortcutControls[0].top - shortcutNote.bottom >= 12 && appChoices ? "pass" : "fail", tops: shortcutControls.map(rect => rect.top), gap: shortcutControls[0].top - shortcutNote.bottom, appChoices });
    showPage("settings");
    document.querySelector('details[data-persist="icons"]').open = true; render();
    rows.push({ name: "icon-menu-stays-open", status: document.querySelector('details[data-persist="icons"]').open ? "pass" : "fail" });
    showPage("keyboard"); showPage("settings");
    rows.push({ name: "icon-menu-closes-on-tab", status: !document.querySelector('details[data-persist="icons"]').open ? "pass" : "fail" });
    // Focus the actual WebView before testing keyboard help, rather than a background document.
    await Native.call("window.manager", { page: "settings" }); await pause(150);
    const help = [...document.querySelectorAll('.setting-help [role="tooltip"]')];
    const hiddenByDefault = help.every(node => getComputedStyle(node).display === "none");
    const visibleDefaults = [...document.querySelectorAll('.setting-description')].filter(node => node.textContent.includes("Default:")).length;
    let shownOnFocus = false;
    if (help.length) { help[0].parentElement.focus(); shownOnFocus = getComputedStyle(help[0]).display !== "none"; help[0].parentElement.blur(); }
    rows.push({ name: "defaults-in-hover-help", status: help.length > 0 && help.every(node => node.textContent.includes("Default:")) && hiddenByDefault && shownOnFocus && visibleDefaults === 0 ? "pass" : "fail", count: help.length, hiddenByDefault, shownOnFocus, visibleDefaults, documentFocused: document.hasFocus() });
    const content = document.querySelector("main"); content.scrollTop = 180; await pause(50);
    const closeBounds = $("hide-manager").getBoundingClientRect(), profileBounds = document.querySelector(".brand").getBoundingClientRect();
    rows.push({ name: "manager-chrome-stays-visible", status: content.scrollTop > 0 && closeBounds.top >= 0 && closeBounds.bottom < innerHeight && profileBounds.top >= 0 && profileBounds.bottom < innerHeight ? "pass" : "fail", scrolled: content.scrollTop, closeTop: closeBounds.top, profileTop: profileBounds.top });
    content.scrollTop = 0;
    rows.push({ name: "vertical-scroll-policy", status: getComputedStyle(content).overflowY === "auto" && getComputedStyle(content).overscrollBehaviorY === "auto" && getComputedStyle(content).overflowX === "hidden" ? "pass" : "fail", limitations: "Native vertical scrolling is enabled in CSS. This is not a physical two-finger gesture test." });
    const initialWindow = await Native.call("window.state");
    const initialViewport = { width: innerWidth, height: innerHeight };
    await Native.call("window.maximize"); await pause(200);
    const expanded = await Native.call("window.state");
    rows.push({ name: "manager-maximize", status: expanded.maximized !== initialWindow.maximized && expanded.screenBottomGap >= 1 && expanded.screenBottomGap <= 3 && (initialWindow.maximized || innerWidth >= initialViewport.width && innerHeight >= initialViewport.height) ? "pass" : "fail", viewport: { width: innerWidth, height: innerHeight }, screenBottomGap: expanded.screenBottomGap });
    await Native.call("window.maximize"); await pause(200);
    rows.push({ name: "manager-restore-window", status: (await Native.call("window.state")).maximized === initialWindow.maximized && innerWidth === initialViewport.width && innerHeight === initialViewport.height ? "pass" : "fail" });
    async function settleEdits() { const start = performance.now(); while ((busy || reading) && performance.now() - start < 10000) await pause(10); if (busy || reading) throw new Error("Autosave did not settle."); }
    busy = false; page = "settings"; render();
    const initial = structuredClone(snapshot.hardware.preferences);
    $("theme-preset").value = "pink"; $("theme-preset").dispatchEvent(new Event("change", { bubbles: true })); await settleEdits();
    rows.push({ name: "preset-DOM-autosave", status: snapshot.hardware.preferences.themePreset === "pink" && snapshot.hardware.preferences.themeAccent === "#e980bc" && !$("page").textContent.includes("Save appearance") ? "pass" : "fail" });
    const colors = UI.presetColors();
    const cardColor = getComputedStyle(document.querySelector(".MiCard")).backgroundColor;
    rows.push({ name: "preset-color-pickers-match-render", status: $("theme-accent").value === UI.presetAccents.pink && $("theme-background").value === colors.background && $("theme-surface").value === colors.surface && getComputedStyle(document.body).backgroundColor === toRgb(colors.background) && cardColor === toRgb(colors.surface) ? "pass" : "fail", accentPicker: $("theme-accent").value, backgroundPicker: $("theme-background").value, cardPicker: $("theme-surface").value, renderedCard: cardColor });
    $("theme-accent").value = "#49b899"; $("theme-accent").dispatchEvent(new Event("change", { bubbles: true })); await settleEdits();
    rows.push({ name: "accent-DOM-override", status: snapshot.hardware.preferences.themePreset === "custom" && $("theme-preset").value === "custom" && getComputedStyle(document.documentElement).getPropertyValue("--blue").trim() === "#49b899" ? "pass" : "fail" });
    $("undo").click(); await settleEdits();
    rows.push({ name: "undo-DOM-accent", status: snapshot.hardware.preferences.themeAccent === "#e980bc" && !$("redo").disabled ? "pass" : "fail" });
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "y", ctrlKey: true, bubbles: true })); await settleEdits();
    rows.push({ name: "redo-Ctrl-Y", status: snapshot.hardware.preferences.themeAccent === "#49b899" ? "pass" : "fail" });
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "z", ctrlKey: true, bubbles: true })); await settleEdits();
    $("undo").click(); await settleEdits();
    rows.push({ name: "undo-Ctrl-Z-and-preset", status: snapshot.hardware.preferences.themePreset === initial.themePreset && snapshot.hardware.preferences.themeAccent === initial.themeAccent ? "pass" : "fail" });
    for (const accent of ["#ab3456", "#3456ab", "#56ab34"]) { $("theme-accent").value = accent; $("theme-accent").dispatchEvent(new Event("change", { bubbles: true })); }
    await settleEdits();
    rows.push({ name: "rapid-autosave-retains-last-edit", status: snapshot.hardware.preferences.themeAccent === "#56ab34" ? "pass" : "fail" });
    $("theme-surface").value = "#252b30"; $("theme-surface").dispatchEvent(new Event("change", { bubbles: true })); await settleEdits();
    rows.push({ name: "manual-card-color-selects-custom", status: snapshot.hardware.preferences.themePreset === "custom" && $("theme-preset").value === "custom" && $("theme-surface").value === "#252b30" && getComputedStyle(document.querySelector(".MiCard")).backgroundColor === "rgb(37, 43, 48)" ? "pass" : "fail" });
    busy = true;
    for (const preset of ["red", "pink", "custom"]) {
      const expected = UI.presetAccents[preset] || "#49b899";
      await Native.call("settings.appearance", { preset, accent: expected, background: "#20262e", surface: "#303842", osdStyle: "xiaomi", developerMode: false, originalPopup: false });
      snapshot = await Native.call("quick.read"); render();
      rows.push({ name: "theme-accent-" + preset, status: getComputedStyle(document.documentElement).getPropertyValue("--blue").trim() === expected ? "pass" : "fail" });
      if (preset === "custom") rows.push({ name: "custom-theme-background", status: getComputedStyle(document.body).backgroundColor === "rgb(32, 38, 46)" ? "pass" : "fail", background: getComputedStyle(document.body).backgroundColor });
    }
    // Return to baseline theme before screenshot and theme checks outside this bridge script.
    const p = baseline.preferences;
    await Native.call("settings.appearance", { preset: p.themePreset, accent: p.themeAccent, background: p.themeBackground, surface: p.themeSurface, osdStyle: p.osdStyle, developerMode: p.developerMode, originalPopup: false });
    snapshot = await Native.call("quick.read"); render();
    if (baseline.refreshRates?.length > 1) {
      const rates = baseline.refreshRates, low = Math.min(...rates), high = Math.max(...rates);
      await Native.call("display.automatic", { on: true, ac: low, battery: low }); await pause(700);
      await check("manual-refresh-override", "display.refresh", { value: high }, state => state.refreshRate === high);
      await Native.call("settings.apply", { key: "OsdDurationMs", value: 1700 });
      const readings = [];
      for (let i = 0; i < 7; i++) { await pause(5000); readings.push((await Native.call("quick.read")).hardware.refreshRate); post({ progress: "Manual display hold " + (i + 1) + "/7" }); }
      rows.push({ name: "manual-refresh-hold-35s", status: readings.every(rate => rate === high) ? "pass" : "fail", readings, rate: high, limitations: "Same physical power source. AC unplug/replug is covered by injected event regression, not a physical cable test in this run." });
    }
  }
  async function check(name, method, args, verify, expectedError = false, settle = 0) {
    post({ progress: name });
    const start = performance.now();
    try {
      const response = await Native.call(method, args), callMs = performance.now() - start;
      if (settle) await pause(settle);
      const state = await Native.call("quick.read");
      const confirmed = !expectedError && (!verify || verify(state.hardware, response));
      rows.push({ name, status: confirmed ? "pass" : "fail", callMs, inputToReadbackMs: performance.now() - start, args, response, hardware: state.hardware });
    } catch (error) {
      const intendedRefusal = expectedError && !/timed out|could not be completed/i.test(error.message)
        && /Choose a listed settings category|Brightness must|Choose a supported|Choose a listed|not offered|power source changed|Choose a refresh|Unknown|not available|Use Open Xiaomi|Connect the charger/i.test(error.message);
      rows.push({ name, status: intendedRefusal ? "pass" : "fail", elapsedMs: performance.now() - start, args, error: error.message });
    }
  }
  try {
    busy = true;
    baseline = (await Native.call("quick.read")).hardware;
    rows.push({ name: "baseline", status: "observed", hardware: baseline,
      viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio },
      fontFamily: getComputedStyle(document.body).fontFamily,
      fontFaces: Array.from(document.fonts, f => ({ family: f.family, status: f.status })) });
    if (phase === "oem") {
      for (const section of ["drivers", "store"])
        await check("OEM-" + section + "-" + rows.length, "xiaomi.open", { section }, (_, response) => section === "drivers" || response.exactPage === true || response.navigationRequested === true, false, 4000);
      for (const row of rows) if (row.name.startsWith("OEM-") && row.status === "pass") row.status = "requested";
      return;
    }
    if (phase === "ui") { await validateSettings(); return; }
    if (phase === "brightness") {
      for (const settle of [0, 150]) for (let i = 0; i < 60 && baseline.brightness != null; i++) {
        const value = Math.max(10, Math.min(90, baseline.brightness + [5, -5, 10, 0][i % 4]));
        await check("brightness-" + (settle ? "settled" : "rapid") + "-" + i, "display.brightness", { value }, h => Math.abs(h.brightness - value) <= 2, false, settle);
      }
      return;
    }
    for (let i = 0; i < 40 && baseline.brightness != null; i++) {
      const value = Math.max(10, Math.min(90, baseline.brightness + [5, -5, 10, 0][i % 4]));
      await check("brightness-changed-" + i, "display.brightness", { value }, h => Math.abs(h.brightness - value) <= 2);
    }
    if (baseline.brightness != null) {
      const slider = document.getElementById("brightness");
      const start = performance.now(), first = trace.length;
      const values = [2, 4, 6, 8, 10].map(v => Math.min(90, baseline.brightness + v));
      for (const value of values) { slider.value = value; slider.dispatchEvent(new Event("input", { bubbles: true })); }
      while (BrightnessInput.pending && performance.now() - start < 10000) await pause(5);
      const actual = (await Native.call("quick.read")).hardware.brightness;
      rows.push({ name: "brightness-DOM-burst", status: actual === values.at(-1) ? "pass" : "fail", inputToReadbackMs: performance.now() - start,
        values, actual, writes: trace.slice(first).filter(r => r.method === "display.brightness") });
    }
    for (const [name, method, args] of [
      ["reject-brightness-zero", "display.brightness", { value: 0 }],
      ["reject-charge-75", "battery.limit", { value: 75 }],
      ["reject-balanced", "performance.set", { value: "Balance", source: baseline.powerSource }],
      ["reject-stale-source", "performance.set", { value: "Auto", source: baseline.powerSource === "Online" ? "Offline" : "Online" }],
      ["reject-unavailable-mode", "performance.set", { value: baseline.powerSource === "Online" ? "Turbo" : "FullSpeed", source: baseline.powerSource }],
      ["reject-refresh-77", "display.refresh", { value: 77 }],
      ["reject-haptic-name", "touchpad.vibration", { value: "Invalid" }],
      ["reject-pressure-999", "touchpad.pressure", { value: 999 }],
      ["reject-input-node", "input.enabled", { device: "controller", on: false }],
      ["reject-share-launch", "xiaomi.open", { section: "share" }],
      ["reject-unknown-method", "unknown.command", {}]
    ]) await check(name, method, args, null, true);
    for (const mode of UI.modes(baseline.powerSource))
      await check("mode-" + baseline.powerSource + "-" + mode, "performance.set", { value: mode, source: baseline.powerSource }, h => h.mode === mode && (baseline.powerSource === "Online" ? h.preferences.acMode : h.preferences.batteryMode) === mode, false, 150);
    snapshot = await Native.call("quick.read");
    busy = false;
    showPage("performance");
    await pause(20);
    const modeButton = Array.from(document.querySelectorAll('[data-action="performance.set"]')).find(button => JSON.parse(button.dataset.args).value === "Quiet");
    const clickStart = performance.now();
    modeButton.click(); // Use the production event handler, including readback, selected state and rendering.
    while ((busy || reading) && performance.now() - clickStart < 10000) await pause(5);
    const chosen = document.querySelector('[data-action="performance.set"].selected');
    rows.push({ name: "mode-DOM-click", status: chosen && JSON.parse(chosen.dataset.args).value === "Quiet" && (await Native.call("quick.read")).hardware.mode === "Quiet" ? "pass" : "fail",
      inputToReadbackMs: performance.now() - clickStart });
    busy = true;
    for (const value of [40, 50, 60, 70, 80, 100])
      await check("charge-limit-" + value, "battery.limit", { value }, h => h.chargeLimit === value && h.requestedChargeLimit === value, false, value === 100 ? 3500 : 300);
    await check("charge-70-again", "battery.limit", { value: 70 }, h => h.chargeLimit === 70);
    await check("full-charge-hold", "battery.limit", { value: 100 }, h => h.chargeLimit === 100 && !h.chargeConflict, false, 35000);
    await check("care-before-travel", "battery.limit", { value: 80 }, h => h.chargeLimit === 80);
    if ((await Native.call("quick.read")).hardware.powerSource === "Online") {
      await check("travel-enable", "battery.travel", { on: true }, h => h.travel && h.chargeLimit === 100, false, 2500);
      await check("travel-cancel", "battery.travel", { on: false }, h => !h.travel && h.chargeLimit === 80);
    } else await check("travel-battery-refusal", "battery.travel", { on: true }, null, true);
    const physical = (await Native.call("quick.read")).hardware;
    const care = baseline.chargeLimit < 100 ? baseline.chargeLimit : 70;
    if (physical.powerSource === "Online" && physical.batteryPercent > care && physical.batteryPercent < 95) {
      for (const [name, value, expectCharging] of [["care", care, false], ["full", 100, true], ["care-restored", care, false]]) {
        await check("physical-setting-" + name, "battery.limit", { value }, h => h.chargeLimit === value, false, 8000);
        const samples = [];
        for (let i = 0; i < 5; i++) {
          const hardware = (await Native.call("quick.read")).hardware, telemetry = await Native.call("telemetry.read");
          samples.push({ hardware, telemetry });
          if (i < 4) await pause(2000);
        }
        // BATTERY_STATUS reports AC as bit 1 and actual charging as bit 4. Null is
        // an unavailable IOCTL, never evidence of zero flow or a charging stop.
        const valid = samples.every(s => s.hardware.powerSource === "Online" && s.hardware.batteryPercent > care && s.telemetry.batteryPowerState != null && (s.telemetry.batteryPowerState & 1));
        const matches = valid && samples.every(s => Boolean(s.telemetry.batteryPowerState & 4) === expectCharging);
        rows.push({ name: "physical-charging-" + name, status: !valid ? "inconclusive" : matches ? "pass" : "fail", expectCharging, care, samples });
      }
    } else rows.push({ name: "physical-charging", status: "skipped", reason: "Needs AC, battery above the care limit and below 95%.", hardware: physical, care });
    for (const rate of baseline.refreshRates || [])
      await check("refresh-" + rate, "display.refresh", { value: rate }, h => h.refreshRate === rate, false, 300);
    if (baseline.hapticsAvailable) {
      for (const value of ["Low", "Medium", "High"])
        await check("haptics-" + value, "touchpad.vibration", { value }, h => h.vibration === value);
      for (const value of [100, 125, 140])
        await check("pressure-" + value, "touchpad.pressure", { value }, h => h.pressure === value);
    }
    for (const device of ["touchpad", "touchscreen"]) {
      if (baseline[device + "Enabled"] !== true) { rows.push({ name: "input-" + device, status: "skipped", reason: "Baseline input is unavailable or disabled." }); continue; }
      try { await check("input-off-" + device, "input.enabled", { device, on: false }, h => h[device + "Enabled"] === false, false, 500); }
      finally { await check("input-restore-" + device, "input.enabled", { device, on: true }, h => h[device + "Enabled"] === true, false, 500); }
    }
    await check("awake-on", "window.awake", { on: true }, (_, response) => response.awake === true);
    await check("awake-off", "window.awake", { on: false }, (_, response) => response.awake === false);
    for (let i = 0; i < 3; i++) { await check("telemetry-" + i, "telemetry.read", {}, (_, r) => r.memoryPercent != null && r.cpuPackageWatts != null); await pause(1000); }
    await validateSettings();
    rows.push({ name: "end-state", status: "observed", hardware: (await Native.call("quick.read")).hardware });
  } catch (error) { rows.push({ name: "suite", status: "fail", error: error.message }); }
  finally { Native.call = original; busy = false; post({ result: { baseline, rows, trace } }); }
};
