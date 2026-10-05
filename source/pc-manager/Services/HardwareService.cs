using Microsoft.Win32;
using XiControl.SystemIntegration;
using XiControl.Wmi;
using XiControl.Config;
using PowerStatus = XiControl.SystemIntegration.PowerStatus;
using PowerLineStatus = XiControl.SystemIntegration.PowerLineStatus;

namespace XiaomiAIManager.Services;

public sealed class HardwareService : IDisposable
{
    public readonly object Sync = new();
    private readonly Preferences preferences;
    private MifsClient? firmware;
    private readonly TouchpadHaptics haptics = new();
    private readonly ManagedInputNode touchpad;
    private readonly ManagedInputNode touchscreen;
    private PowerLineStatus? lastPower;
    private bool retryPolicies = true;
    private bool travelFullNotified;
    private AppConfig? advanced;
    public IMifsClient SharedFirmware { get; }
    public TouchpadHaptics SharedHaptics => haptics;
    public Action<string>? Notice;
    internal Action<PerfMode, bool>? ModeConfirmed;
    internal Action<string, bool>? InputConfirmed;
    internal Action<bool>? TravelConfirmed;
    internal Action<int>? RefreshConfirmed;
    internal Action? PolicySourceChanged;
    public string? FirmwareError { get; private set; }
    private long diagnosticUntil;
    private BatteryReport batteryReport;
    private TouchpadHapticsState? cachedHaptics;
    private bool? cachedTouchpad, cachedTouchscreen;
    private int[] cachedRates = [];
    private SystemInfo? systemInfo;
    private int diagnosticsRunning;
    private long diagnosticVersion;
    private long diagnosticPublishedVersion = -1;
    internal Action? ReconcileRequested;

    public HardwareService(Preferences preferences)
    {
        this.preferences = preferences;
        touchpad = new(preferences, false);
        touchscreen = new(preferences, true);
        SharedFirmware = new SharedMifs(this);
    }
    public void AttachAdvanced(AppConfig? config) => advanced = config;
    private void SyncAdvanced()
    {
        if (advanced is null) return;
        advanced.ChargeCare = preferences.TravelReturnLimit is not null || preferences.ChargeLimit is < 100;
        advanced.CareLimitPercent = preferences.TravelReturnLimit ?? (preferences.ChargeLimit is < 100 ? preferences.ChargeLimit.Value : advanced.CarePercent());
        advanced.TravelMode = preferences.TravelReturnLimit is not null;
        advanced.AutoRefreshRate = preferences.AutoRefresh;
        advanced.AcRefreshRate = preferences.AcRefreshRate;
        advanced.BatteryRefreshRate = preferences.BatteryRefreshRate;
        advanced.Save();
    }

    // Run on the native worker. Recovery only concerns nodes previously disabled by this app.
    public void Initialize()
    {
        _ = ComputerSpecs.ReadCached(); // Inventory never delays brightness, mode or input readiness.
        lock (Sync)
        {
            if (EndTravelAtStartup(preferences)) preferences.Save();
            try { firmware = new MifsClient(); }
            catch (Exception ex)
            {
                XiControl.Log.Ex("Hardware.Initialize", ex);
                FirmwareError = "Xiaomi firmware controls are unavailable. The OEM WMI interface or required permissions were not available.";
            }
            RecoverInputs();
            ApplyPolicies();
        }
        RefreshDiagnostics();
    }
    // Rebind the proprietary WMI client without closing the resident or its windows.
    public void Reconnect()
    {
        lock (Sync)
        {
            var replacement = new MifsClient();
            var previous = firmware;
            firmware = replacement;
            FirmwareError = null;
            previous?.Dispose();
            diagnosticVersion++;
            diagnosticUntil = 0;
        }
        RefreshDiagnostics();
    }
    internal void RecoverInputs()
    {
        lock (Sync)
        {
            // A failed node must not prevent recovery of the other input or basic firmware readiness.
            try { touchpad.RestoreAfterBoot(); } catch (Exception ex) { XiControl.Log.Ex("Recover touchpad", ex); }
            try { touchscreen.RestoreAfterBoot(); } catch (Exception ex) { XiControl.Log.Ex("Recover touchscreen", ex); }
            cachedTouchpad = touchpad.IsEnabled(); cachedTouchscreen = touchscreen.IsEnabled();
            Interlocked.Increment(ref diagnosticVersion); diagnosticUntil = 0;
        }
    }

