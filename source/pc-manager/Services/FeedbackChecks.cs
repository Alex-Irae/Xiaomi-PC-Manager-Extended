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
        foreach (string invalid in new[] { "Fn+K", "Ctrl+Ctrl+K", "Ctrl+NeverAKey" })
        { try { GlobalShortcuts.Parse(invalid); throw new InvalidOperationException("Invalid shortcut accepted: " + invalid); } catch (ArgumentException) { } }
        if (!AppearanceOptions.ValidColor("#a1B2c3") || AppearanceOptions.ValidColor("red; background:url(x)") || AppearanceOptions.ValidColor("#123")) throw new InvalidOperationException("Theme color validation failed.");
        var paletteId = "saved:" + new string('a', 32);
        var preferences = new Preferences { ThemePreset = paletteId, ThemePalettes = [
            new ThemePalette { Id = paletteId, Name = "Evening", Accent = "#112233", Background = "#222222", Surface = "#333333" },
            new ThemePalette { Id = "saved:" + new string('b', 32), Name = "Morning" }
        ] };
        AppearanceOptions.RenamePaletteInMemory(preferences, paletteId, "Night");
        if (preferences.ThemePalettes[0].Name != "Night" || preferences.ThemePreset != paletteId)
            throw new InvalidOperationException("Palette rename changed its identity or selection.");
        try { AppearanceOptions.RenamePaletteInMemory(preferences, paletteId, "Morning"); throw new InvalidOperationException("Duplicate palette name accepted."); }
        catch (ArgumentException) { }
        try { AppearanceOptions.DeletePaletteInMemory(preferences, "blue"); throw new InvalidOperationException("Built-in palette deletion accepted."); }
        catch (ArgumentException) { }
        AppearanceOptions.DeletePaletteInMemory(preferences, paletteId);
        if (preferences.ThemePreset != "custom" || preferences.ThemePalettes.Count != 1
            || preferences.ThemeAccent != "#112233" || preferences.ThemeBackground != "#222222" || preferences.ThemeSurface != "#333333")
            throw new InvalidOperationException("Deleting the active palette lost its current colors.");
        // The quick panel opens on the first release; a second press undoes it before the double action runs.
        var pressWindow = new CheckTimer(); var presses = new List<string>();
        using (var eager = new XiControl.Input.MiButtonGesture(new CheckTimer(), pressWindow) { Click = () => presses.Add("click"), DoubleClick = () => presses.Add("double"), Eager = () => true })
        {
            eager.Down(); eager.Up();
            if (!presses.SequenceEqual(new[] { "click" })) throw new InvalidOperationException("An eager press must act on release.");
            pressWindow.Fire();
            if (presses.Count != 1) throw new InvalidOperationException("An eager press acted twice.");
            eager.Down(); eager.Up(); eager.Down(); eager.Up();
            if (!presses.SequenceEqual(new[] { "click", "click", "click", "double" })) throw new InvalidOperationException("A double press must undo the eager press, then run its own action.");
        }
        presses.Clear();
        using (var patient = new XiControl.Input.MiButtonGesture(new CheckTimer(), pressWindow) { Click = () => presses.Add("click") })
        {
            patient.Down(); patient.Up();
            if (presses.Count != 0) throw new InvalidOperationException("Other single-press actions must still wait for the double-press window.");
            pressWindow.Fire();
            if (!presses.SequenceEqual(new[] { "click" })) throw new InvalidOperationException("A single press did not act after the window.");
        }
        Console.WriteLine("PASS refresh transitions/manual override, shortcut parsing and palette rename/delete boundaries.");
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
