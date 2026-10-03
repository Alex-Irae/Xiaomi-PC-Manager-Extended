// Purpose: expose XiControl settings in the manager's own categorized English UI.
// Dependencies: the resident's existing AppController, guards and AppConfig; no new packages.
// Outputs: validated settings/readbacks through settings.read and settings.apply.
// Command: app/PCManager.exe --manager, then select a settings category.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using XiControl.Config;
using XiControl.Input;
using XiControl.SystemIntegration;
using XiControl.Ui;
using XiControl.Wmi;

namespace XiaomiAIManager.Services;

public sealed partial class AdvancedControls
{
    private sealed record Setting(string Key, string Group, string Label, string Kind, Func<object?> Read,
        Action<JsonElement> Write, double Min = 0, double Max = 0, string[]? Options = null, string Description = "");
    private List<Setting>? definitions;
    internal static readonly string[] KeyAppSlots = ["MiClick", "MiDouble", "MiHold", "SettingsKey", "AiKey", "ProjKey"];
    private static readonly JsonSerializerOptions SettingJson = new()
    { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    private List<Setting> Definitions => definitions ??= BuildSettings();
    private static AppConfig Defaults() => new()
    {
        Language = "en", FlyoutTheme = "light", CheckUpdates = false, AutoStart = true,
        MiClickAction = "panel", MiDoubleAction = "page.home", MiHoldAction = "panel",
        SettingsKeyAction = "windowssettings", AiKeyAction = "xiaoai", ProjKeyAction = "projection",
        OsdPosition = OsdPosition.Bottom, MonitorView = "", MonitorCompactMetric = "power", TrayMetricKind = "power"
    };
    private static object DefaultValue(string key)
    {
        if (key.StartsWith("Visible.") || key.StartsWith("LockOsd.")) return true;
        if (key.StartsWith("Api.")) return typeof(ApiSettings).GetProperty(key[4..])!.GetValue(new ApiSettings()) ?? "";
        return key switch
        {
            "StartupStrategy" => "Profiles", "SlideStrength" => "60",
            "AutoBrightnessPointsAc" or "AutoBrightnessPointsBattery" => BrightnessCurve.DefaultPoints(),
            "CycleRefreshRates" => Array.Empty<int>(), "KeyCodes" => new Dictionary<string, string>(),
            _ => typeof(AppConfig).GetProperty(key)!.GetValue(Defaults()) ?? ""
        };
    }
    private static string Help(Setting s) => s.Description.Length > 0 ? s.Description : s.Key switch
    {
        "BrightnessRampMs" => "Time for a brightness-cap adjustment. This affects the optional cap, not the manual slider.",
        "BrightnessConvergeMs" => "Pause between steps when moving brightness back towards its cap.",
        "BrightnessBackoffMin" => "Pause the cap after a manual override so it does not immediately undo your choice.",
        "BrightnessGapDivisor" => "Each cap step closes this fraction of the remaining brightness gap. Larger is gentler.",
        "BrightnessSnapPercent" => "Finish the cap adjustment when the remaining gap is this small.",
        "AutoBrightnessDeadband" => "Ignore predicted brightness changes smaller than this percentage gap.",
        "AutoBrightnessSettleMs" => "Wait for the ambient-light prediction to settle before applying it.",
        "AutoBrightnessLearnMs" => "Wait after your manual adjustment before learning from it.",
        "AutoBrightnessHysteresis" => "Fractional light change needed to accept a new prediction; 0.1 means 10%.",
        "AutoBrightnessLearnBlend" => "How strongly a small manual correction moves the learned curve; 0.5 uses half the correction.",
        "AutoBrightnessFineStep" => "Manual changes within this brightness gap are blended instead of fully replacing a point.",
        "AutoBrightnessRevertMs" => "Delay before returning to the learned curve after a manual change.",
        "AutoBrightnessRevertBackoffMin" => "Longer pause after repeated manual overrides of automatic brightness.",
        "AutoBrightnessMedianSec" => "Smooth sensor noise across this time window. Zero disables smoothing.",
        "AutoRestart" => "Windows checks every minute and starts the resident again if it has exited. Turn this off before deliberately exiting for longer.",
        "StartupStrategy" => "Profiles uses separate AC/battery choices; Restore uses the last mode; Pin uses the fixed startup mode.",
        "TouchpadEdgeHapticsMs" => "Minimum pause between haptic clicks during edge swipes; a higher value reduces clicks.",
        "CycleRefreshRates" => "An empty list cycles all supported internal-panel rates.",
        _ => s.Kind == "number" || s.Kind == "decimal" ? $"Allowed range: {s.Min} to {s.Max}." : ""
    };
    internal async Task<object> ResetCategoryAsync(string group)
    {
        var rows = Definitions.Where(s => s.Group == group).ToArray();
        if (rows.Length == 0) throw new ArgumentException("Choose a listed settings category.");
        var problems = new List<string>();
        async Task HardwareDefault(string label, Action action)
        {
            try { await Task.Run(() => { lock (app.Hardware.Sync) action(); }); }
            catch (Exception ex) { problems.Add(label + ": " + ex.Message); }
        }
        // Turn automatic policies off before resetting their parameters, then use actual device callbacks.
        if (group == "display") { controller.SetAutoBrightness(false); controller.SetBrightnessCap(false); }
        if (group == "settings") { api.Enabled = false; ApiApplied(); }
        foreach (var row in rows)
        {
            if (Program.TestMode && (row.Key is "AutoStart" or "AutoRestart" || row.Key.StartsWith("Api."))) continue;
            if (row.Key == "SlideStrength" && controller.TouchpadHaptics is null) { problems.Add("Edge-swipe strength: haptic device unavailable."); continue; }
            try { await ApplySettingAsync(row.Key, JsonSerializer.SerializeToElement(DefaultValue(row.Key), SettingJson)); }
            catch (Exception ex) { problems.Add(row.Label + ": " + ex.Message); }
        }
        if (group == "performance") await HardwareDefault("Performance", () => app.Hardware.SetPowerProfiles("Auto", "Auto"));
        if (group == "battery") await HardwareDefault("Battery care", () => { if (app.Preferences.TravelReturnLimit is not null) app.Hardware.SetTravel(false); app.Hardware.SetChargeLimit(80); });
        if (group == "display") await HardwareDefault("Display", () =>
        {
            var rates = RefreshRate.Supported();
            if (rates.Length > 0) { app.Hardware.SetAutomaticDisplay(false, rates.Max(), rates.Min()); app.Hardware.SetRefresh(rates.Min()); }
            app.Hardware.SetBrightness(50);
        });
        if (group == "touchpad")
        {
            foreach (string device in new[] { "touchpad", "touchscreen" }) await HardwareDefault(device, () => app.Hardware.SetInput(device, true));
            await HardwareDefault("Haptic strength", () => app.Hardware.SetVibration("Medium"));
            await HardwareDefault("Click force", () => app.Hardware.SetPressure(125));
        }
        if (group == "keyboard")
        {
            app.SaveShortcuts(JsonSerializer.SerializeToElement(new { shortcuts = Array.Empty<KeyboardShortcut>() }));
            app.SaveCopilotShortcut(JsonSerializer.SerializeToElement(new { action = "none" }));
        }
        if (group == "settings")
        {
            var p = app.Preferences;
            p.Appearance = "light"; p.CloseToTray = true; p.MinimizeToTray = false; p.PopupPosition = OsdPosition.BottomRight; p.PopupOffsetX = p.PopupOffsetY = 0; p.QuickIcons.Clear();
            p.QuickSystemActions = ["screenoff", "awake", "refresh", "autorefresh", "touchpad", "touchscreen", "monitor", "sleepoff"];
            p.PopupScale = 85; p.CompactPanel = true; p.PopupAnimations = true; p.ThemePreset = "blue"; p.ThemeAccent = "#3482ff";
            p.ThemeBackground = "#1f242c"; p.ThemeSurface = "#2b333f"; p.DeveloperMode = false;
            p.OriginalPopupEnabled = false; p.OsdStyle = "xiaomi";
            SetAwake(false); app.SetPreventSleep(false); p.Save();
            app.ApplyAppearance();
        }
        ConfigurationChanged(); app.RefreshWindows();
        return new { message = problems.Count == 0 ? "Category defaults restored." : "Defaults restored where available. " + string.Join(" ", problems), group, problems, complete = problems.Count == 0 };
    }

    private string[] DefaultErrors() => Definitions.Select(row =>
    {
        try { ValidateSetting(row, JsonSerializer.SerializeToElement(DefaultValue(row.Key), SettingJson)); return null; }
        catch (Exception ex) { return row.Key + ": " + ex.Message; }
    }).Where(error => error is not null).Cast<string>().ToArray();
    internal object ReadSettings() => new
    {
        rows = Definitions.Select(s => new { s.Key, s.Group, s.Label, s.Kind, s.Min, s.Max, s.Options, description = Help(s), value = s.Read(), defaultValue = DefaultValue(s.Key) }).ToArray(),
        keys = KeyStatus, alsAvailable = als.Available, lux = float.IsFinite(als.LastLux) ? (float?)als.LastLux : null,
        startupRegistered = controller.AutoStartEnabled, dataDirectory = Preferences.DataDirectory,
        keyApps = app.Preferences.KeyAppLinks, shortcuts = app.Preferences.Shortcuts, shortcutError = app.ShortcutError,
        copilot = app.Preferences.CopilotShortcut, copilotError = app.CopilotError, copilotInterceptCount = app.CopilotInterceptCount
        , companions = new { store = XiaomiBridge.LocateCompanion("store", app.Preferences), xiaoai = XiaomiBridge.LocateCompanion("ai", app.Preferences) }
        , defaultErrors = DefaultErrors()
    };
    internal async Task RestoreSettingsAsync(Dictionary<string, JsonElement> values)
    {
        foreach (var (key, value) in values)
        {
            if (value.ValueKind == JsonValueKind.Null) continue; // Unavailable device values cannot be written.
            if (key.EndsWith("Command", StringComparison.Ordinal) && KeyAppSlots.Contains(key[..^7]))
            {
                // This value came from native history, including trusted app-picker mappings.
                typeof(AppConfig).GetProperty(key)!.SetValue(cfg, value.GetString()); cfg.Save();
            }
            else await ApplySettingAsync(key, value);
        }
        ConfigurationChanged();
    }
    internal object AssignKeyApp(string slot, IWin32Window owner)
    {
        if (!KeyAppSlots.Contains(slot)) throw new ArgumentException("Choose a listed keyboard action.");
        var link = AppLinks.Pick(owner);
        if (link is null) return new { message = "App selection cancelled. The previous mapping is unchanged." };
        lock (app.Hardware.Sync)
        {
            app.Preferences.KeyAppLinks[slot] = link;
            typeof(AppConfig).GetProperty(slot + "Action")!.SetValue(cfg, "app");
            typeof(AppConfig).GetProperty(slot + "Command")!.SetValue(cfg, slot);
            cfg.Save();
        }
        ConfigurationChanged(); app.RefreshWindows();
        return new { message = "Key mapped to " + link.Label + "." };
    }
    internal object ApplySetting(string key, JsonElement value)
    {
        WriteSetting(key, value);
        return FinishSetting(key);
    }
    internal async Task<object> ApplySettingAsync(string key, JsonElement value)
    {
        // Execution-state requests belong to their calling thread; monitor palette callbacks
        // also touch native windows. All transport, disk and startup-task work uses a worker.
        if (key is "OwlMode" or "FlyoutTheme") WriteSetting(key, value);
        else await Task.Run(() => WriteSetting(key, value));
        if (key == "FlyoutTheme")
        {
            app.Preferences.Appearance = value.GetString()!;
            app.Preferences.Save();
            app.ApplyAppearance();
        }
        return FinishSetting(key);
    }
    private void WriteSetting(string key, JsonElement value)
    {
        var setting = Definitions.SingleOrDefault(s => s.Key == key) ?? throw new ArgumentException("Unknown setting.");
        ValidateSetting(setting, value);
        if (value.ValueKind == JsonValueKind.String && value.GetString() == "app" && key.EndsWith("Action", StringComparison.Ordinal)
            && !app.Preferences.KeyAppLinks.ContainsKey(key[..^6])) throw new ArgumentException("Choose an app for this key first.");
        lock (app.Hardware.Sync)
        {
            if (key.EndsWith("Command", StringComparison.Ordinal) && KeyAppSlots.Contains(key[..^7])
                && typeof(AppConfig).GetProperty(key[..^7] + "Action")!.GetValue(cfg) as string == "app")
                throw new ArgumentException("Use Choose app to change this mapping.");
            setting.Write(value);
            if (key.EndsWith("Action", StringComparison.Ordinal) && value.ValueKind == JsonValueKind.String && value.GetString() == "app")
            {
                typeof(AppConfig).GetProperty(key[..^6] + "Command")!.SetValue(cfg, key[..^6]); cfg.Save();
            }
        }
    }
    private object FinishSetting(string key)
    {
        if (key is "MonitorView" or "MonitorCompactMetric" or "ForceAcpiTemperature")
        {
            bool visible = monitor?.Visible == true;
            monitor?.Hide();
            monitor?.Dispose();
            monitor = null;
            if (visible) ShowMonitor(null);
        }
        ConfigurationChanged();
        app.RefreshWindows();
        return new { message = "Change requested. Settings and device state refresh after confirmation.", key, value = Definitions.Single(s => s.Key == key).Read() };
    }
    private static void ValidateSetting(Setting s, JsonElement value)
    {
        switch (s.Kind)
        {
            case "toggle":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Choose on or off.");
                break;
            case "number": case "decimal":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n) || n < s.Min || n > s.Max || (s.Kind == "number" && n != Math.Truncate(n)))
                    throw new ArgumentException($"Choose a value between {s.Min} and {s.Max}.");
                break;
            case "choice":
                if (value.ValueKind != JsonValueKind.String || s.Options?.Contains(value.GetString()) != true) throw new ArgumentException("Choose a listed setting value.");
                break;
            case "text":
                if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 1024 || value.GetString()!.Contains('\0')) throw new ArgumentException("Text must be at most 1024 characters.");
                break;
            case "curve":
                var points = value.Deserialize<List<BrightnessPoint>>(SettingJson);
                if (points is null || points.Count is < 2 or > 32 || points.Any(p => !float.IsFinite(p.Lux) || p.Lux < 0 || p.Lux > 200000 || p.Percent is < 1 or > 100)
                    || points.Select(p => p.Lux).Distinct().Count() != points.Count) throw new ArgumentException("Use 2 to 32 distinct lux points, with brightness from 1% to 100%.");
                break;
            case "keymap":
                var codes = value.Deserialize<Dictionary<string, string>>(SettingJson);
                if (codes is null || codes.Count > 32 || codes.Any(p => !KeyMap.IsKnownSlot(p.Key) || KeyMap.ParseCode(p.Value) is null)
                    || codes.Values.Select(KeyMap.ParseCode).Distinct().Count() != codes.Count) throw new ArgumentException("Use named firmware keys and distinct byte codes such as 0x1B.");
                break;
            case "rates":
                var rates = value.Deserialize<List<int>>();
                if (rates is null || rates.Count > 16 || rates.Distinct().Count() != rates.Count || rates.Any(r => !RefreshRate.Supported().Contains(r))) throw new ArgumentException("Choose rates supported by the internal display.");
                break;
            default: throw new ArgumentException("Unknown setting type.");
        }
    }
    private List<Setting> BuildSettings()
    {
        var rows = new List<Setting>();
        void Add(string key, string group, string label, string kind = "toggle", double min = 0, double max = 0,
            string[]? options = null, Action<JsonElement>? apply = null, string description = "")
        {
            var property = typeof(AppConfig).GetProperty(key) ?? throw new InvalidOperationException("Missing setting " + key);
            rows.Add(new(key, group, label, kind, () => property.GetValue(cfg), apply ?? (v =>
            {
                property.SetValue(cfg, v.Deserialize(property.PropertyType, SettingJson));
                cfg.Save();
            }), min, max, options, description));
        }
        void Bool(string key, string group, string label, Action<bool> apply) => Add(key, group, label, apply: v => apply(v.GetBoolean()));
        void Int(string key, string group, string label, int min, int max, Action<int>? apply = null) => Add(key, group, label, "number", min, max, apply: apply is null ? null : v => apply(v.GetInt32()));
        void Choice(string key, string group, string label, string[] options, Action<string>? apply = null) => Add(key, group, label, "choice", options: options, apply: apply is null ? null : v => apply(v.GetString()!));
        void Text(string key, string group, string label) => Add(key, group, label, "text");
        Bool("AutoStart", "settings", "Start in the background at Windows sign-in", on => { if (Program.TestMode) throw new InvalidOperationException("Startup is disabled in test mode."); controller.ToggleAutoStart(on); });
        Add("LogEnabled", "settings", "Save diagnostic logs");
        Bool("AutoRestart", "settings", "Restart automatically if the app exits", on =>
        {
            cfg.AutoRestart = on; cfg.Save(); AutoStart.RestartIfClosed = on;
            if (!Program.TestMode && cfg.AutoStart) { AutoStart.Set(true); if (!AutoStart.IsEnabled()) throw new InvalidOperationException("Windows did not confirm resident startup."); }
        });
        Add("AutoCleanupOem", "settings", "Clean up Xiaomi tools after their main window closes", description: "Stop their verified background UI and return to daily service isolation. Active installers delay cleanup. Turn off to leave OEM tools running.");
        Add("CheckUpdates", "settings", "Notify about upstream XiControl releases", apply: v => controller.SetCheckUpdates(v.GetBoolean()),
            description: "Checks GitHub at most daily against the bundled XiControl 0.16.0 component. A notification only; it does not download or replace code.");
        Choice("FlyoutTheme", "notifications", "Manager and monitor theme", ["light", "dark", "system"], controller.SetFlyoutTheme);

        rows.Add(new("StartupStrategy", "performance", "Mode selection at startup", "choice", () => controller.CurrentStartStrategy.ToString(),
            v => controller.SetStartStrategy(Enum.Parse<StartStrategy>(v.GetString()!)), Options: ["None", "Profiles", "Restore", "Pin"]));
        foreach (bool ac in new[] { true, false })
            foreach (var mode in ModeVisibility.Available(ac))
            {
                bool source = ac; var value = mode;
                rows.Add(new($"Visible.{(ac ? "ac" : "battery")}.{mode}", "performance", $"Show {ModeVisibility.Label(mode, ac)} on {(ac ? "AC" : "battery")}", "toggle",
                    () => !controller.HiddenModesFor(source).Contains(value), v => controller.SetModeVisible(value, v.GetBoolean(), source)));
            }

        Add("SoftChargeAlert", "battery", "Battery-level reminder", description: "A software reminder does not stop charging.");
        Int("SoftChargeLimitPercent", "battery", "Reminder threshold (%)", 50, 95);
        Add("SoftChargeAlertSound", "battery", "Sound for battery-level reminder");
        Int("SoftChargeRepeatMin", "battery", "Reminder interval (minutes)", 1, 240);
        Int("SoftChargeRepeatMax", "battery", "Maximum repeat reminders", 0, 20);
        Add("TravelSound", "battery", "Sound when travel charging reaches full");
        Add("TravelLockSound", "battery", "Travel sound while Windows is locked");
        Add("TravelLockToast", "battery", "Travel toast while Windows is locked");
        Text("TravelSoundFile", "battery", "Custom travel sound (PCM WAV path)");
        Add("ChargerWattsOsd", "battery", "Show adapter rating when connected");
        Int("WeakChargerWatts", "battery", "Low-power adapter warning threshold (W)", 0, 240);

        Bool("RememberBrightness", "display", "Remember separate AC and battery brightness", controller.SetRememberBrightness);
        Bool("BrightnessCapEnabled", "display", "Limit brightness by power source", controller.SetBrightnessCap);
        Int("BrightnessCapAc", "display", "AC brightness cap (%)", 1, 100, n => controller.SetBrightnessCaps(n, cfg.BrightnessCapBattery));
        Int("BrightnessCapBattery", "display", "Battery brightness cap (%)", 1, 100, n => controller.SetBrightnessCaps(cfg.BrightnessCapAc, n));
        Bool("AutoBrightness", "display", "Ambient-light automatic brightness", on => { if (on && !als.Available) throw new InvalidOperationException("No supported light sensor is reporting."); controller.SetAutoBrightness(on); });
        Bool("AutoBrightnessLearning", "display", "Learn from manual brightness changes", controller.SetAutoBrightnessLearning);
        Choice("AutoBrightnessRevert", "display", "Return to the learned brightness", ["", "battery", "off"], n => controller.SetAutoBrightnessRevert(n.Length == 0 ? null : n));
        Int("AutoBrightnessMedianSec", "display", "Light-sensor smoothing window (seconds)", 0, 600, controller.SetBrightnessMedianSec);
        foreach (bool ac in new[] { true, false })
        {
            bool source = ac;
            rows.Add(new(ac ? "AutoBrightnessPointsAc" : "AutoBrightnessPointsBattery", "display", (ac ? "AC" : "Battery") + " brightness curve", "curve",
                () => controller.BrightnessCurvePoints(source), v => controller.SetBrightnessCurve(source, v.Deserialize<List<BrightnessPoint>>(SettingJson)!)));
        }
        Int("BrightnessRampMs", "display", "Brightness cap ramp (ms)", 0, 120000);
        Int("BrightnessConvergeMs", "display", "Brightness cap convergence (ms)", 0, 600000);
        Int("BrightnessBackoffMin", "display", "Brightness cap pause after manual override (minutes)", 0, 1440);
        Int("BrightnessGapDivisor", "display", "Brightness cap step divisor", 1, 20);
        Int("BrightnessSnapPercent", "display", "Brightness cap final step (%)", 1, 20);
        Int("AutoBrightnessDeadband", "display", "Automatic brightness deadband (%)", 0, 30);
        Int("AutoBrightnessSettleMs", "display", "Automatic brightness settling (ms)", 0, 60000);
        Int("AutoBrightnessLearnMs", "display", "Brightness learning delay (ms)", 0, 60000);
        Add("AutoBrightnessHysteresis", "display", "Light-change hysteresis", "decimal", 0, 1);
        Add("AutoBrightnessLearnBlend", "display", "Brightness learning blend", "decimal", 0, 1);
        Int("AutoBrightnessFineStep", "display", "Brightness learning adjustment step", 1, 100);
        Int("AutoBrightnessRevertMs", "display", "Return to learned brightness delay (ms)", 0, 600000);
        Int("AutoBrightnessRevertBackoffMin", "display", "Learned brightness pause (minutes)", 0, 1440);
        Bool("RefreshRateFeature", "display", "Enable display-rate controls", controller.ToggleRefreshRateFeature);
        // Legacy HoldRefreshRate stays in saved config for compatibility; manual overrides are authoritative.
        Add("CycleRefreshRates", "display", "Rates included in keyboard cycling", "rates");

        Add("TouchpadFeature", "touchpad", "Show touchpad controls");
        Add("TouchscreenFeature", "touchpad", "Show touchscreen controls");
        Add("TouchpadKeepOff", "touchpad", "Keep touchpad disabled after restarting", description: "Off restores an input device disabled by this app on its next launch. On keeps your disabled device off, including after Windows restarts.");
        Add("TouchscreenKeepOff", "touchpad", "Keep touchscreen disabled after restarting", description: "Off restores an input device disabled by this app on its next launch. On keeps your disabled device off, including after Windows restarts.");
        Bool("TouchpadDeadZone", "touchpad", "Ignore touches at the bottom edge", controller.SetTouchpadDeadZone);
        Int("TouchpadDeadZoneMm", "touchpad", "Bottom-edge dead zone (mm)", 1, 40, controller.SetTouchpadDeadZoneMm);
        Bool("TouchpadEdgeSliders", "touchpad", "Brightness and volume edge swipes", controller.SetTouchpadEdgeSliders);
        Int("TouchpadEdgeWidthMm", "touchpad", "Edge swipe width (mm)", 4, 30, controller.SetTouchpadEdgeWidthMm);
        Int("TouchpadEdgeSwipesPerRange", "touchpad", "Swipes across the full adjustment range", 1, 3, controller.SetTouchpadEdgeSwipes);
        Bool("TouchpadEdgeSwap", "touchpad", "Swap brightness and volume edges", controller.SetTouchpadEdgeSwap);
        Bool("TouchpadEdgeHaptics", "touchpad", "Haptic feedback for edge swipes", controller.SetTouchpadEdgeHaptics);
        Int("TouchpadEdgeHapticsMs", "touchpad", "Minimum time between swipe feedback (ms)", 50, 500, controller.SetTouchpadEdgeHapticsMs);
        rows.Add(new("SlideStrength", "touchpad", "Edge-swipe haptic strength", "choice", () => controller.TouchpadHaptics?.Slide.ToString(),
            v => controller.SetTouchpadSlideStrength(int.Parse(v.GetString()!)),
            Options: TouchpadHapticsProtocol.SlidePresets.Select(n => n.ToString()).ToArray()));
        string[] actions = ["none", "panel", "modes", "charge", "travel", "settings", "windowssettings", "owl", "monitor", "touchpad", "touchscreen", "autobright", "hz", "screenoff", "projection", "screenshot", "taskview", "copilot", "xiaoai", "play", "next", "prev", "stop", "calc", "launch"];
        actions = [.. actions, .. KeyRouter.ManagerPages.Select(p => "page." + p)];
        Choice("TouchpadHeavyPressAction", "touchpad", "Firm-press action", actions, controller.SetTouchpadHeavyPress);
        Add("TouchpadHeavyPressCommand", "touchpad", "Firm-press custom command", "text", apply: v => controller.SetTouchpadHeavyPressCommand(v.GetString()));
        Bool("OwlMode", "settings", "Enable Stay awake controls", controller.ToggleOwlFeature);
        Add("OwlIgnoreDisplay", "settings", "Stay awake also keeps the display on");
        Add("AwakeOverrideLid", "settings", "Stay awake overrides AC lid sleep", description: "The previous AC lid action is saved and restored when Stay awake ends.");

        Add("KeyboardRoutingEnabled", "keyboard", "Handle Xiaomi firmware keys without the OEM manager");
        Add("HandleScreenshotKey", "keyboard", "Use F7 for screenshots instead of XiaoAI", description: "On opens Windows Snipping Tool. Off opens the latest installed XiaoAI. The dedicated Mi key opens the popup.");
        Int("MiHoldMs", "keyboard", "Mi key hold time (ms)", 150, 2000);
        Int("MiDoubleClickMs", "keyboard", "Mi key double-click interval (ms)", 100, 1000);
        foreach (var (id, label) in new[] { ("MiClick", "Mi key press"), ("MiDouble", "Mi key double press"), ("MiHold", "Mi key hold"), ("SettingsKey", "Settings key"), ("AiKey", "AI key"), ("ProjKey", "Projection key") })
        { Choice(id + "Action", "keyboard", label + " action", [.. actions, "app"]); Text(id + "Command", "keyboard", label + " custom command"); }
        Add("KeyCodes", "keyboard", "Firmware event-code overrides", "keymap", description: "Use the last observed event shown above. Example: {\"Settings\":\"0x1B\"}. An empty object restores the model defaults.");

        Add("PerformanceOsdEnabled", "notifications", "Performance notifications");
        Choice("OsdPosition", "notifications", "Position for all notifications", Enum.GetNames<OsdPosition>(), n => { cfg.OsdPosition = Enum.Parse<OsdPosition>(n); cfg.Save(); });
        Int("OsdDurationMs", "notifications", "Notification duration (ms)", 500, 10000, n => { cfg.OsdDurationMs = n; cfg.Save(); });
        foreach (var key in Enum.GetValues<LockOsd>())
        {
            var name = key;
            rows.Add(new("LockOsd." + key, "notifications", key + " notifications", "toggle", () => !cfg.HiddenLockOsd.Contains(name),
                v => { if (v.GetBoolean()) cfg.HiddenLockOsd.Remove(name); else if (!cfg.HiddenLockOsd.Contains(name)) cfg.HiddenLockOsd.Add(name); cfg.Save(); }));
        }
        Add("TrayMetricEnabled", "monitor", "Show a hardware metric in the tray");
        Choice("TrayMetricKind", "monitor", "Tray metric", ["power", "cpu", "gpu", "ram", "temp"]);
        Int("TrayMetricPeriodSec", "monitor", "Tray metric interval (seconds)", 1, 60);
        Add("ForceAcpiTemperature", "monitor", "Use ACPI temperature instead of DPTF");
        Choice("MonitorView", "monitor", "Default monitor view", ["", "power", "mini"]);
        Choice("MonitorCompactMetric", "monitor", "Small monitor metric", ["power", "cpu", "gpu", "ram", "temp"]);
        foreach (var (key, label, kind, min, max) in new[] {
            ("Enabled", "Enable HTTP API", "toggle", 0, 0), ("Port", "HTTP API port", "number", 1024, 65535),
            ("LanAccess", "Allow access from the local network", "toggle", 0, 0), ("AllowMode", "API may change performance mode", "toggle", 0, 0),
            ("AllowCare", "API may change charge care", "toggle", 0, 0), ("AllowTravel", "API may change travel charging", "toggle", 0, 0),
            ("AllowOwl", "API may change Stay awake", "toggle", 0, 0), ("WebhookOnChargeLimit", "Send battery-limit webhook", "toggle", 0, 0), ("WebhookUrl", "Battery-limit webhook URL", "text", 0, 0) })
        {
            var property = typeof(ApiSettings).GetProperty(key)!;
            rows.Add(new("Api." + key, "settings", label, kind, () => property.GetValue(api), v =>
            {
                if (Program.TestMode) throw new InvalidOperationException("Network features are disabled in test mode.");
                if (key == "WebhookUrl" && v.GetString() is { Length: > 0 } url && !Webhook.IsAllowed(url)) throw new ArgumentException("Use a supported HTTP or HTTPS webhook URL.");
                property.SetValue(api, v.Deserialize(property.PropertyType)); ApiApplied();
            }, min, max));
        }
        return rows;
    }
    internal object GenerateApiToken()
    {
        if (Program.TestMode) throw new InvalidOperationException("API tokens are disabled in test mode.");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        api.TokenSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        ApiApplied();
        return new { token, message = "Save this token now. Only its SHA-256 hash is stored." };
    }
    internal void PreviewLockOsd() => Flash(OsdKind.CapsLockOn, "Caps Lock on", "Notification preview");
    internal object ProbeNotificationPlacement()
    {
        var results = new List<object>();
        var saved = cfg.OsdPosition;
        var area = OsdPlacement.TargetScreen().WorkingArea;
        try
        {
            foreach (var position in new[] { OsdPosition.Top, OsdPosition.Bottom })
            {
                cfg.OsdPosition = position;
                PreviewLockOsd();
                Form lockForm = app.Preferences.OsdStyle == "xiaomi" ? performanceOsd : osd;
                Rectangle lockBounds = lockForm.Bounds; bool lockVisible = lockForm.Visible;
                performanceOsd.Flash(PerfMode.Auto, true, 500, position);
                bool InExpectedHalf(Rectangle bounds) => position == OsdPosition.Top ? bounds.Bottom < area.Top + area.Height / 2 : bounds.Top > area.Top + area.Height / 2;
                results.Add(new { position = position.ToString(), lockBounds, performanceBounds = performanceOsd.Bounds,
                    pass = lockVisible && performanceOsd.Visible && InExpectedHalf(lockBounds) && InExpectedHalf(performanceOsd.Bounds)
                        && performanceOsd.BackColor.GetBrightness() < 0.3f, performanceBackground = performanceOsd.BackColor.Name });
            }
        }
        finally { cfg.OsdPosition = saved; osd.Position = saved; osd.Hide(); performanceOsd.Hide(); }
        return new { area, results, limitations = "Native form bounds and visibility, not optical screenshot validation or a physical Caps Lock key." };
    }
    internal static void CheckSettingValidation()
    {
        Setting Spec(string kind, double min = 0, double max = 0, string[]? options = null) => new("check", "check", "check", kind, () => null, _ => { }, min, max, options);
        void Check(Setting spec, string json, bool accepts)
        {
            bool actual;
            try { using var value = JsonDocument.Parse(json); ValidateSetting(spec, value.RootElement); actual = true; }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException) { actual = false; }
            if (actual != accepts) throw new InvalidOperationException($"Setting validation mismatch for {spec.Kind}: {json}");
        }
        var percent = Spec("number", 1, 100);
        foreach (string value in new[] { "1", "100" }) Check(percent, value, true);
        foreach (string value in new[] { "0", "101", "1.5", "\"50\"", "null", "{}" }) Check(percent, value, false);
        Check(Spec("toggle"), "false", true); Check(Spec("toggle"), "0", false);
        Check(Spec("choice", options: ["Bottom", "Top"]), "\"Top\"", true);
        Check(Spec("choice", options: ["Bottom", "Top"]), "\"8\"", false);
        Check(Spec("keymap"), "{\"Settings\":\"0x1B\"}", true);
        foreach (string value in new[] { "{\"1\":\"0x1B\"}", "{\"Settings\":\"0x100\"}", "{\"Settings\":\"0x1B\",\"ai\":\"27\"}", "{\"unknown\":\"0x1B\"}" }) Check(Spec("keymap"), value, false);
        Check(Spec("curve"), "[{\"lux\":0,\"percent\":10},{\"lux\":100,\"percent\":80}]", true);
        Check(Spec("curve"), "[{\"lux\":0,\"percent\":10},{\"lux\":0,\"percent\":80}]", false);
        Check(Spec("curve"), "[{\"lux\":0,\"percent\":0},{\"lux\":100,\"percent\":80}]", false);
        // Exercise the real action router without firmware, shell launches or stored configuration.
        var router = new KeyRouter(new AppConfig(), null!);
        string? page = null, slot = null;
        router.ManagerPage = p => page = p; router.AppLink = p => slot = p;
        foreach (string target in KeyRouter.ManagerPages)
        {
            router.Run("page." + target, null);
            if (page != target) throw new InvalidOperationException("Manager page routing failed.");
        }
        page = null; router.Run("page.unknown", null);
        if (page is not null) throw new InvalidOperationException("Unknown manager page was routed.");
        router.Run("app", "AiKey");
        if (slot != "AiKey") throw new InvalidOperationException("App key routing failed.");
        bool taskView = false; router.TaskView = () => taskView = true; router.Run("taskview", null);
        if (!taskView) throw new InvalidOperationException("Task view routing failed.");
        bool windowsSettings = false; router.OpenWindowsSettings = () => windowsSettings = true; router.Run("windowssettings", null);
        if (!windowsSettings) throw new InvalidOperationException("Windows Settings routing failed.");
        foreach (bool owned in new[] { false, true })
        foreach (bool keepOff in new[] { false, true })
        foreach (bool? enabled in new bool?[] { false, true, null })
        {
            var expected = !owned ? HidNodeToggle.BootAction.Nothing : keepOff
                ? enabled == true ? HidNodeToggle.BootAction.Disable : HidNodeToggle.BootAction.Nothing
                : enabled == true ? HidNodeToggle.BootAction.ClearFlag : HidNodeToggle.BootAction.Enable;
            if (HidNodeToggle.DecideAfterBoot(owned, keepOff, enabled) != expected) throw new InvalidOperationException("Input restart recovery failed.");
        }
        if (Defaults().HandleScreenshotKey || Defaults().AiKeyAction != "xiaoai") throw new InvalidOperationException("XiaoAI F7/AI defaults changed.");
        var legacy = new Preferences { ResidentSetupVersion = 3 };
        var keyboard = new AppConfig { HandleScreenshotKey = true, SettingsKeyAction = "app" };
        MigrateF7Default(legacy, keyboard);
        if (keyboard.HandleScreenshotKey || legacy.ResidentSetupVersion != 4 || keyboard.SettingsKeyAction != "app")
            throw new InvalidOperationException("F7 migration failed or changed another binding.");
        keyboard.HandleScreenshotKey = true;
        MigrateF7Default(legacy, keyboard);
        if (!keyboard.HandleScreenshotKey) throw new InvalidOperationException("F7 migration overwrote a later custom mapping.");
        var screenshotRouter = new KeyRouter(keyboard, null!);
        byte screenshotCode = (byte)Enumerable.Range(0, 256).First(code => KeyMap.Default().Kind((byte)code) == KeyKind.Screenshot);
        bool screenshot = false;
        bool aiLaunched = false; screenshotRouter.XiaoAi = () => aiLaunched = true;
        screenshotRouter.Screenshot = () => screenshot = true;
        keyboard.HandleScreenshotKey = false; screenshotRouter.Handle(screenshotCode, 0);
        if (screenshot || !aiLaunched) throw new InvalidOperationException("F7 did not reserve itself for XiaoAI.");
        keyboard.HandleScreenshotKey = true; screenshotRouter.Handle(screenshotCode, 0);
        if (!screenshot) throw new InvalidOperationException("Custom screenshot opt-in did not route.");
        try { AppLinks.OpenKey(new Preferences(), "invalid"); throw new InvalidOperationException("Unknown app slot accepted."); }
        catch (ArgumentException) { }
        try { AppLinks.OpenKey(new Preferences(), "AiKey"); throw new InvalidOperationException("Unassigned app slot accepted."); }
        catch (ArgumentException) { }
        var area = new Rectangle(100, 200, 1000, 800); var size = new Size(160, 160);
        var travel = new Preferences { TravelReturnLimit = 70, ChargeLimit = 70, XiControl = new AppConfig { TravelMode = true, CareLimitPercent = 70 } };
        if (!HardwareService.EndTravelAtStartup(travel) || travel.TravelReturnLimit is not null || travel.XiControl.TravelMode || travel.ChargeLimit != 70)
            throw new InvalidOperationException("Travel charging survived startup.");
        var full = new Preferences { ChargeLimit = 100, BatteryCareLimit = 60 };
        if (HardwareService.EndTravelAtStartup(full) || full.ChargeLimit != 100 || full.BatteryCareLimit != 60)
            throw new InvalidOperationException("Startup changed persistent full charging.");
        var top = OsdPlacement.Locate(area, size, OsdPosition.Top, 32);
        var bottom = OsdPlacement.Locate(area, size, OsdPosition.Bottom, 32);
        var osd = OsdPlacement.LocateOsd(area, size, OsdPosition.Bottom);
        var tall = OsdPlacement.LocateOsd(area, new Size(160, 320), OsdPosition.Bottom);
        if (top.Y != 232 || bottom.Y != 808 || osd.Y != 768 || tall.Y != 536 || top.X != bottom.X)
            throw new InvalidOperationException("OSD position must follow card height.");
        CheckFeedback();
        Console.WriteLine("PASS setting validation: bounds, types, enum choices, key maps, duplicate curve points and OSD positions. No device or preferences were changed.");
    }
}