    private void RefreshDiagnostics()
    {
        if (Environment.TickCount64 < diagnosticUntil || Interlocked.CompareExchange(ref diagnosticsRunning, 1, 0) != 0) return;
        long version = Interlocked.Read(ref diagnosticVersion);
        _ = Task.Run(() =>
        {
            try
            {
                // PnP, BIOS and capacity queries cannot delay light popup reads or firmware writes.
                var info = SystemInfo.Current;
                var battery = BatteryInfo.Read();
                var pad = haptics.Read();
                var padOn = touchpad.IsEnabled(); var screenOn = touchscreen.IsEnabled();
                var rates = RefreshRate.Supported();
                lock (Sync)
                {
                    // A read started before a confirmed write must not publish the old device state.
                    if (version != diagnosticVersion) return;
                    systemInfo = info; batteryReport = battery; cachedHaptics = pad;
                    cachedTouchpad = padOn; cachedTouchscreen = screenOn; cachedRates = rates;
                    diagnosticPublishedVersion = version;
                    diagnosticUntil = Environment.TickCount64 + 60_000;
                }
                PolicyReadingsChanged?.Invoke();
            }
            catch (Exception ex) { XiControl.Log.Ex("Hardware diagnostics", ex); diagnosticUntil = Environment.TickCount64 + 15_000; }
            finally { Interlocked.Exchange(ref diagnosticsRunning, 0); }
        });
    }
    internal Action? PolicyReadingsChanged;

