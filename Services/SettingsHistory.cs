// Purpose: bounded, resident-only undo/redo of committed manager settings.
// Dependencies: existing settings callbacks and confirmed hardware controls; no packages.
// Outputs: in-memory history only, cleared at restart. Command: app/PCManager.exe --manager.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XiaomiAIManager.Services;

internal sealed class SettingsHistory(ManagerApplication app)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly PropertyInfo[] Properties = typeof(Preferences).GetProperties().Where(p => p.CanWrite
        && p.Name is not ("XiControl" or "LastRefreshPowerSource" or "PowerModeMigrationDone" or "ModeLayoutVersion" or "ResidentSetupVersion")).ToArray();
    private static readonly string[] PolicyNames = ["PowerProfiles", "RestoreMode", "ForceStartMode", "StartPerfMode", "AcPerfMode", "BatteryPerfMode", "AcBrightness", "BatteryBrightness"];
    internal sealed record Snapshot(Dictionary<string, JsonElement> Preferences, Dictionary<string, JsonElement> Settings, Dictionary<string, JsonElement> Policies, JsonElement Hardware, bool Awake, bool Physical);
    private sealed record Edit(Snapshot Before, Snapshot After, string Label);
    private readonly List<Edit> undo = [], redo = [];
    internal object State => new { canUndo = undo.Count > 0, canRedo = redo.Count > 0,
        undoLabel = undo.LastOrDefault()?.Label, redoLabel = redo.LastOrDefault()?.Label };
    internal static string Describe(string method, JsonElement args)
    {
        string? Field(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;
        string Nice(string? value) => System.Text.RegularExpressions.Regex.Replace(value ?? "setting", "([a-z])([A-Z])", "$1 $2");
        return method switch
        {
            "settings.apply" => Nice(Field("key")),
            "settings.reset" => "Reset " + Nice(Field("group")) + " defaults",
            "settings.shortcuts" => "Custom shortcuts",
            "settings.customize" => "Quick panel layout and app links",
            "settings.addApp" => "Add an application",
            "settings.keyApp" => "Key application",
            "settings.appearance" or "settings.save" => "Appearance",
            "settings.paletteSave" => "Save named palette",
            "settings.profile" or "settings.profileReset" => "Profile picture",
            "performance.set" => "Performance mode",
            "performance.profiles" => "Power-source profiles",
            "battery.limit" => "Battery-care limit",
            "battery.travel" => "Travel charging",
            "battery.care" => "Battery care",
            "display.cycle" or "display.refresh" => "Display refresh rate",
            "display.automatic" => "Automatic refresh rate",
            "input.enabled" => Nice(Field("device")) + " state",
            "touchpad.vibration" => "Touchpad vibration",
            "touchpad.pressure" => "Touchpad press sensitivity",
            "window.awake" => "Stay awake",
            _ => Nice(method.Split('.').Last())
        };
    }
    internal static bool Tracks(string method) => method is "settings.apply" or "settings.reset" or "settings.save" or "settings.appearance" or "settings.paletteSave"
        or "settings.profile" or "settings.profileReset" or "settings.shortcuts" or "settings.customize" or "settings.addApp" or "settings.keyApp"
        or "settings.selectXiaomi" or "performance.set" or "performance.profiles" or "battery.limit" or "battery.travel"
        or "battery.care" or "display.cycle" or "display.refresh" or "display.automatic" or "input.enabled" or "touchpad.vibration" or "touchpad.pressure" or "window.awake";
    internal Snapshot Capture(bool physical = false)
    {
        var settings = JsonSerializer.SerializeToElement(app.Advanced!.ReadSettings(), Json).GetProperty("rows")
            .EnumerateArray().Where(row => !row.GetProperty("Key").GetString()!.StartsWith("Api.")).ToDictionary(row => row.GetProperty("Key").GetString()!, row => row.GetProperty("value").Clone());
        lock (app.Hardware.Sync)
            return new(Properties.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.GetValue(app.Preferences), p.PropertyType, Json)),
                settings, PolicyNames.ToDictionary(key => key, key => JsonSerializer.SerializeToElement(typeof(XiControl.Config.AppConfig).GetProperty(key)!.GetValue(app.Advanced.Configuration), Json)),
                JsonSerializer.SerializeToElement(physical ? app.Hardware.Read() : new { }, Json), app.Awake, physical);
    }
    private static bool Different(JsonElement a, JsonElement b) => a.GetRawText() != b.GetRawText();
    internal void Record(Snapshot before, Snapshot after, string label)
    {
        if (!before.Preferences.Any(p => Different(p.Value, after.Preferences[p.Key])) && !before.Settings.Any(p => Different(p.Value, after.Settings[p.Key]))
            && !before.Policies.Any(p => Different(p.Value, after.Policies[p.Key])) && before.Awake == after.Awake && (!before.Physical || !PhysicalChanged(before.Hardware, after.Hardware))) return;
        undo.Add(new(before, after, label)); if (undo.Count > 32) undo.RemoveAt(0); redo.Clear();
    }
    private static readonly string[] Physical = ["mode", "refreshRate", "vibration", "pressure", "touchpadEnabled", "touchscreenEnabled"];
    private static bool PhysicalChanged(JsonElement a, JsonElement b) => Physical.Any(key => Different(a.GetProperty(key), b.GetProperty(key)));
    internal async Task<object> Move(bool forward)
    {
        var source = forward ? redo : undo; var destination = forward ? undo : redo;
        if (source.Count == 0) return State;
        var edit = source[^1]; var target = forward ? edit.After : edit.Before; var other = forward ? edit.Before : edit.After;
        bool RootChanged(string key) => Different(target.Preferences[key], other.Preferences[key]);
        if (target.Physical && Different(target.Hardware.GetProperty("mode"), other.Hardware.GetProperty("mode")))
        {
            var current = await Task.Run(() => JsonSerializer.SerializeToElement(app.Hardware.Read(), Json));
            if (Different(current.GetProperty("powerSource"), target.Hardware.GetProperty("powerSource")))
                throw new InvalidOperationException("The power source changed. Restore it before undoing this mode change.");
        }
        // Only fields changed by this edit are restored. Later telemetry and unrelated settings are left alone.
        foreach (var property in Properties.Where(p => Different(target.Preferences[p.Name], other.Preferences[p.Name])))
            property.SetValue(app.Preferences, target.Preferences[property.Name].Deserialize(property.PropertyType, Json));
        if (RootChanged("PreventSleep")) app.SetPreventSleep(app.Preferences.PreventSleep);
        // Keep the shared XiControl config synchronized before another callback saves it back to root preferences.
        await Task.Run(() =>
        {
            lock (app.Hardware.Sync)
            {
                if (RootChanged("AcMode") || RootChanged("BatteryMode")) app.Hardware.SetPowerProfiles(app.Preferences.AcMode, app.Preferences.BatteryMode);
                if (RootChanged("AutoRefresh") || RootChanged("AcRefreshRate") || RootChanged("BatteryRefreshRate"))
                    app.Hardware.SetAutomaticDisplay(app.Preferences.AutoRefresh, app.Preferences.AcRefreshRate, app.Preferences.BatteryRefreshRate);
                if (RootChanged("ChargeLimit") || RootChanged("TravelReturnLimit"))
                {
                    int? travel = app.Preferences.TravelReturnLimit;
                    app.Hardware.SetChargeLimit(travel ?? app.Preferences.ChargeLimit ?? 100);
                    if (travel is not null) app.Hardware.SetTravel(true);
                }
            }
        });
        foreach (var (key, value) in target.Policies.Where(p => Different(p.Value, other.Policies[p.Key])))
        {
            var property = typeof(XiControl.Config.AppConfig).GetProperty(key)!;
            property.SetValue(app.Advanced!.Configuration, value.Deserialize(property.PropertyType, Json));
        }
        app.Advanced!.Configuration.Save();
        await app.Advanced.RestoreSettingsAsync(target.Settings.Where(p => Different(p.Value, other.Settings[p.Key])).ToDictionary(p => p.Key, p => p.Value));
        if (Different(target.Preferences["Shortcuts"], other.Preferences["Shortcuts"])) app.ReapplyShortcuts();
        if (target.Awake != other.Awake) app.SetAwake(target.Awake);
        await Task.Run(() =>
        {
            lock (app.Hardware.Sync)
            {
                var h = target.Hardware; var old = other.Hardware;
                bool Changed(string key) => Different(h.GetProperty(key), old.GetProperty(key));
                if (!target.Physical) return;
                if (Changed("mode") && h.GetProperty("mode").ValueKind == JsonValueKind.String)
                    app.Hardware.SetMode(h.GetProperty("mode").GetString()!, h.GetProperty("powerSource").GetString(), remember: false);
                if (Changed("refreshRate") && h.GetProperty("refreshRate").ValueKind == JsonValueKind.Number && h.GetProperty("refreshRate").TryGetInt32(out int rate)) app.Hardware.SetRefresh(rate);
                if (Changed("vibration") && h.GetProperty("vibration").ValueKind == JsonValueKind.String) app.Hardware.SetVibration(h.GetProperty("vibration").GetString()!);
                if (Changed("pressure") && h.GetProperty("pressure").ValueKind == JsonValueKind.Number && h.GetProperty("pressure").TryGetInt32(out int pressure)) app.Hardware.SetPressure(pressure);
                foreach (string device in new[] { "touchpad", "touchscreen" })
                    if (Changed(device + "Enabled") && h.GetProperty(device + "Enabled").ValueKind is JsonValueKind.True or JsonValueKind.False)
                        app.Hardware.SetInput(device, h.GetProperty(device + "Enabled").GetBoolean());
            }
        });
        app.Preferences.Save(); app.ApplyAppearance(); app.RefreshWindows();
        source.RemoveAt(source.Count - 1); destination.Add(edit);
        return State;
    }
    internal void Clear() { undo.Clear(); redo.Clear(); }
}
