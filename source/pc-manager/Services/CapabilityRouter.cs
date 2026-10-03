using System.Text.Json;
using XiControl.SystemIntegration;

namespace XiaomiAIManager.Services;

public sealed class CapabilityRouter(Preferences preferences, HardwareService hardware) : IDisposable
{
    private readonly TelemetryService telemetry = new();
    private readonly XiaomiBridge xiaomi = new(preferences);
    internal Func<bool> OemCleanupEnabled { set => xiaomi.CleanupEnabled = value; }
    internal void EndOemSessionForValidation() => xiaomi.EndSessionForValidation();
    public object Handle(string method, JsonElement args)
    {
        // External navigation must not hold the firmware lock while an OEM accessibility provider responds.
        switch (method)
        {
            case "display.brightness": return hardware.SetBrightness(Number(args, "value"));
            case "xiaomi.open": return xiaomi.Open(Text(args, "section"));
            case "xiaomi.components": return xiaomi.Components;
            case "xiaomi.session": return xiaomi.SessionState;
            case "windows.open": return XiaomiBridge.OpenWindows(Text(args, "page"));
            case "official.open": return XiaomiBridge.OpenOfficial(Text(args, "page"));
            case "state.read":
                object reading;
                lock (hardware.Sync) reading = hardware.Read(includeInventory: true);
                return new { hardware = reading, telemetry = telemetry.Read(), xiaomiPath = xiaomi.Locate() };
        }
        lock (hardware.Sync)
        {
            switch (method)
            {
                case "quick.read": return new { hardware = hardware.Read(), telemetry = telemetry.ReadBattery() };
                case "telemetry.read": return telemetry.Read();
                case "performance.set": hardware.SetMode(Text(args, "value"), Text(args, "source")); break;
                case "battery.limit": hardware.SetChargeLimit(Number(args, "value")); break;
                case "battery.care": hardware.SetBatteryCare(Flag(args, "on")); break;
                case "battery.travel": hardware.SetTravel(Flag(args, "on")); break;
                case "display.refresh": hardware.SetRefresh(Number(args, "value")); break;
                case "display.cycle": hardware.CycleRefresh(); break;
                case "display.automatic":
                    hardware.SetAutomaticDisplay(Flag(args, "on"), Number(args, "ac"), Number(args, "battery"));
                    return new { message = "Automatic display settings saved. Check the displayed rate for the current result." };
                case "performance.profiles":
                    hardware.SetPowerProfiles(OptionalText(args, "ac"), OptionalText(args, "battery"));
                    return new { message = "Power profiles saved. Check the current mode; firmware can refuse a profile." };
                case "input.enabled": hardware.SetInput(Text(args, "device"), Flag(args, "on")); break;
                case "touchpad.vibration": hardware.SetVibration(Text(args, "value")); break;
                case "touchpad.pressure": hardware.SetPressure(Number(args, "value")); break;
                case "settings.save":
                    string appearance = Text(args, "appearance");
                    if (appearance is not ("light" or "dark" or "system")) throw new ArgumentException("Unknown appearance.");
                    bool closeToTray = Flag(args, "closeToTray");
                    preferences.Appearance = appearance;
                    preferences.CloseToTray = closeToTray;
                    preferences.Save();
                    break;
                default: throw new ArgumentException("This capability is not available.");
            }
            return new { message = "Setting applied and checked." };
        }
    }
    private static JsonElement Field(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value : throw new ArgumentException($"Missing parameter: {name}.");
    private static string Text(JsonElement args, string name) => Field(args, name).ValueKind == JsonValueKind.String
        ? Field(args, name).GetString() ?? "" : throw new ArgumentException($"Invalid text parameter: {name}.");
    private static string? OptionalText(JsonElement args, string name) =>
        Field(args, name).ValueKind == JsonValueKind.Null ? null : Text(args, name);
    private static int Number(JsonElement args, string name) => Field(args, name).ValueKind == JsonValueKind.Number && Field(args, name).TryGetInt32(out int value)
        ? value : throw new ArgumentException($"Invalid number parameter: {name}.");
    private static bool Flag(JsonElement args, string name) => Field(args, name).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new ArgumentException($"Invalid toggle parameter: {name}.")
    };
    public void Dispose() { xiaomi.Dispose(); lock (hardware.Sync) telemetry.Dispose(); }
}