    public object Read(bool includeInventory = false)
    {
        var power = PowerStatus.Read();
        RefreshDiagnostics();
        var health = batteryReport;
        var hapticState = cachedHaptics;
        var info = systemInfo;
        string? mode = SafeRead(() => firmware?.GetPerfMode() is PerfMode value && Enum.IsDefined(value) ? value.ToString() : null);
        int? chargeLimit = SafeRead(() => firmware?.GetChargeLimit());
        int? wantedCharge = preferences.TravelReturnLimit is not null ? 100 : preferences.ChargeLimit;
        string? wantedMode = advanced?.PowerProfiles == true ? (power.LineStatus == PowerLineStatus.Online ? preferences.AcMode : preferences.BatteryMode) : null;
        bool chargeConflict = chargeLimit is int live && wantedCharge is int desired && live != desired;
        bool modeConflict = power.LineStatus != PowerLineStatus.Unknown && mode is not null && wantedMode is not null && mode != wantedMode;
        if (chargeConflict || modeConflict) ReconcileRequested?.Invoke();
        return new
        {
            deviceName = Environment.MachineName,
            specs = includeInventory ? ComputerSpecs.ReadCached() : null,
            model = info?.ModelLine, bios = info?.BiosLine, serial = info?.SerialMasked,
            diagnosticsLoading = diagnosticsRunning != 0,
            firmwareAvailable = mode is not null || chargeLimit is not null,
            firmwareError = FirmwareError ?? (mode is null && chargeLimit is null ? "Firmware performance and charging controls are unavailable. Windows controls remain available." : null),
            mode, chargeLimit,
            requestedChargeLimit = preferences.TravelReturnLimit is not null ? 100 : preferences.ChargeLimit,
            chargeConflict, modeConflict, wantedMode,
            visibleModes = ModeVisibility.Visible(ModeVisibility.Available(power.LineStatus == PowerLineStatus.Online), ModeVisibility.For(advanced?.HiddenModesBySource, power.LineStatus == PowerLineStatus.Online))
                .Select(value => value.ToString()).ToArray(),
            modeLabel = mode is not null && Enum.TryParse<PerfMode>(mode, out var readMode) ? ModeVisibility.Label(readMode, power.LineStatus == PowerLineStatus.Online) : null,
            testMode = Program.TestMode,
            adapterWatts = SafeRead(() => firmware?.GetAdapterWatts()) is int watts && watts > 0 ? (int?)watts : null,
            batteryPercent = power.BatteryPercent, powerSource = power.LineStatus.ToString(),
            batteryHealth = health.HealthPercent, cycles = health.Cycles,
            designWh = health.DesignWh > 0 ? (double?)health.DesignWh : null,
            fullWh = health.FullWh > 0 ? (double?)health.FullWh : null,
            travel = preferences.TravelReturnLimit is not null, careLimit = preferences.BatteryCareLimit,
            brightness = Brightness.Get(), refreshRate = RefreshRate.Current(), refreshRates = cachedRates,
            touchpadEnabled = cachedTouchpad, touchscreenEnabled = cachedTouchscreen,
            hapticsAvailable = hapticState is not null, vibration = hapticState?.Vibration?.ToString(), pressure = hapticState?.Pressure,
            preferences = new
            {
                preferences.Appearance, preferences.CloseToTray, preferences.MinimizeToTray, preferences.AutoRefresh,
                preferences.ThemePreset, preferences.ThemeAccent, preferences.ThemeBackground, preferences.ThemeSurface, preferences.ThemePalettes,
                preferences.DeveloperMode, preferences.OriginalPopupEnabled, preferences.ProfileImage, preferences.PopupScale, preferences.CompactPanel, preferences.PopupAnimations, preferences.OsdStyle,
                customAssets = AppAssets.Manifest(),
                preferences.AcRefreshRate, preferences.BatteryRefreshRate, preferences.AcMode, preferences.BatteryMode,
                powerProfiles = advanced?.PowerProfiles ?? (preferences.AcMode is not null || preferences.BatteryMode is not null),
                chargeGuard = preferences.ChargeLimit is < 100,
                touchpadFeature = advanced?.TouchpadFeature ?? true, touchscreenFeature = advanced?.TouchscreenFeature ?? true,
                refreshFeature = advanced?.RefreshRateFeature ?? true, owlFeature = advanced?.OwlMode ?? true,
                rememberBrightness = advanced?.RememberBrightness ?? false, autoBrightness = advanced?.AutoBrightness ?? false,
                brightnessCap = advanced?.BrightnessCapEnabled ?? false
            },
            customization = new { preferences.PopupPosition, preferences.PopupOffsetX, preferences.PopupOffsetY, preferences.QuickLinks, preferences.QuickSystemActions, preferences.QuickIcons }
        };
    }

    private static T? SafeRead<T>(Func<T?> read)
    {
        try { return read(); }
        catch (Exception ex) { XiControl.Log.Ex("Hardware.Read", ex); return default; }
    }

    public void SetMode(string value, string? expectedSource = null, bool remember = true)
    {
        if (!Enum.TryParse<PerfMode>(value, false, out var mode) || !Enum.IsDefined(mode))
            throw new ArgumentException("Unknown performance mode.");
        var source = PowerStatus.Read().LineStatus;
        if (source == PowerLineStatus.Unknown || (expectedSource is not null && expectedSource != source.ToString()))
            throw new InvalidOperationException("The power source changed or is unavailable. Refresh before choosing a mode.");
        bool online = source == PowerLineStatus.Online;
        if (!ModeVisibility.IsAvailable(mode, online))
            throw new InvalidOperationException("This mode is not offered for the current power source.");
        if (firmware is null || !firmware.SetPerfMode(mode) || firmware.GetPerfMode() != mode)
            throw new InvalidOperationException("The firmware did not confirm this mode. Availability depends on the laptop and power source.");
        if (PowerStatus.Read().LineStatus != source)
            throw new InvalidOperationException("The charger changed during this operation. The mode was not saved; refresh its actual state.");
        // Only a choice made by the user is announced. Policies that restore the saved mode
        // at startup, on a power change or from the 30-second guard stay silent.
        if (remember) ModeConfirmed?.Invoke(mode, online);
        else PolicyReadingsChanged?.Invoke();
        if (!remember) return;
        if (advanced is not null)
        {
            advanced.PowerProfiles = true;
            advanced.RestoreMode = false;
            advanced.ForceStartMode = null;
            advanced.RememberMode(mode, source == PowerLineStatus.Online);
        }
        else
        {
            if (source == PowerLineStatus.Online) preferences.AcMode = value;
            else preferences.BatteryMode = value;
            preferences.Save();
        }
    }

