/* Native calls only. A normal browser previews the layout with all device writes disabled. */
"use strict";
const Native = (() => {
  const host = window.chrome?.webview;
  const pending = new Map();
  const listeners = new Set();
  let sequence = 0;
  host?.addEventListener("message", ({ data }) => {
    if (data.id && pending.has(data.id)) {
      const request = pending.get(data.id);
      pending.delete(data.id);
      clearTimeout(request.timeout);
      if (data.ok) request.resolve(data.data);
      else request.reject(new Error(data.error || "The operation failed."));
    }
    for (const listener of listeners) listener(data);
  });
  return {
    connected: Boolean(host),
    listen: listener => listeners.add(listener),
    call(method, args = {}) {
      if (!host) return Promise.reject(new Error("Open XiaomiAIManager.exe to use device controls."));
      return new Promise((resolve, reject) => {
        const id = String(++sequence);
        const timeout = setTimeout(() => {
          pending.delete(id);
          reject(new Error("The operation timed out and may still be running. Refresh to check its state."));
        }, 30000);
        pending.set(id, { resolve, reject, timeout });
        host.postMessage({ id, method, args });
      });
    }
  };
})();

// At most one write in flight; dragging replaces the pending value rather than building a backlog.
const BrightnessInput = (() => {
  let latest = null, running = false;
  return {
    get pending() { return running || latest !== null; },
    async set(value, confirmed, failed) {
      latest = value;
      if (running) return;
      running = true;
      try {
        while (latest !== null) {
          const next = latest;
          latest = null;
          try {
            const result = await Native.call("display.brightness", { value: next });
            if (latest === null) confirmed(result.brightness);
          } catch (error) { if (latest === null) failed(error.message); }
        }
      } finally { running = false; }
    }
  };
})();

