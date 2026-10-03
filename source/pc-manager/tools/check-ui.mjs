// Purpose: static UI/native contract check, without launching the app or touching hardware.
// Dependencies: Node.js standard library. Output: PASS messages, or assertion failure.
// Command from project directory: node tools/check-ui.mjs
import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import vm from "node:vm";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = relative => readFileSync(path.join(root, relative), "utf8");
const native = read("MainWindow.cs") + read("Services/CapabilityRouter.cs");
const quickCatalog = vm.createContext({});
vm.runInContext(read("www/quick-tools.js"), quickCatalog);
const systemIds = [...vm.runInContext("QuickSystemTools.map(tool => tool.id)", quickCatalog)];
const nativeIds = [...read("Services/AppLinks.cs").match(/SystemActionIds = \[([^\]]+)\]/)[1].matchAll(/"([^"]+)"/g)].map(match => match[1]);
assert.deepEqual(systemIds, nativeIds, "Quick system actions must match native validation");
assert.equal(new Set(systemIds).size, systemIds.length);
const methods = new Set([...native.matchAll(/case "([a-z]+\.[a-zA-Z]+)"/g)].map(match => match[1]));
const shortcuts = new Set([...read("DesktopShortcuts.cs").matchAll(/"(Calculator|Notepad|Screenshot|Clipboard|Screen)"/g)].map(match => match[1]));
for (const file of ["bridge.js", "quick-tools.js", "quick.js", "app.js"]) {
  const source = read("www/" + file);
  new vm.Script(source, { filename: file }); // Parse only. Never execute frontend code.
  for (const [, method] of source.matchAll(/"([a-z]+\.[a-zA-Z]+)"/g)) {
    assert(methods.has(method) || /^page\./.test(method), `${file}: unknown native method ${method}`);
  }
  for (const [, name] of source.matchAll(/\{ name: "([^"]+)" \}/g)) {
    assert(shortcuts.has(name), `${file}: unknown shortcut ${name}`);
  }
  console.log("PASS syntax and literal commands:", file);
}
for (const [htmlFile, scriptFile] of [["quick.html", "quick.js"], ["index.html", "app.js"]]) {
  const html = read("www/" + htmlFile);
  const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
  assert.equal(ids.length, new Set(ids).size, `${htmlFile}: duplicate IDs`);
  for (const [, reference] of html.matchAll(/(?:src|href)="([^"#]+)"/g)) {
    const local = reference.startsWith('https://xiaomi-assets.local/') ? path.join(root, 'assets', reference.slice('https://xiaomi-assets.local/'.length)) : path.join(root, 'www', reference);
    assert(existsSync(local), `${htmlFile}: missing ${reference}`);
    if (reference.startsWith('https:')) assert(html.includes('https://xiaomi-assets.local') && read('MainWindow.cs').includes('"xiaomi-assets.local"'), 'App images must use an explicitly mapped local asset host');
  }
  for (const [, method] of html.matchAll(/data-native="([^"]+)"/g)) {
    assert(methods.has(method), `${htmlFile}: unknown native method ${method}`);
  }
  for (const [, name] of html.matchAll(/data-shortcut="([^"]+)"/g)) {
    assert(shortcuts.has(name), `${htmlFile}: unknown shortcut ${name}`);
  }
  if (htmlFile === "quick.html") {
    for (const [, id] of read("www/" + scriptFile).matchAll(/\$\("([^"]+)"\)/g)) {
      assert(ids.includes(id), `${scriptFile}: missing static element ${id}`);
    }
  }
  assert(html.includes('lang="en"'), `${htmlFile}: English language missing`);
  assert(html.includes("connect-src 'none'"), `${htmlFile}: unexpected network access`);
  console.log("PASS local assets, language, and markup commands:", htmlFile);
}
assert(read("Program.cs").includes("[STAThread]\n    private static void Main") || read("Program.cs").includes("[STAThread]\r\n    private static void Main"), "STAThread must annotate Main");
assert(read("Program.cs").includes('commands.Length == 0 ? "toggle"'), "Default entry must be the popup");
console.log("PASS popup entry contract. Compile and hardware validation remain separate.");
const actions = read("Vendor/XiControl/Ui/SettingsActions.cs");
const integration = read("Services/AdvancedControls.cs");
const mounted = integration.slice(integration.indexOf("private SettingsActions Actions()"), integration.indexOf("private void StartApi()"));
const callbacks = [...actions.matchAll(/public required [^;\r\n]+?\s+(\w+);/g)].map(match => match[1]);
assert(callbacks.length > 50, "Advanced callback contract unexpectedly empty");
for (const name of callbacks) assert(new RegExp(`\\b${name}\\s*=`).test(mounted), `Advanced setting callback missing: ${name}`);
assert(integration.includes("haptics = app.Hardware.SharedHaptics"), "Advanced and quick controls must share the haptic transport");
const http = read("Vendor/XiControl/SystemIntegration/HttpApi.cs");
assert(http.includes("MaxBodyBytes + 1") && http.includes("count > MaxBodyBytes"), "Chunked API body must have a byte ceiling");
assert(!http.includes("ReadToEnd()"), "API request body must not be unbounded");
assert(http.includes("WaitAsync(deadline.Token)"), "API reads must have a deadline");
assert(read("Services/XiaomiBridge.cs").includes("https://www.mi.com/service/notebook/drivers"), "Official driver destination missing");
const en = JSON.parse(read("Vendor/XiControl/Localization/en.json").replace(/^\uFEFF/, ""));
for (const name of ["manager.keys.enabled", "manager.awake.display", "manager.awake.lid"]) assert(en[name], `Missing English label: ${name}`);
for (const [, name] of read("Vendor/XiControl/Ui/SvgIcons.cs").matchAll(/public const string \w+ = "([^"]+)";/g)) {
  assert(existsSync(path.join(root, "Vendor/XiControl/Assets/svg", name + ".svg")), `Missing embedded SVG: ${name}`);
}
for (const name of ["travel-ready", "travel-on", "travel-off"]) assert(existsSync(path.join(root, "Vendor/XiControl/Assets/sound", name + ".wav")), `Missing sound: ${name}`);
console.log(`PASS all ${callbacks.length} advanced callbacks, shared haptics, official driver URL, and bounded API source contracts.`);
const quick = read("www/quick.html"), quickJs = read("www/quick.js"), hardware = read("Services/HardwareService.cs");
assert(!/Device Center|data-oem="connect"|class="quick-grid utilities"|data-shortcut=/.test(quick), "Removed popup rows returned");
assert(!/Share and devices|Sharing and interconnect|"share"/.test(read("www/app.js")), "Sharing returned in the secondary UI");
assert(quickJs.includes("source: snapshot?.hardware.powerSource"), "Popup mode requests must bind to the displayed source");
assert(hardware.includes("expectedSource != source.ToString()") && hardware.includes("firmware.GetPerfMode() != mode"), "Mode writes require source validation and firmware readback");
assert(integration.includes("profiles.ApplyMode = app.Hardware.ApplyProfileMode"), "Automatic writes must validate the source and saved profile under the shared transport lock");
assert(quick.includes('src="quick-tools.js"') && read("www/quick-tools.js").includes("Object.freeze(["), "Editable tools row missing");
const bridge = read("Services/XiaomiBridge.cs");
assert(bridge.includes('start.ArgumentList.Add("--scan_driver")') && !bridge.includes('--intent 20'), "Use the verified driver scan flag, never a guessed Toolbox CLI flag");
assert(bridge.includes("ProcessImage.PathFor(process.Id)") && bridge.includes("AutomationElement.ProcessIdProperty"), "OEM accessibility navigation must be scoped by executable and process");
for (const [, font] of read("www/fonts.css").matchAll(/url\('([^']+)'\)/g)) assert(existsSync(path.join(root, "www", font)), `Missing font: ${font}`);
console.log("PASS source-bound modes, OEM scan/navigation scope, removed rows, local fonts, and extension row contracts.");
const modeSource = read("Vendor/XiControl/Config/ModeVisibility.cs");
assert(modeSource.includes('Ac = [PerfMode.Quiet, PerfMode.Auto, PerfMode.FullSpeed, PerfMode.Eco]'), "Incorrect AC modes/order");
assert(modeSource.includes('Battery = [PerfMode.Eco, PerfMode.Quiet, PerfMode.Auto, PerfMode.Turbo]'), "Incorrect battery modes/order");
for (const file of ["www/quick.js", "www/app.js", "www/bridge.js"]) assert(!/Balance|Balanced/.test(read(file)), `${file}: legacy Balanced selection returned`);
assert(hardware.includes("!ModeVisibility.IsAvailable(mode, online)"), "Native mode writes must enforce the source-specific set");
assert(read("Vendor/XiControl/Ui/Settings/PerfTab.cs").includes("ModeVisibility.Available(ac)"), "Native profile controls must share source-specific modes");
assert(integration.includes("app.Hardware.ModeConfirmed = ShowPerformance") && !integration.includes("Flash(ModeUi.Kind(mode)"), "Mouse selections must use the Xiaomi performance OSD");
const osd = read("Services/XiaomiPerformanceOsd.cs");
for (const [, file] of osd.matchAll(/=> "([^"\n]+\.png)"/g)) assert(existsSync(path.join(root, "www/osd", file)), `Missing OEM OSD card ${file}`);
assert(osd.includes("OsdPlacement.LocateOsd(area, Size, position") && integration.includes("config.OsdPosition = OsdPosition.Bottom") && /(?:number|item\.Number)\.ToString\(\)/.test(osd) && !osd.includes("TransparencyKey"), "OSD must honor its position with a bottom-center default, numbered test badge, and no color key");
assert(read("Services/Preferences.cs").includes('Path.Combine(AppContext.BaseDirectory, "test-data")'), "Test settings must be separate");
assert(read("Program.cs").includes('TestMode ? ".Test"'), "Test resident must have a distinct identity");
assert(integration.includes("Program.TestMode ? new ApiSettings()") && integration.includes("Program.TestMode || !cfg.KeyboardRoutingEnabled"), "Test mode must isolate API and firmware-key routing");
assert(read("Vendor/XiControl/Ui/MonitorForm.cs").includes('"small" => ViewKind.Power, "medium" => ViewKind.Mini, "large" => ViewKind.Full'), "Monitor sizes must reuse the original XiControl views");
console.log("PASS exact AC/battery modes, Xiaomi numbered OSD, separate test identity/settings, and original monitor sizes.");
assert(read("Vendor/XiControl/SystemIntegration/Brightness.cs").includes('["Timeout"] = (uint)0'), "Brightness must not request a delayed transition");
assert(!native.includes("TrySuspendAsync") && native.includes("method == \"display.brightness\""), "Avoid explicit controller suspension and preserve independent brightness path");
assert(quickJs.includes('Native.call("quick.read")') && read("www/bridge.js").includes("latest = value"), "Quick panel must use light reads and coalesced brightness input");
assert(integration.includes("charge.ApplyLimit = app.Hardware.ApplyChargePolicy") && hardware.includes("wanted != percent"), "Stale charge policy must be rejected under the shared lock");
console.log("PASS immediate brightness, hidden frontend refresh guards, light popup reads, and guarded charging intent.");
assert(integration.includes("preferences.ChargeLimit is not null ? 100 : null"), "Unconfigured charging must stay unmanaged until an explicit selection");
assert(read("Program.cs").includes("XiaomiAIManager.Hardware."), "Daily/test residents must share a hardware-owner mutex");
console.log("PASS unmanaged fresh charging and single daily/test hardware owner.");
assert(bridge.includes('OemServiceControl.PrepareExplicitTools()'), "Explicit OEM actions must restore their service when isolated");
const serviceScript = read('tools/oem-service.ps1');
assert(serviceScript.includes('oem-service-original.json') && serviceScript.includes('oem-service-permissions.sddl') && serviceScript.includes('} finally {'), "OEM isolation must preserve service state and restore permissions");
assert(!read('Services/OemServiceControl.cs').includes('GetProcessesByName') && read('ManagerApplication.cs').includes('OemServiceControl.RestoreDailyIsolation();') && read('ManagerApplication.cs').includes('await Task.Run(Hardware.Initialize)'), "OEM startup isolation must not block hardware initialization or become an idle process killer");
console.log("PASS reversible OEM service state/permissions, explicit tool restoration, and no idle service polling.");
assert(serviceScript.includes("'--reapply-policies'") && read('ManagerApplication.cs').includes('Hardware.ReapplySavedPolicies()'), "Returning from OEM tools must reapply current saved firmware intent");
assert(serviceScript.includes('Original service permissions did not pass privileged readback.'), "Service permissions require privileged readback");
console.log("PASS explicit isolation notification and privileged permissions readback.");
assert(!quick.includes('id="charge-limit"') && !quick.includes('id="full-charge"') && quick.includes('id="travel-label"'), "Care limits must be manager-only, with an explicit cancel-travel label in the popup");
assert(!read('www/app.js').includes('"Advanced settings"') && !read('www/app.js').includes('"More settings"'), "Duplicate settings surfaces returned");
assert(native.includes('!Trusted(web.CoreWebView2.Source)') && !native.includes('app.Exiting || !webReady || web.CoreWebView2 is null'), "Early trusted replies must not be dropped before NavigationCompleted");
assert(read('ManagerApplication.cs').includes('popup.PreloadAsync()') && quickJs.includes('"visibilitychange"'), "Resident must preload and refresh the popup on visibility changes");
const configSource = read('Vendor/XiControl/Config/AppConfig.cs');
const propertyNames = new Set([...configSource.matchAll(/public [^\r\n;]+?\s+(\w+)\s*\{ get; set; \}/g)].map(m => m[1]));
for (const [, name] of read('Services/ManagerSettings.cs').split('private List<Setting> BuildSettings()')[1].matchAll(/(?:Add|Bool|Int|Choice|Text)\("([^"]+)"/g)) assert(propertyNames.has(name), `Manager setting has no backend property: ${name}`);
assert(read('Services/ManagerSettings.cs').includes('KeyMap.IsKnownSlot') && read('Services/AppLinks.cs').includes('UnelevatedShell(pid)'), "Key-map validation and normal-user app launch boundary required");
console.log("PASS popup readiness, manager-only care limits, consolidated settings schema and app-link boundaries.");
const screenOff = read("ManagerApplication.cs").split("internal async void ScreenOff()")[1]?.split("internal Task<object> ProbePopupAsync")[0] || "";
assert(screenOff.includes("ManualDisplay.RequestIdleDisplayOff()") && !screenOff.includes("PostMessage(") && !screenOff.includes("ManualDisplay.Prepare"), "Screen off must use the restorable idle-display path without changing sign-in policy");
assert(read("Services/ManualScreenOff.cs").includes("manual-display-idle.json") && read("Services/ManualScreenOff.cs").includes("RestoreIdle()"), "The temporary display timeout must have crash and wake restoration");
assert(read("www/quick-tools.js").includes('id: "screenoff"') && read("www/quick-tools.js").includes('method: "window.screenOff"') && read("www/app.js").includes('button("Screen off now", "window.screenOff")'), "Both display controls must expose Screen off");
assert(read("Vendor/XiControl/Ui/OsdForm.cs").includes("OsdPlacement.LocateOsd") && read("Services/XiaomiPerformanceOsd.cs").includes("OsdPlacement.LocateOsd"), "Both OSD renderers must use card-relative placement without moving the popup");
assert(read("www/bridge.js").includes("keyboard: '<rect") && read("www/app.js").includes('keyboard: ["Keyboard", "keyboard"]'), "Keyboard navigation needs its own icon");
assert(read("ManagerApplication.cs").includes("popup.LastDismissStartedMs") && read("MainWindow.cs").includes("PopupWorkArea(Screen screen) => screen.Bounds"), "Tray toggle and popup placement must account for the taskbar click");
console.log("PASS restorable Screen off and shared OSD clearance.");