    internal void RememberFirmwareMode()
    {
        lock (Sync)
        {
            var source = PowerStatus.Read().LineStatus;
            if (source == PowerLineStatus.Unknown || firmware?.GetPerfMode() is not PerfMode mode || !ModeVisibility.IsAvailable(mode, source == PowerLineStatus.Online)) return;
            // In Smart mode the firmware announces its own internal steps (values 7 and 8) with the
            // same event as the mode key, about once a minute under load. The mode itself has not
            // changed then, so nothing is shown and no settings file is rewritten.
            bool onAc = source == PowerLineStatus.Online;
            PerfMode? known = advanced is { PowerProfiles: true } ? (onAc ? advanced.AcPerfMode : advanced.BatteryPerfMode)
                : Enum.TryParse<PerfMode>(onAc ? preferences.AcMode : preferences.BatteryMode, out var saved) ? saved : null;
            if (known == mode) return;
            if (advanced is not null)
            {
                advanced.PowerProfiles = true; advanced.RestoreMode = false; advanced.ForceStartMode = null;
                advanced.RememberMode(mode, source == PowerLineStatus.Online);
            }
            ModeConfirmed?.Invoke(mode, source == PowerLineStatus.Online);
        }
    }

    public void SetChargeLimit(int percent)
    {
        if (percent is not (40 or 50 or 60 or 70 or 80 or 100)) throw new ArgumentException("Choose a supported charge limit.");
        WriteCharge(percent);
        preferences.ChargeLimit = percent;
        if (percent < 100) preferences.BatteryCareLimit = percent;
        preferences.TravelReturnLimit = null;
        travelFullNotified = false;
        preferences.Save();
        SyncAdvanced();
    }

    internal static bool EndTravelAtStartup(Preferences p)
    {
        if (p.TravelReturnLimit is null && p.XiControl?.TravelMode != true) return false;
        int target = p.TravelReturnLimit ?? p.XiControl?.CarePercent() ?? p.BatteryCareLimit;
        p.ChargeLimit = p.BatteryCareLimit = target; p.TravelReturnLimit = null;
        if (p.XiControl is { } cfg) { cfg.TravelMode = false; cfg.ChargeCare = true; cfg.CareLimitPercent = target; }
        return true;
    }
    public void SetBatteryCare(bool on) => SetChargeLimit(on ? preferences.BatteryCareLimit : 100);
    public void CycleRefresh()
    {
        if (advanced?.RefreshRateFeature == false) throw new InvalidOperationException("Display rate controls are disabled.");
        if (RefreshRate.Cycle(advanced?.CycleRefreshRates) is not int rate) throw new InvalidOperationException("The display rate could not change.");
        RefreshConfirmed?.Invoke(rate);
    }
    internal bool? ApplyProfileMode(PerfMode mode, bool online)
    {
        lock (Sync)
        {
            var source = PowerStatus.Read().LineStatus;
            if (source == PowerLineStatus.Unknown || (source == PowerLineStatus.Online) != online || advanced?.PowerProfiles != true
                || (online ? advanced.AcPerfMode : advanced.BatteryPerfMode) != mode) return null;
            if (firmware?.GetPerfMode() == mode) return true;
            try { SetMode(mode.ToString(), source.ToString(), remember: false); return true; }
            catch (Exception ex) { XiControl.Log.Ex("PowerProfile.Apply", ex); return false; }
        }
    }

    private void WriteCharge(int percent)
    {
        if (firmware is null || !firmware.SetChargeLimit(percent) || firmware.GetChargeLimit() != percent)
            throw new InvalidOperationException("The firmware did not confirm the charge limit. The current battery setting may have changed; refresh to read its actual state.");
    }
    internal bool? ApplyChargePolicy(int percent, bool hurry)
    {
        lock (Sync)
        {
            int wanted = preferences.TravelReturnLimit is not null ? 100 : preferences.ChargeLimit ?? 100;
            if (preferences.ChargeLimit is null || wanted != percent) return null; // Discard a guard queued before a manual change.
            if (firmware?.GetChargeLimit() == percent) return true;
            return firmware?.SetChargeLimit(percent, resetFirst: !hurry) == true && (hurry || firmware.GetChargeLimit() == percent);
        }
    }