const UI = {
  applyAppearance(appearance = "light") {
    document.documentElement.dataset.followWindows = String(appearance === "system");
    document.documentElement.dataset.theme = appearance === "system" ? (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light") : appearance;
    const preset = document.querySelector("#theme-preset");
    if (preset && UI.presetAccents[preset.value]) {
      const colors = UI.presetColors();
      document.querySelector("#theme-accent").value = UI.presetAccents[preset.value];
      document.querySelector("#theme-background").value = colors.background;
      document.querySelector("#theme-surface").value = colors.surface;
    }
  },
  presetColors() {
    const style = getComputedStyle(document.documentElement);
    return { background: style.getPropertyValue("--preset-bg").trim(), surface: style.getPropertyValue("--preset-surface").trim() };
  },
  palette(p = {}) {
    const preset = p.themePreset || "blue";
    if (UI.presetAccents[preset]) return { accent: UI.presetAccents[preset], ...UI.presetColors() };
    const saved = (p.themePalettes || []).find(palette => palette.id === preset);
    if (saved) return { accent: saved.accent, background: saved.background, surface: saved.surface };
    return { accent: p.themeAccent || "#3482ff", background: p.themeBackground || "#171a1f", surface: p.themeSurface || "#1e2228" };
  },
  applyPreferences(p = {}) {
    const root = document.documentElement, style = root.style;
    const assets = p.customAssets || {};
    if (UI.assetRevision !== assets.revision) UI.failedAssetIcons.clear();
    UI.assetRevision = assets.revision || 0;
    UI.assetIcons = new Set(assets.icons || []);
    let customStyle = document.querySelector("#custom-asset-style");
    if (assets.css) {
      if (!customStyle) {
        customStyle = document.createElement("link");
        customStyle.id = "custom-asset-style"; customStyle.rel = "stylesheet";
        document.head.append(customStyle);
      }
      const url = "https://xiaomi-custom-assets.local/custom.css?v=" + UI.assetRevision;
      if (customStyle.href !== url) customStyle.href = url;
    } else customStyle?.remove();
    root.dataset.compact = String(p.compactPanel !== false);
    root.dataset.animations = String(p.popupAnimations !== false);
    root.dataset.developer = String(Boolean(p.developerMode));
    const preset = p.themePreset || "blue", palette = UI.palette(p), accent = palette.accent;
    const valid = value => /^#[a-fA-F0-9]{6}$/.test(value);
    if (style?.setProperty && valid(accent)) {
      style.setProperty("--blue", accent); style.setProperty("--blue-bg", `color-mix(in srgb, ${accent} 12%, transparent)`);
      style.setProperty("--active", `color-mix(in srgb, ${accent} 18%, var(--surface))`);
      if (!UI.presetAccents[preset] && valid(palette.background) && valid(palette.surface)) {
        style.setProperty("--panel", palette.background); style.setProperty("--bg", palette.background); style.setProperty("--surface", palette.surface);
      } else { style.removeProperty("--panel"); style.removeProperty("--bg"); style.removeProperty("--surface"); }
    }
    document.querySelectorAll(".profile-picture").forEach(node => {
      const source = /^profile-[a-f0-9]{32}\.png$/.test(p.profileImage || "") ? "https://xiaomi-icons.local/" + p.profileImage
        : assets.logo ? "https://xiaomi-custom-assets.local/app.png?v=" + UI.assetRevision : "https://xiaomi-assets.local/app.svg";
      if (node.getAttribute("src") !== source) node.setAttribute("src", source);
    });
  },
  power(t) {
    if (Number.isFinite(t.batteryWatts) && t.batteryWatts < 0) return { watts: Math.abs(t.batteryWatts), detail: "Battery discharge across the laptop" };
    return { watts: t.cpuPackageWatts, detail: "Processor package only while on AC; full laptop draw is unavailable" };
  },
  escape: value => String(value ?? "").replace(/[&<>"']/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[character])),
  number: (value, unit = "", digits = 0) => Number.isFinite(value) ? value.toFixed(digits) + unit : "Unavailable",
  mode: (value, source) => ({ Quiet: source === "Online" ? "Silent" : "Quiet", Auto: source === "Online" ? "Smart" : "Auto", Turbo: "Turbo", FullSpeed: "Full speed", Eco: "Eco" }[value] || "Unlisted firmware mode"),
  modes: source => source === "Online" ? ["Quiet", "Auto", "FullSpeed", "Eco"] : ["Eco", "Quiet", "Auto", "Turbo"],
  chargeEstimate(h, t) {
    const target = h.travel ? 100 : h.requestedChargeLimit ?? h.chargeLimit;
    if (!Number.isFinite(target) || !Number.isFinite(h.batteryPercent) || !Number.isFinite(h.fullWh) || h.fullWh <= 0) return { status: "Unavailable", target };
    if (h.batteryPercent >= target) return { status: "Target reached", target, minutes: 0 };
    if (h.powerSource !== "Online") return { status: "Connect the charger", target };
    if (t.batteryPowerState != null && !(t.batteryPowerState & 4)) return { status: "Not charging", target };
    // Firmware and Windows battery APIs can report charging watts with either sign.
    const chargingWatts = Math.abs(t.batteryWatts);
    if (!Number.isFinite(chargingWatts) || chargingWatts < 0.1) return { status: "Waiting for charging power", target };
    // Remaining energy is full capacity times the percentage gap. Wh / W gives hours.
    const minutes = 60 * h.fullWh * (target - h.batteryPercent) / (100 * chargingWatts);
    return { status: "Charging", target, minutes };
  },
  quickIcons: {},
  assetIcons: new Set(),
  failedAssetIcons: new Set(),
  assetRevision: 0,
  presetAccents: { blue: "#3482ff", red: "#ff615e", pink: "#e980bc", green: "#3ab58b" },
  modeIcon: value => ({ Quiet: "quiet", Auto: "auto", FullSpeed: "speed", Turbo: "speed", Eco: "battery" }[value] || "auto"),
  icon(name, bundled = false) {
    const chosen = UI.quickIcons[name] || name;
    if (!bundled && UI.assetIcons.has(chosen) && !UI.failedAssetIcons.has(chosen))
      return `<img class="asset-icon" data-asset-name="${UI.escape(name)}" alt="" src="https://xiaomi-custom-assets.local/icons/${encodeURIComponent(chosen)}.png?v=${UI.assetRevision}">`;
    const shapes = {
      settings: '<path d="m9 3 1-1h4l1 1 1 3 3 1 2 3v4l-2 3-3 1-1 3h-6l-1-3-3-1-2-3v-4l2-3 3-1z"/><circle cx="12" cy="12" r="3"/>',
      message: '<path d="M5 3h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H9l-6 4V5a2 2 0 0 1 2-2z"/><path d="M7 8h10M7 12h7"/>',
      undo: '<path d="m9 4-5 5 5 5M4 9h10a6 6 0 0 1 0 12"/>',
      redo: '<path d="m15 4 5 5-5 5M20 9H10a6 6 0 0 0 0 12"/>',
      reload: '<path d="M4 9a8 8 0 0 1 15-3l2 3m0-5v5h-5M20 15a8 8 0 0 1-15 3l-2-3m0 5v-5h5"/>',
      screen: '<rect x="3" y="4" width="18" height="13" rx="2"/><path d="m8 21 4-4 4 4"/>',
      screenoff: '<path d="M7 4h12a2 2 0 0 1 2 2v9m-2 2H5a2 2 0 0 1-2-2V6M8 21h8m-4-4v4M2 2l20 20"/>',
      maximize: '<path d="M9 3H3v6m12-6h6v6M3 15v6h6m12-6v6h-6"/>',
      device: '<circle cx="8" cy="7" r="4" fill="#8d80ff" stroke="none"/><circle cx="17" cy="11" r="4" fill="#68d6af" stroke="none"/><circle cx="10" cy="18" r="3" fill="#ff9b90" stroke="none"/>',
      quiet: '<path d="M20 14A8 8 0 0 1 10 4a8 8 0 1 0 10 10z"/>',
      auto: '<circle cx="12" cy="12" r="9"/><path d="m12 12 5-5M5 12h1m6-7v1m6 6h1M7 7l1 1"/>',
      speed: '<path d="m12 3-7 10h6l-1 8 9-12h-6z"/>',
      travel: '<rect x="5" y="7" width="14" height="14" rx="3"/><path d="M9 7V4h6v3M9 11v6m6-6v6"/>',
      touchpad: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 15h18m-9 0v4"/>',
      keyboard: '<rect x="2" y="5" width="20" height="14" rx="2"/><path d="M5 9h1m3 0h1m3 0h1m3 0h1M5 12h1m3 0h1m3 0h1m3 0h1M7 16h10"/>',
      touch: '<rect x="3" y="3" width="18" height="15" rx="2"/><path d="m13 21-3-6q-1-2 1-2l2 2V9q0-2 2 0v5l3 1v5"/>',
      refresh: '<rect x="2" y="4" width="20" height="15" rx="2"/><path d="m9 9 3-2 4 2m-1-2 1 2-2 1m1 3-3 2-4-2m1 2-1-2 2-1"/>',
      awake: '<path d="m5 7 3-4 4 3 4-3 3 4v9q-7 7-14 0z"/><circle cx="9" cy="11" r="2"/><circle cx="15" cy="11" r="2"/><path d="m10 16 2 2 2-2"/>',
      calculator: '<rect x="5" y="2" width="14" height="20" rx="3"/><path d="M8 6h8M8 11h1m3 0h1m3 0h1M8 15h1m3 0h1m3 0h1M8 19h1m3 0h1m3 0h1"/>',
      notepad: '<rect x="5" y="4" width="14" height="17" rx="2"/><path d="M8 2v4m4-4v4m4-4v4M8 10h8m-8 4h8m-8 4h5"/>',
      screenshot: '<path d="M5 7V3h14v4M5 17v4h14v-4M4 11l16 6M4 17l16-6"/><circle cx="4" cy="10" r="2"/><circle cx="4" cy="18" r="2"/>',
      clipboard: '<rect x="7" y="6" width="14" height="16" rx="2"/><path d="M15 3H3v15m8-8h6m-6 4h6m-6 4h4"/>',
      tools: '<path d="m12 3 9 5v9l-9 5-9-5V8zM3 8l9 5 9-5m-9 5v9"/>',
      battery: '<rect x="3" y="6" width="17" height="12" rx="2"/><path d="M23 10v4M7 10v4m4-4v4"/>',
      cpu: '<rect x="5" y="5" width="14" height="14" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M8 2v3m8-3v3M8 19v3m8-3v3M2 8h3m-3 8h3m14-8h3m-3 8h3"/>',
      search: '<circle cx="10" cy="10" r="6"/><path d="m15 15 6 6"/>',
      store: '<path d="M4 9h16l-1 12H5L4 9zM8 9V7a4 4 0 0 1 8 0v2"/>',
      translate: '<path d="M3 4h11M8 2v3m-3 3 7 7M12 5c-1 5-4 8-9 10m12-4-4 10m4-10 5 10m-7-3h5"/>',
      update: '<path d="M4 9a8 8 0 0 1 15-3l2 3m0-5v5h-5M20 15a8 8 0 0 1-15 3l-2-3m0 5v-5h5"/>',
      home: '<path d="m3 11 9-8 9 8M5 10v11h14V10m-10 11v-7h6v7"/>'
    };
    return `<svg viewBox="0 0 24 24" aria-hidden="true">${shapes[chosen] || shapes.device}</svg>`;
  }
};

document.addEventListener("error", event => {
  if (event.target?.classList?.contains("app-logo") && event.target.src.startsWith("https://xiaomi-custom-assets.local/")) {
    event.target.src = "https://xiaomi-assets.local/app.svg"; return;
  }
  if (!event.target?.classList?.contains("asset-icon")) return;
  const name = event.target.dataset.assetName;
  UI.failedAssetIcons.add(UI.quickIcons[name] || name);
  event.target.outerHTML = UI.icon(name, true);
}, true);

matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => { if (document.documentElement.dataset.followWindows === "true") UI.applyAppearance("system"); });
