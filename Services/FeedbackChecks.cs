// Purpose: regression checks for source-transition refresh and editable shortcuts.
// Dependencies: existing XiControl interfaces, no devices or external packages.
// Output: assertions through --check-settings. Command: dotnet PCManager.dll --check-settings.
using Microsoft.Win32;
using XiControl.Config;
using XiControl.SystemIntegration;

namespace XiaomiAIManager.Services;

public sealed partial class AdvancedControls
{
    private static void CheckFeedback()
    {
        var source = new CheckPower { IsOnline = true };
        var debounce = new CheckTimer(); var watchdog = new CheckTimer(); int applied = 0;
        using var guard = new RefreshRateGuard(new AppConfig { RefreshRateFeature = true, AutoRefreshRate = true, HoldRefreshRate = true }, source, source, debounce, watchdog, () => applied++);
        source.Send(PowerModes.StatusChange); debounce.Fire();
        source.Display(); debounce.Fire(); // A manual refresh change, even with old Hold enabled.
        source.Send(PowerModes.Resume); watchdog.Fire(); debounce.Fire();
        if (applied != 0) throw new InvalidOperationException("Manual display choice was overwritten without a power transition.");
        source.IsOnline = false; source.Send(PowerModes.StatusChange); debounce.Fire();
        source.Send(PowerModes.StatusChange); debounce.Fire();
        if (applied != 1) throw new InvalidOperationException("Power transition must apply exactly once.");
        source.IsOnline = true; source.Send(PowerModes.Resume); debounce.Fire(); watchdog.Fire();
        if (applied != 2) throw new InvalidOperationException("Source change during sleep did not apply.");
        var chord = GlobalShortcuts.Parse("Ctrl+Alt+K");
        if (chord.key != (uint)Keys.K || chord.modifiers != (0x4000 | 3)) throw new InvalidOperationException("Shortcut parser returned incorrect keys.");
        if (GlobalShortcuts.Parse("ctrl+alt+k") != chord) throw new InvalidOperationException("Shortcut case should not matter.");
        foreach (string invalid in new[] { "Fn+K", "K", "Ctrl+Ctrl+K", "Ctrl+NeverAKey" })
        { try { GlobalShortcuts.Parse(invalid); throw new InvalidOperationException("Invalid shortcut accepted: " + invalid); } catch (ArgumentException) { } }
        if (!AppearanceOptions.ValidColor("#a1B2c3") || AppearanceOptions.ValidColor("red; background:url(x)") || AppearanceOptions.ValidColor("#123")) throw new InvalidOperationException("Theme color validation failed.");
        Console.WriteLine("PASS refresh transitions/manual override, shortcut parsing and theme boundary.");
    }
    private sealed class CheckPower : IPowerEvents, IDisplayEvents
    {
        public bool IsOnline { get; set; }
        public float BatteryLifePercent => .5f;
        public event Action<PowerModes>? PowerModeChanged;
        public event Action? DisplaySettingsChanged;
        public event Action? SessionEnding { add { } remove { } }
        internal void Send(PowerModes mode) => PowerModeChanged?.Invoke(mode);
        internal void Display() => DisplaySettingsChanged?.Invoke();
        public void Dispose() { }
    }
    private sealed class CheckTimer : IAppTimer
    {
        public int Interval { get; set; }
        public event Action? Tick;
        private bool running;
        public void Start() => running = true;
        public void Stop() => running = false;
        internal void Fire() { if (running) Tick?.Invoke(); }
        public void Dispose() { }
    }
}