    public void SetTravel(bool enabled, bool notify = true)
    {
        if (enabled)
        {
            if (preferences.TravelReturnLimit is not null) return;
            if (PowerStatus.Read().LineStatus != PowerLineStatus.Online)
                throw new InvalidOperationException("Connect the charger before enabling travel charging.");
            int? current = preferences.ChargeLimit is < 100 ? preferences.ChargeLimit : firmware?.GetChargeLimit();
            if (current is not (40 or 50 or 60 or 70 or 80))
                throw new InvalidOperationException("Set a battery-care limit before enabling one-off travel charging.");
            // Persist the recovery target before releasing charge protection, so interruption is recoverable.
            preferences.TravelReturnLimit = current;
            preferences.ChargeLimit = current;
            preferences.Save();
            try { WriteCharge(100); }
            catch { SyncAdvanced(); throw; } // Keep the recovery target even if readback fails after release.
        }
        else if (preferences.TravelReturnLimit is int previous)
        {
            WriteCharge(previous);
            preferences.TravelReturnLimit = null;
            preferences.Save();
        }
        else return; // No state change: do not flash a contradictory travel OSD.
        travelFullNotified = false;
        SyncAdvanced();
        if (notify) TravelConfirmed?.Invoke(enabled);
    }

    public object SetBrightness(int percent)
    {
        if (percent is < 1 or > 100) throw new ArgumentException("Brightness must be between 1% and 100%.");
        lock (Brightness.WriteSync)
        {
        var timing = System.Diagnostics.Stopwatch.StartNew();
        if (!Brightness.SetAsUser(percent)) throw new InvalidOperationException("Windows refused the brightness write or no supported internal panel was found.");
        double writeMs = timing.Elapsed.TotalMilliseconds;
        int? actual = Brightness.Get();
        bool retried = false;
        // WMI can return before CurrentBrightness updates. Poll only on a mismatch, never delay every slider step.
        while (actual != percent && timing.ElapsedMilliseconds < 180)
        {
            if (!retried && timing.ElapsedMilliseconds >= 40)
            {
                // A successful void WMI return can leave the old level retained. Retry once with fresh targets.
                Brightness.ClearTargets();
                if (!Brightness.SetAsUser(percent)) throw new InvalidOperationException("Windows refused the brightness retry.");
                retried = true;
            }
            Thread.Sleep(8);
            actual = Brightness.Get();
        }
        if (actual != percent)
        {
            XiControl.Log.Write($"Brightness mismatch requested={percent} actual={actual} verifiedMs={timing.Elapsed.TotalMilliseconds:F1}");
            throw new InvalidOperationException("Windows did not confirm the brightness change.");
        }
        XiControl.Log.Write($"Brightness requested={percent} actual={actual} writeMs={writeMs:F1} verifiedMs={timing.Elapsed.TotalMilliseconds:F1} retried={retried}");
        return new { brightness = actual, writeMs, verifiedMs = timing.Elapsed.TotalMilliseconds, retried };
        }
    }
    internal void InvalidateDiagnostics() { lock (Sync) { diagnosticVersion++; diagnosticUntil = 0; } }
    internal void PublishHaptics(TouchpadHapticsState? state)
    {
        lock (Sync) { diagnosticVersion++; cachedHaptics = state; diagnosticUntil = 0; }
    }
    internal async Task RefreshDiagnosticsForValidationAsync()
    {
        InvalidateDiagnostics();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            RefreshDiagnostics();
            if (Volatile.Read(ref diagnosticsRunning) == 0 && diagnosticPublishedVersion == Interlocked.Read(ref diagnosticVersion)) return;
            await Task.Delay(20);
        } while (timeout.ElapsedMilliseconds < 15000);
        throw new TimeoutException("Device diagnostics did not finish within 15 seconds.");
    }

    public void SetRefresh(int hz)
    {
        if (!RefreshRate.Supported().Contains(hz)) throw new ArgumentException("Choose a refresh rate reported by the internal display.");
        if (!RefreshRate.Apply(hz) || RefreshRate.Current() != hz)
            throw new InvalidOperationException("Windows did not confirm the refresh-rate change.");
        RefreshConfirmed?.Invoke(hz);
    }

    public void SetInput(string device, bool enabled)
    {
        ManagedInputNode node = device switch
        {
            "touchpad" => touchpad, "touchscreen" => touchscreen,
            _ => throw new ArgumentException("Unknown input device.")
        };
        if (node.IsEnabled() is not bool state) throw new InvalidOperationException("The input device was not found.");
        if (state != enabled && node.Toggle() != enabled) throw new InvalidOperationException("Windows did not confirm the input-device change.");
        diagnosticVersion++;
        if (device == "touchpad") { cachedTouchpad = enabled; if (!enabled) cachedHaptics = null; }
        else cachedTouchscreen = enabled;
        diagnosticUntil = 0;
        if (state != enabled) InputConfirmed?.Invoke(device, enabled);
    }

    public void SetVibration(string value)
    {
        if (!Enum.TryParse<HapticsVibration>(value, false, out var level) || !Enum.IsDefined(level))
            throw new ArgumentException("Unknown haptic strength.");
        if (!haptics.SetVibration(level)) throw new InvalidOperationException("The touchpad did not confirm this haptic setting.");
        PublishHaptics(cachedHaptics is { } current ? current with { Vibration = level } : haptics.Read());
    }

    public void SetPressure(int value)
    {
        if (!TouchpadHapticsProtocol.PressurePresets.Contains(value)) throw new ArgumentException("Choose a supported click-force preset.");
        if (!haptics.SetPressure(value)) throw new InvalidOperationException("The touchpad did not confirm this click-force setting.");
        PublishHaptics(cachedHaptics is { } current ? current with { Pressure = value } : haptics.Read());
    }

    public void SetAutomaticDisplay(bool enabled, int ac, int battery)
    {
        int[] supported = RefreshRate.Supported();
        if (enabled && (!supported.Contains(ac) || !supported.Contains(battery)))
            throw new ArgumentException("Both automatic refresh rates must be supported by the internal display.");
        if (enabled)
        {
            int target = PowerStatus.Read().LineStatus == PowerLineStatus.Online ? ac : battery;
            if (!RefreshRate.Apply(target) || RefreshRate.Current() != target)
                throw new InvalidOperationException("Windows did not confirm the automatic refresh rate for the current power source.");
        }
        preferences.AutoRefresh = enabled;
        preferences.AcRefreshRate = ac;
        preferences.BatteryRefreshRate = battery;
        preferences.Save();
        retryPolicies = true;
        SyncAdvanced();
        ApplyPolicies();
    }

    public void SetPowerProfiles(string? ac, string? battery)
    {
        foreach (string? name in new[] { ac, battery })
            if (name is not null && (!Enum.TryParse<PerfMode>(name, false, out var mode) || !Enum.IsDefined(mode)))
                throw new ArgumentException("Unknown power profile.");
        if (ac is not null && !ModeVisibility.IsAvailable(Enum.Parse<PerfMode>(ac), true)) throw new ArgumentException("Choose an AC mode: Silent, Smart, Full speed, or Eco.");
        if (battery is not null && !ModeVisibility.IsAvailable(Enum.Parse<PerfMode>(battery), false)) throw new ArgumentException("Choose a battery mode: Eco, Quiet, Auto, or Turbo.");
        preferences.AcMode = ac;
        preferences.BatteryMode = battery;
        if (advanced is not null)
        {
            advanced.PowerProfiles = ac is not null || battery is not null;
            advanced.AcPerfMode = ac is null ? null : Enum.Parse<PerfMode>(ac);
            advanced.BatteryPerfMode = battery is null ? null : Enum.Parse<PerfMode>(battery);
            advanced.RestoreMode = false;
            advanced.ForceStartMode = null;
        }
        preferences.Save();
        retryPolicies = true;
        SyncAdvanced();
        ApplyPolicies();
    }

    // Adapted from XiControl ChargeGuard: no off/on reset during the suspend or shutdown window.
    public void BeforeSuspend()
    {
        lock (Sync)
        {
            if (preferences.TravelReturnLimit is null && preferences.ChargeLimit is int limit && limit < 100)
                try
                {
                    if (firmware?.GetChargeLimit() != limit) firmware?.SetChargeLimit(limit, resetFirst: false);
                }
                catch (Exception ex) { XiControl.Log.Ex("ChargeGuard.Suspend", ex); }
            retryPolicies = true;
        }
    }

    public void PowerChanged(PowerModes mode)
    {
        lock (Sync)
        {
            if (mode is PowerModes.Resume or PowerModes.StatusChange) retryPolicies = true;
            diagnosticUntil = 0;
            if (mode == PowerModes.Resume) Brightness.ClearTargets();
            ApplyPolicies();
        }
    }

    internal void ReapplySavedPolicies()
    {
        lock (Sync)
        {
            retryPolicies = true;
            diagnosticUntil = 0;
            ApplyPolicies();
            if (preferences.TravelReturnLimit is null && preferences.ChargeLimit is int limit && limit < 100 && ApplyChargePolicy(limit, hurry: false) == false)
                throw new InvalidOperationException("The saved battery-care limit was refused after stopping Xiaomi tools.");
            XiControl.Log.Write("Resident policies reapplied after OEM isolation.");
        }
    }

    public void ApplyPolicies()
    {
        var power = PowerStatus.Read();
        if (power.LineStatus == PowerLineStatus.Unknown) return;
        if (preferences.TravelReturnLimit is not null && power.LineStatus == PowerLineStatus.Offline)
        {
            try { SetTravel(false, notify: false); XiControl.Log.Write("Travel charging ended on battery; care limit restored."); }
            catch (Exception ex) { XiControl.Log.Ex("Travel.Restore", ex); Notice?.Invoke("The travel charge limit could not be restored. Check Battery settings."); }
        }
        if (advanced is null && preferences.TravelReturnLimit is not null && power.BatteryPercent == 100 && !travelFullNotified)
        {
            travelFullNotified = true;
            try { SetTravel(false, notify: false); }
            catch (Exception ex) { XiControl.Log.Ex("Travel.Complete", ex); }
        }
        if (preferences.ChargeLimit is int limit && firmware is not null)
        {
            int chargeWanted = preferences.TravelReturnLimit is not null ? 100 : limit;
            try { if (firmware.GetChargeLimit() != chargeWanted) WriteCharge(chargeWanted); }
            catch (Exception ex) { XiControl.Log.Ex("ChargeGuard.Restore", ex); }
        }
        if (advanced?.PowerProfiles == true)
        {
            var profileWanted = power.LineStatus == PowerLineStatus.Online ? advanced.AcPerfMode : advanced.BatteryPerfMode;
            if (profileWanted is PerfMode profile) ApplyProfileMode(profile, power.LineStatus == PowerLineStatus.Online);
        }
        if (lastPower == power.LineStatus && !retryPolicies) return;
        if (preferences.TravelReturnLimit is not null || preferences.ChargeLimit == 100)
        {
            // Explicit full/travel intent must also survive resume and charger changes.
            try { if (firmware?.GetChargeLimit() is int actualLimit && actualLimit != 100) WriteCharge(100); }
            catch (Exception ex) { XiControl.Log.Ex("FullCharge.Restore", ex); Notice?.Invoke("Full charging was not confirmed. Check Battery settings for a competing controller."); }
        }
        bool refreshTransition = preferences.LastRefreshPowerSource is not null && preferences.LastRefreshPowerSource != power.LineStatus.ToString();
        if (preferences.LastRefreshPowerSource != power.LineStatus.ToString())
        { preferences.LastRefreshPowerSource = power.LineStatus.ToString(); preferences.Save(); }
        if (preferences.AutoRefresh && refreshTransition)
        {
            int target = power.LineStatus == PowerLineStatus.Online ? preferences.AcRefreshRate : preferences.BatteryRefreshRate;
            if (!RefreshRate.Apply(target)) Notice?.Invoke("The automatic display rate could not be applied.");
            else if (RefreshRate.Current() is int confirmed)
            {
                XiControl.Log.Write($"Refresh.Auto source={power.LineStatus} requested={target} confirmed={confirmed} osd={RefreshConfirmed is not null}");
                RefreshConfirmed?.Invoke(confirmed);
            }
            else XiControl.Log.Write($"Refresh.Auto source={power.LineStatus} requested={target} confirmed=unavailable");
        }
        lastPower = power.LineStatus;
        retryPolicies = false;
        if (advanced is not null)
        {
            // The poll also catches a source that was Unknown during the resume event.
            PolicySourceChanged?.Invoke();
            return; // XiControl guards still own the actual display/performance restoration.
        }
        string? wanted = power.LineStatus == PowerLineStatus.Online ? preferences.AcMode : preferences.BatteryMode;
        if (wanted is not null)
            try { SetMode(wanted, power.LineStatus.ToString(), remember: false); }
            catch (Exception ex) { XiControl.Log.Ex("PowerProfile", ex); Notice?.Invoke("The automatic performance profile was refused by the firmware."); }
    }

    public void Dispose()
    {
        lock (Sync) firmware?.Dispose();
    }

    // Thin config adapter around XiControl's guarded PnP implementation.
    private sealed class ManagedInputNode(Preferences settings, bool screen) : HidNodeToggle
    {
        protected override string CompatId => screen ? "HID_DEVICE_UP:000D_U:0004" : "HID_DEVICE_UP:000D_U:0005";
        protected override string LogName => screen ? "Touchscreen" : "Touchpad";
        protected override bool KeepOff => screen ? settings.XiControl?.TouchscreenKeepOff == true : settings.XiControl?.TouchpadKeepOff == true;
        protected override string? DeviceId
        {
            get => screen ? settings.TouchscreenDeviceId : settings.TouchpadDeviceId;
            set
            {
                if (screen) { settings.TouchscreenDeviceId = value; if (settings.XiControl is { } config) config.TouchscreenDeviceId = value; }
                else { settings.TouchpadDeviceId = value; if (settings.XiControl is { } config) config.TouchpadDeviceId = value; }
            }
        }
        protected override bool PersistOff
        {
            get => screen ? settings.TouchscreenPersistOff : settings.TouchpadPersistOff;
            set
            {
                if (screen) { settings.TouchscreenPersistOff = value; if (settings.XiControl is { } config) config.TouchscreenPersistOff = value; }
                else { settings.TouchpadPersistOff = value; if (settings.XiControl is { } config) config.TouchpadPersistOff = value; }
            }
        }
        protected override void SaveConfig() => settings.Save();
    }

    // One transport owner, including upstream policy callbacks. Missing hardware stays unavailable.
    private sealed class SharedMifs(HardwareService owner) : IMifsClient
    {
        public PerfMode? GetPerfMode() { lock (owner.Sync) return owner.firmware?.GetPerfMode(); }
        public int? GetChargeLimit() { lock (owner.Sync) return owner.firmware?.GetChargeLimit(); }
        public int GetAdapterWatts() { lock (owner.Sync) return owner.firmware?.GetAdapterWatts() ?? 0; }
        public int? GetBatteryHealth() { lock (owner.Sync) return owner.firmware?.GetBatteryHealth(); }
        public bool SetPerfMode(PerfMode mode)
        {
            lock (owner.Sync)
                return PowerStatus.Read().LineStatus is var source && source != PowerLineStatus.Unknown && ModeVisibility.IsAvailable(mode, source == PowerLineStatus.Online)
                    && owner.firmware?.SetPerfMode(mode) == true && owner.firmware.GetPerfMode() == mode;
        }
        public bool SetChargeLimit(int percent, bool resetFirst = true)
        {
            lock (owner.Sync) return owner.firmware?.SetChargeLimit(percent, resetFirst) == true && owner.firmware.GetChargeLimit() == percent;
        }
        public void Dispose() { } // HardwareService owns and disposes the real MifsClient.
    }
}
