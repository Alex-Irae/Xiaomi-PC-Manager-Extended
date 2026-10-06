using System.Text.Json;
using XiControl;
using XiControl.Config;
using XiControl.Input;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Ui;
using XiControl.Wmi;
using NativePower = XiControl.SystemIntegration.PowerStatus;

namespace XiaomiAIManager.Services;

// Reuses the supplied XiControl command layer, policy guards, and complete settings pages.
// The Xiaomi popup remains the entry point. No second XiControl process is launched.
public sealed partial class AdvancedControls : IDisposable
{
    private static void MigrateF7Default(Preferences preferences, AppConfig config)
    {
        if (preferences.ResidentSetupVersion >= 4) return;
        config.HandleScreenshotKey = false;
        preferences.ResidentSetupVersion = 4;
    }
    private readonly ManagerApplication app;
    private readonly SystemEventsSource events = new();
    private readonly IMifsClient mifs;
    private readonly AppConfig cfg;
    private readonly AppController controller;
    private readonly ChargeGuard charge;
    private readonly RefreshRateGuard hz;
    private readonly BrightnessCapGuard cap;
    private readonly AutoBrightnessGuard automatic;
    private readonly AlsWatcher als = new();
    private readonly PowerProfileGuard profiles;
    private readonly TravelChargeMonitor travel;
    private readonly ChargeLimitWatcher chargeWatch;
    private readonly TouchpadInput input = new();
    private readonly TouchpadHaptics haptics;
    private readonly TouchpadEdgeSliders edges;
    private readonly TouchpadHeavyPress heavy;
    private readonly OsdForm osd = new();
    private readonly XiaomiPerformanceOsd performanceOsd = new();
    private readonly ApiSettings api;
    private HttpApi? apiHost;
    private SettingsForm? settings;
    private MonitorForm? monitor;
    private TrayMetricIcon? metric;
    private KeyRouter keys = null!;
    private MiButtonGesture? gesture;
    private MifsEventWatcher? keyEvents;
    private string keySignature = "";
    private bool disposed;
    private readonly object apiSync = new();
    private readonly PowerDraw apiDraw = new();
    private string awakeSignature = "";
    private string metricSignature = "";
    private string policySignature = "";
    private string refreshSignature = "";
    private string chargeSignature = "";
    private bool? travelState;
    private string? lastKeyEvent;
    private readonly System.Collections.Concurrent.ConcurrentQueue<object> recentKeyEvents = new();
    internal object KeyStatus => new { enabled = cfg.KeyboardRoutingEnabled, running = keyEvents?.Running ?? false, error = keyEvents?.LastError, lastEvent = lastKeyEvent, recentEvents = recentKeyEvents.ToArray() };

    public AdvancedControls(ManagerApplication app)
    {
        this.app = app;
        mifs = app.Hardware.SharedFirmware;
        haptics = app.Hardware.SharedHaptics;
        var store = new HostConfigStore(app.Preferences, mifs, app.Hardware.Sync);
        cfg = store.Load();
        store.Changed = () => app.Post(() => { ConfigurationChanged(); app.RefreshWindows(); });
        Loc.Current = "en";
        FlyoutPalette.Apply(app.Preferences.Appearance);
        osd.Position = cfg.OsdPosition;
        osd.DurationMs = cfg.OsdDurationMs;
        _ = osd.Handle;
        var pad = new TouchpadControl(cfg);
        var screen = new TouchscreenControl(cfg);
        cap = new(cfg, events);
        automatic = new(cfg, events, clamp: (level, online) => cap.ClampRestore(level, online));
        charge = new(mifs, events, () => cfg.ChargeCare && !cfg.TravelMode ? cfg.CarePercent() : 100, new WorkerTimer());
        charge.ApplyLimit = app.Hardware.ApplyChargePolicy;
        hz = new(cfg, events, events, null, null, () => RunHardware(() => RefreshRate.ApplyForPower(cfg)));
        profiles = new(mifs, cfg, events, cap, automatic);
        travel = new(cfg, events);
        chargeWatch = new(cfg, events);
        var dead = new TouchpadDeadZone(cfg, pad);
        edges = new(cfg, pad, haptics: haptics, input: input);
        heavy = new(cfg, input, haptics);
        controller = new(mifs, cfg, events, new Localizer(), charge, hz, profiles, cap, automatic, als, travel, pad, screen, dead, edges, haptics, heavy);
        controller.FirmwareFailed = () => app.Notify("The device did not confirm the requested setting. Refresh to check the actual state.");
        controller.HapticsChanged = () => app.Post(() => { app.Hardware.PublishHaptics(controller.TouchpadHaptics); app.RefreshWindows(); });
        controller.FlyoutThemeChanged = () => app.Post(SyncAppearanceTheme);
        controller.ModeSet = mode => ShowPerformance(mode, events.IsOnline);
        controller.ModeCycled = controller.ModeSet;
        app.Hardware.ModeConfirmed = ShowPerformance;
        app.Hardware.InputConfirmed = (device, on) => app.Post(() => Flash(device == "touchpad" ? (on ? OsdKind.TouchpadOn : OsdKind.TouchpadOff) : (on ? OsdKind.TouchscreenOn : OsdKind.TouchscreenOff), device + (on ? " on" : " off")));
        app.Hardware.TravelConfirmed = on => app.Post(() => Flash(on ? OsdKind.Travel : OsdKind.TravelOff, on ? "Travel charging on" : "Travel charging off"));
        app.Hardware.RefreshConfirmed = value => app.Post(() => Flash(OsdKind.RefreshRate, "Refresh rate", value.ToString()));
        profiles.ModeApplied = () => app.Post(app.RefreshWindows);
        profiles.ApplyMode = app.Hardware.ApplyProfileMode;
        app.Hardware.PolicySourceChanged = () => app.Post(() => { controller.ReloadModeVisibility(); profiles.Reapply(); app.RefreshWindows(); });
        profiles.ModeFailed = () => app.Notify("The saved performance mode was refused. Refresh to see the actual mode; the saved profile was kept.");
        controller.CareChanged = on => app.Post(chargeWatch.Rearm);
        controller.AwakeChanged = () => app.Post(() => app.RefreshWindows());
        controller.TouchpadHeavyPressed = () => app.Post(() => keys.Run(cfg.TouchpadHeavyPressAction, cfg.TouchpadHeavyPressCommand));
        travel.Ready = () =>
        {
            if (cfg.TravelSound) Sound.PlayTravelReady(cfg.TravelSoundFile);
            RunHardware(() => app.Hardware.SetTravel(false, notify: false));
        };
        chargeWatch.Reached = OnChargeReached;
        events.PowerModeChanged += mode =>
        {
            if (mode == Microsoft.Win32.PowerModes.Suspend) return;
            app.Post(() => { controller.ReloadModeVisibility(); app.RefreshWindows(); });
            chargeWatch.Rearm();
            // AC reconnection is not a travel-mode action; do not reuse its OSD.
        };
        api = Program.TestMode ? new ApiSettings() : ApiSettingsStore.Load();
        _ = Definitions; // Build immutable callbacks on the window thread before worker reads.
        ConfigureKeys();
    }

    public bool Awake => cfg.Awake;
    internal AppConfig Configuration => cfg;
    public bool Visible => settings?.Visible == true || monitor?.Visible == true;
    internal void HideSurfaces() { settings?.Hide(); monitor?.Hide(); }
    public void Start()
    {
        app.Hardware.AttachAdvanced(cfg);
        SyncAppearanceTheme();
        AutoStart.RestartIfClosed = cfg.AutoRestart;
        app.Router.OemCleanupEnabled = () => cfg.AutoCleanupOem;
        controller.Startup();
        awakeSignature = AwakeOptions();
        chargeWatch.Rearm();
        ConfigurationChanged();
        if (api.Enabled) StartApi();
        if (cfg.CheckUpdates) _ = controller.CheckUpdatesAsync(force: false);
    }
    public object SetAwake(bool on)
    {
        if (on && !cfg.OwlMode) throw new InvalidOperationException("Enable Stay awake controls in Settings first.");
        if (cfg.Awake != on) controller.ToggleAwake();
        if (cfg.Awake != on) throw new InvalidOperationException("Windows did not confirm the stay-awake setting.");
        awakeSignature = AwakeOptions();
        return new { message = on ? "Stay awake is on." : "Stay awake is off.", awake = cfg.Awake };
    }
    public void ShowSettings()
    {
        if (settings is null || settings.IsDisposed)
        {
            settings = new SettingsForm(cfg, Actions());
            settings.VisibleChanged += (_, _) => als.SetDemand(cfg.AutoBrightness || settings.Visible);
        }
        settings.Text = "Xiaomi Manager · Advanced controls";
        settings.Popup();
    }
    private void ShowMonitor() => ShowMonitor(null);
    public void ShowMonitor(string? size)
    {
        if (monitor is null || monitor.IsDisposed) monitor = new MonitorForm(cfg, mifs) { Text = "Xiaomi hardware monitor" };
        if (size is null) monitor.Popup(); else monitor.ShowView(size);
        Log.Write($"Monitor view={size ?? cfg.MonitorView ?? "large"} visible={monitor.Visible} size={monitor.Width}x{monitor.Height} dpi={monitor.DeviceDpi}");
    }
    private void ShowPerformance(PerfMode mode, bool online) => app.Post(() =>
    {
        if (cfg.PerformanceOsdEnabled)
        {
            if (app.Preferences.OsdStyle == "xiaomi") performanceOsd.Flash(mode, online, cfg.OsdDurationMs, cfg.OsdPosition);
            else osd.Flash(mode switch { PerfMode.Quiet => OsdKind.Quiet, PerfMode.Eco => OsdKind.Eco, PerfMode.Turbo => OsdKind.Turbo, PerfMode.FullSpeed => OsdKind.Full, _ => OsdKind.Auto }, ModeVisibility.Label(mode, online));
        }
        app.RefreshWindows();
    });
    internal void ShowPowerSourceChanged(bool online)
    {
        if (disposed) return;
        int percent = Math.Clamp((int)Math.Round(SystemInformation.PowerStatus.BatteryLifePercent * 100), 0, 100);
        // The policy worker's later refresh-rate card joins this one in the OSD window.
        app.Post(() => Flash(online ? OsdKind.Charging : OsdKind.OnBattery,
            online ? "Charging" : "On battery", percent.ToString()));
    }
    public void PreviewPerformanceOsd(int number)
    {
        if (number is < 1 or > 4) throw new ArgumentException("Choose an OSD number from 1 to 4.");
        bool online = NativePower.Read().LineStatus == XiControl.SystemIntegration.PowerLineStatus.Online;
        performanceOsd.Flash(ModeVisibility.Available(online)[number - 1], online, cfg.OsdDurationMs, cfg.OsdPosition);
    }
    private void ConfigurationChanged()
    {
        if (disposed) return;
        osd.Position = cfg.OsdPosition;
        osd.DurationMs = cfg.OsdDurationMs;
        als.SetDemand(cfg.AutoBrightness || settings?.Visible == true);
        Log.Enabled = cfg.LogEnabled;
        ConfigureKeys();
        ApplyMetric();
        string charging = $"{cfg.ChargeCare}:{cfg.CarePercent()}:{cfg.TravelMode}:{cfg.SoftChargeAlert}:{cfg.SoftChargeLimitPercent}";
        if (charging != chargeSignature)
        {
            chargeSignature = charging;
            RunHardware(charge.Reapply);
            chargeWatch.Rearm();
        }
        if (travelState != cfg.TravelMode) { travelState = cfg.TravelMode; travel.Rearm(); }
        string policies = JsonSerializer.Serialize(new { cfg.PowerProfiles, cfg.AcPerfMode, cfg.BatteryPerfMode, cfg.RememberBrightness });
        if (policies != policySignature)
        {
            policySignature = policies;
            profiles.Reapply();
        }
        string refreshPolicies = $"{cfg.RefreshRateFeature}:{cfg.AutoRefreshRate}:{cfg.AcRefreshRate}:{cfg.BatteryRefreshRate}";
        // Apply edited rules once, but never reassert them for unrelated configuration saves.
        if (refreshSignature.Length > 0 && refreshSignature != refreshPolicies) hz.Reapply();
        refreshSignature = refreshPolicies;
        string signature = AwakeOptions();
        if (cfg.Awake && signature != awakeSignature)
        {
            AwakeMode.Disable(cfg);
            cfg.Awake = AwakeMode.Enable(cfg);
            if (!cfg.Awake) app.Notify("Windows did not accept the updated stay-awake options.");
            awakeSignature = signature;
            cfg.Save();
        }
    }
    private string AwakeOptions() => $"{cfg.OwlIgnoreDisplay}:{cfg.AwakeOverrideLid}";
    private void ApplyMetric()
    {
        string signature = $"{cfg.TrayMetricEnabled}:{cfg.TrayMetricKind}:{cfg.TrayMetricPeriodSec}:{cfg.ForceAcpiTemperature}";
        if (signature == metricSignature) return;
        metricSignature = signature;
        if (!cfg.TrayMetricEnabled) { metric?.Dispose(); metric = null; return; }
        if (metric is null) { metric = new(cfg, osd, ShowMonitor); metric.Start(); }
        else metric.SettingsChanged();
    }
    private void Flash(OsdKind kind, string title, string? subtitle = null)
    {
        if (disposed) return;
        osd.Position = cfg.OsdPosition;
        osd.DurationMs = cfg.OsdDurationMs;
        if (app.Preferences.OsdStyle != "xiaomi" || !performanceOsd.FlashNotification(kind, cfg.OsdDurationMs, cfg.OsdPosition, subtitle)) osd.Flash(kind, title, subtitle);
    }
    private void FlashLock(LockOsd key, string label, byte value)
    {
        var kind = key == LockOsd.CapsLock ? (value == 0 ? OsdKind.CapsLockOff : OsdKind.CapsLockOn)
            : key == LockOsd.NumLock ? (value == 0 ? OsdKind.NumLockOff : OsdKind.NumLockOn)
            : key == LockOsd.FnLock ? (value == 0 ? OsdKind.FnLockOff : OsdKind.FnLockOn)
            : (value == 0 ? OsdKind.WinKeyLockOff : OsdKind.WinKeyLockOn);
        if (!cfg.HiddenLockOsd.Contains(key)) Flash(kind, label + (value == 0 ? " off" : " on"));
    }
    private void ConfigureKeys()
    {
        string signature = JsonSerializer.Serialize(new { cfg.KeyboardRoutingEnabled, cfg.MiHoldMs, cfg.MiDoubleClickMs, cfg.KeyCodes });
        if (signature == keySignature) return;
        keySignature = signature;
        keyEvents?.Dispose(); keyEvents = null;
        gesture?.Dispose();
        gesture = new(holdMs: cfg.MiHoldMs, doubleClickMs: cfg.MiDoubleClickMs);
        long lastTravelGesture = 0;
        keys = new(cfg, gesture)
        {
            CycleModes = () => RunHardware(controller.CycleMode),
            ToggleCharge = () => RunHardware(() =>
            {
                app.Hardware.SetChargeLimit(cfg.ChargeCare ? 100 : cfg.CarePercent());
            }),
            TogglePanel = app.TogglePopup,
            ScreenOff = app.ScreenOff,
            ToggleOwl = () => SetAwake(!cfg.Awake),
            ToggleMonitor = ShowMonitor,
            ToggleTravel = () =>
            {
                long now = Environment.TickCount64;
                if (now - lastTravelGesture < 550) return; // Some firmware sends the same gesture twice.
                lastTravelGesture = now;
                RunHardware(() => app.Hardware.SetTravel(app.Preferences.TravelReturnLimit is null));
            },
            ToggleTouchpad = () => ToggleInput("touchpad"), ToggleTouchscreen = () => ToggleInput("touchscreen"),
            ToggleAutoBrightness = () => controller.SetAutoBrightness(!cfg.AutoBrightness),
            CycleRefreshRate = CycleDisplay,
            Projection = KeyActions.Projection, Screenshot = KeyActions.Screenshot, TaskView = KeyActions.TaskView,
            OpenSettings = () => app.OpenManager("settings"), OpenWindowsSettings = () => XiaomiBridge.OpenWindows("settings"), Copilot = KeyActions.Copilot,
            MediaPlayPause = KeyActions.MediaPlayPause, MediaNext = KeyActions.MediaNext, MediaPrev = KeyActions.MediaPrev, MediaStop = KeyActions.MediaStop,
            Calculator = KeyActions.Calculator, Launch = KeyActions.LaunchCommand,
            XiaoAi = () => { try { XiaomiBridge.OpenCompanion("ai", app.Preferences); } catch (Exception ex) { app.Notify(ex.Message); } },
            AppLink = slot => { try { AppLinks.OpenKey(app.Preferences, slot); } catch (Exception ex) { app.Notify(ex.Message); } },
            ManagerPage = app.OpenManager,
            PanelVisible = () => app.PopupVisible, AutoBrightnessAvailable = () => als.Available,
            MicKey = value => Flash(value == 0 ? OsdKind.MicOn : OsdKind.MicOff, value == 0 ? "Microphone on" : "Microphone muted"),
            BacklightKey = value => Flash(OsdKind.Backlight, "Keyboard backlight", value.ToString()),
            ProjectionWarningKey = value => Flash(OsdKind.Travel, "Projection", "Check the Windows display settings."),
            TouchpadStateKey = value => Flash(value == 0 ? OsdKind.TouchpadOff : OsdKind.TouchpadOn, value == 0 ? "Touchpad off" : "Touchpad on"),
            LowPowerKey = value => Flash(OsdKind.Travel, "Low battery"),
            NumLockKey = value => FlashLock(LockOsd.NumLock, "Num Lock", value),
            CapsLockKey = value => FlashLock(LockOsd.CapsLock, "Caps Lock", value),
            FnLockKey = value => FlashLock(LockOsd.FnLock, "Fn Lock", value),
            WinKeyLockKey = value => FlashLock(LockOsd.WinKeyLock, "Windows key lock", value),
            CameraPrivacyKey = value => Flash(OsdKind.Travel, value == 0 ? "Camera enabled" : "Camera privacy on"),
            RefreshRateKey = value => CycleDisplay(),
            PerformanceKey = value => RunHardware(app.Hardware.RememberFirmwareMode)
        };
        gesture.Click = () => keys.Run(cfg.MiClickAction, cfg.MiClickCommand);
        gesture.DoubleClick = () => keys.Run(cfg.MiDoubleAction, cfg.MiDoubleCommand);
        gesture.Hold = () => keys.Run(cfg.MiHoldAction, cfg.MiHoldCommand);
        gesture.DoubleEnabled = () => cfg.MiDoubleAction != "none";
        gesture.Eager = () => cfg.MiClickAction == "panel";
        gesture.HoldEnabled = () => cfg.MiHoldAction != "none";
        if (Program.TestMode || !cfg.KeyboardRoutingEnabled) return;
        keyEvents = new MifsEventWatcher();
        keyEvents.KeyPressed += (code, value) => app.Post(() =>
        {
            if (disposed || !cfg.KeyboardRoutingEnabled) return;
            lastKeyEvent = $"0x{code:X2} / {value} / {KeyMap.FromConfig(cfg).Kind(code)}";
            recentKeyEvents.Enqueue(new { time = DateTimeOffset.Now, code, value, kind = KeyMap.FromConfig(cfg).Kind(code).ToString() });
            if (recentKeyEvents.Count > 32) recentKeyEvents.TryDequeue(out _);
            Log.Write("Firmware key: " + lastKeyEvent);
            keys.Handle(code, value);
            app.RefreshWindows();
        });
        var watcher = keyEvents;
        _ = Task.Run(() => { watcher.Start(); app.Post(app.RefreshWindows); });
    }
    internal void SyncAppearanceTheme()
    {
        if (cfg.FlyoutTheme != app.Preferences.Appearance)
        {
            cfg.FlyoutTheme = app.Preferences.Appearance;
            cfg.Save();
        }
        FlyoutPalette.Apply(app.Preferences.Appearance);
        if (monitor is { IsDisposed: false }) { monitor.BackColor = FlyoutPalette.Card; monitor.Invalidate(); }
        osd.Invalidate();
    }

    private void ToggleInput(string device)
    {
        var node = device == "touchpad" ? (HidNodeToggle)new TouchpadControl(cfg) : new TouchscreenControl(cfg);
        if (node.IsEnabled() is not bool current) { app.Notify("This input device is unavailable."); return; }
        RunHardware(() => app.Hardware.SetInput(device, !current));
    }
    private void CycleDisplay() => RunHardware(() =>
    {
        app.Hardware.CycleRefresh();
    });
    internal void RunCustomAction(KeyboardShortcut shortcut)
    {
        if (shortcut.Action == "none") return;
        if (shortcut.Action.StartsWith("suite.", StringComparison.Ordinal)) app.SuiteOpen(shortcut.Action[6..]);
        else if (shortcut.Action == "app") AppLinks.Open(app.Preferences, shortcut.AppId ?? "");
        else keys.Run(shortcut.Action, null);
    }
    private void RunHardware(Action action) => _ = Task.Run(async () =>
    {
        await app.Queue.WaitAsync();
        try { if (!disposed) { lock (app.Hardware.Sync) action(); } }
        catch (Exception ex) { Log.Ex("Advanced.Command", ex); app.Notify("The device action failed. Refresh and check its actual state."); }
        finally { app.Queue.Release(); }
    });

    private SettingsActions Actions() => new()
    {
        GetAutoStart = () => !Program.TestMode && controller.AutoStartEnabled,
        SetAutoStart = on => { if (Program.TestMode) app.Notify("Startup registration is disabled in the test environment."); else controller.ToggleAutoStart(on); },
        Languages = () => controller.Languages, CurrentLanguage = () => "en", SetLanguage = _ => controller.SetLanguage("en"),
        SetFlyoutTheme = controller.SetFlyoutTheme,
        SetModeVisibleFor = controller.SetModeVisible, HiddenModesFor = controller.HiddenModesFor, CanHideModeFor = controller.CanHideModeFor,
        IsOnlineNow = PowerLine.IsOnline,
        GetStartStrategy = () => controller.CurrentStartStrategy, SetStartStrategy = controller.SetStartStrategy, SetProfileMode = controller.SetProfileMode,
        SetRememberBrightness = controller.SetRememberBrightness, SetBrightnessCap = controller.SetBrightnessCap, SetBrightnessCaps = controller.SetBrightnessCaps,
        IsAdaptiveBrightness = () => AdaptiveBrightness.IsEnabled(true) || AdaptiveBrightness.IsEnabled(false),
        SetAutoBrightness = controller.SetAutoBrightness, SetAutoBrightnessLearning = controller.SetAutoBrightnessLearning, SetAutoBrightnessRevert = controller.SetAutoBrightnessRevert,
        IsAlsAvailable = () => als.Available, CurrentLux = () => als.LastLux,
        SetBrightnessMedianSec = controller.SetBrightnessMedianSec, ResetBrightnessCurve = controller.ResetBrightnessCurve,
        BrightnessCurvePoints = controller.BrightnessCurvePoints, SetBrightnessCurve = controller.SetBrightnessCurve,
        SetAutoHz = controller.ToggleAutoHz, SetHoldRefreshRate = controller.SetHoldRefreshRate, SetRefreshRateFeature = controller.ToggleRefreshRateFeature,
        SetRefreshRates = controller.SetRefreshRates, SetCycleRate = controller.SetCycleRate,
        SetCheckUpdates = controller.SetCheckUpdates, GetUpdate = () => controller.Update, GetUpdateStatus = () => controller.LastUpdateCheck,
        CheckUpdatesNow = done => _ = Task.Run(async () => { await controller.CheckUpdatesAsync(force: true); app.Post(done); }),
        SetTouchpadDeadZone = controller.SetTouchpadDeadZone, SetTouchpadDeadZoneMm = controller.SetTouchpadDeadZoneMm,
        SetTouchpadEdgeSliders = controller.SetTouchpadEdgeSliders, SetTouchpadEdgeWidthMm = controller.SetTouchpadEdgeWidthMm, SetTouchpadEdgeSwipes = controller.SetTouchpadEdgeSwipes,
        SetTouchpadEdgeSwap = controller.SetTouchpadEdgeSwap, SetTouchpadEdgeHaptics = controller.SetTouchpadEdgeHaptics, SetTouchpadEdgeHapticsMs = controller.SetTouchpadEdgeHapticsMs,
        SetTouchpadSlideStrength = controller.SetTouchpadSlideStrength, SetTouchpadHeavyPress = controller.SetTouchpadHeavyPress, SetTouchpadHeavyPressCommand = controller.SetTouchpadHeavyPressCommand,
        GetTouchpadHaptics = haptics.Read, SetTouchpadVibration = controller.SetTouchpadVibration, SetTouchpadPressure = controller.SetTouchpadPressure,
        SetOwlFeature = controller.ToggleOwlFeature, SetCareLimit = controller.SetCareLimit, SoftChargeApplied = chargeWatch.Rearm, GetBatteryReport = BatteryInfo.Read,
        GetApiSettings = () => api, ApiApplied = ApiApplied, TestWebhook = TestWebhook, TrayMetricApplied = ApplyMetric,
        SetOsdPosition = value => { cfg.OsdPosition = value; cfg.Save(); osd.Position = value; },
        SetOsdDuration = value => { cfg.OsdDurationMs = value; cfg.Save(); osd.DurationMs = value; },
        SetLockOsd = (key, on) => { if (on) cfg.HiddenLockOsd.Remove(key); else if (!cfg.HiddenLockOsd.Contains(key)) cfg.HiddenLockOsd.Add(key); cfg.Save(); },
        PreviewOsd = () => PreviewPerformanceOsd(1)
    };

    private void StartApi()
    {
        if (api.Port is < 1024 or > 65535) { app.Notify("HTTP API port must be between 1024 and 65535."); return; }
        try
        {
            var router = new ApiRouter(api)
            {
                SetMode = mode => RunHardware(() => app.Hardware.SetMode(mode.ToString())),
                SetCare = on => RunHardware(() => app.Hardware.SetChargeLimit(on ? cfg.CarePercent() : 100)),
                SetTravel = on => RunHardware(() => app.Hardware.SetTravel(on)),
                SetOwl = on => app.Post(() => SetAwake(on)),
                Status = ApiState, OwlFeature = () => cfg.OwlMode
            };
            apiHost = new(api, router);
        }
        catch (Exception ex) { Log.Ex("API.Start", ex); app.Notify("The HTTP API could not start. Check its port and native log."); }
    }
    private void ApiApplied()
    {
        if (Program.TestMode) { api.Enabled = false; api.WebhookOnChargeLimit = false; app.Notify("The HTTP API, webhooks and firewall changes are disabled in the test environment."); return; }
        ApiSettingsStore.Save(api);
        apiHost?.Dispose(); apiHost = null;
        if (api.Enabled) StartApi();
        _ = Task.Run(() => ApiFirewall.Set(api.Enabled && api.LanAccess, api.Port));
    }
    private ApiStatus ApiState()
    {
        var power = NativePower.Read();
        float? watts = null;
        lock (apiSync) { if (apiDraw.TryReadWatts(out float value) && float.IsFinite(value)) watts = value; }
        return new(mifs.GetPerfMode()?.ToString() ?? "unknown", cfg.ChargeCare, cfg.TravelMode, cfg.Awake, power.BatteryPercent,
            power.LineStatus == XiControl.SystemIntegration.PowerLineStatus.Online, watts, BatteryInfo.Read().HealthPercent);
    }
    private void TestWebhook(Action<bool> done)
    {
        if (Program.TestMode) { done(false); return; }
        if (!Webhook.IsAllowed(api.WebhookUrl)) { done(false); return; }
        _ = Task.Run(async () => { bool result = await Webhook.SendAsync(api.WebhookUrl!, Webhook.Payload("test", cfg.CarePercent(), ApiState(), !cfg.ChargeLimitUnsupported)); app.Post(() => done(result)); });
    }
    private void OnChargeReached(int percent, ChargeTarget target, bool repeat)
    {
        if (target.Soft)
        {
            app.Notify($"Battery is at {percent}%. The {target.Limit}% software alert does not stop charging. Unplug when ready.");
            if (cfg.SoftChargeAlertSound) System.Media.SystemSounds.Exclamation.Play();
        }
        if (api.WebhookOnChargeLimit && Webhook.IsAllowed(api.WebhookUrl))
            _ = Task.Run(() => Webhook.SendAsync(api.WebhookUrl!, Webhook.Payload(repeat ? "charge_limit_reminder" : "charge_limit", target.Limit, ApiState(), !target.Soft)));
    }
    public void Shutdown() { controller.Shutdown(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        app.Hardware.PolicySourceChanged = null;
        app.Hardware.ModeConfirmed = null;
        app.Hardware.InputConfirmed = null; app.Hardware.TravelConfirmed = null; app.Hardware.RefreshConfirmed = null;
        keyEvents?.Dispose(); gesture?.Dispose(); apiHost?.Dispose(); apiDraw.Dispose();
        settings?.Dispose(); monitor?.Dispose(); metric?.Dispose(); osd.Dispose(); performanceOsd.Dispose();
        chargeWatch.Dispose(); travel.Dispose(); profiles.Dispose(); automatic.Dispose(); cap.Dispose(); hz.Dispose(); charge.Dispose();
        heavy.Dispose(); edges.Dispose(); input.Dispose(); als.Dispose(); events.Dispose();
    }

    // One root settings.json remains authoritative, including the imported configuration.
    private sealed class HostConfigStore(Preferences preferences, IMifsClient firmware, object sync) : IConfigStore
    {
        public Action? Changed;
        public AppConfig Load()
        {
            var config = preferences.XiControl;
            if (config is null)
            {
                int? care = Program.TestMode ? preferences.ChargeLimit : preferences.ChargeLimit ?? firmware.GetChargeLimit();
                config = new AppConfig
                {
                    Language = "en", FlyoutTheme = "light", CheckUpdates = false,
                    ChargeCare = preferences.TravelReturnLimit is not null || care is < 100,
                    CareLimitPercent = preferences.TravelReturnLimit ?? (care is < 100 ? care.Value : 80), TravelMode = preferences.TravelReturnLimit is not null,
                    AutoRefreshRate = preferences.AutoRefresh, AcRefreshRate = preferences.AcRefreshRate, BatteryRefreshRate = preferences.BatteryRefreshRate,
                    PowerProfiles = preferences.AcMode is not null || preferences.BatteryMode is not null,
                    AcPerfMode = Parse(preferences.AcMode), BatteryPerfMode = Parse(preferences.BatteryMode),
                    TouchpadDeviceId = preferences.TouchpadDeviceId, TouchscreenDeviceId = preferences.TouchscreenDeviceId,
                    TouchpadPersistOff = preferences.TouchpadPersistOff, TouchscreenPersistOff = preferences.TouchscreenPersistOff
                };
            }
            config.Language = "en";
            // One-time migration to the user's chosen AC/battery strategy. Never guess an unseen source's mode.
            if (!preferences.PowerModeMigrationDone && !Program.TestMode)
            {
                config.PowerProfiles = true;
                config.RestoreMode = false;
                config.ForceStartMode = null;
                var source = NativePower.Read().LineStatus;
                if (firmware.GetPerfMode() is PerfMode mode && Enum.IsDefined(mode))
                {
                    if (source == XiControl.SystemIntegration.PowerLineStatus.Online && ModeVisibility.IsAvailable(mode, true)) config.AcPerfMode ??= mode;
                    else if (source == XiControl.SystemIntegration.PowerLineStatus.Offline && ModeVisibility.IsAvailable(mode, false)) config.BatteryPerfMode ??= mode;
                }
                preferences.PowerModeMigrationDone = true;
            }
            if (config.AcPerfMode is PerfMode ac && !ModeVisibility.IsAvailable(ac, true)) config.AcPerfMode = null;
            if (config.BatteryPerfMode is PerfMode battery && !ModeVisibility.IsAvailable(battery, false)) config.BatteryPerfMode = null;
            if (preferences.ModeLayoutVersion < 1)
            {
                config.RestoreMode = false;
                config.ForceStartMode = null;
                config.StartPerfMode = null;
                preferences.ModeLayoutVersion = 1;
            }
            if (Program.TestMode) { config.KeyboardRoutingEnabled = false; config.AutoStart = false; }
            config.MigrateKeyActions();
            if (!Program.TestMode && preferences.ResidentSetupVersion < 1)
            {
                config.AutoStart = true;
                config.KeyboardRoutingEnabled = true;
                if (config.MiClickAction == "modes") config.MiClickAction = "panel";
                if (config.SettingsKeyAction == "charge") config.SettingsKeyAction = "settings";
                if (config.AiKeyAction == "copilot") config.AiKeyAction = "panel";
                config.OsdPosition = OsdPosition.Bottom;
                preferences.ResidentSetupVersion = 1;
            }
            if (!Program.TestMode && preferences.ResidentSetupVersion < 2)
            {
                // Adopt useful defaults for the resident; every slot remains editable in Keyboard.
                if (config.SettingsKeyAction is "touchscreen" or "charge") config.SettingsKeyAction = "settings";
                if (config.AiKeyAction is "none" or "copilot") config.AiKeyAction = "panel";
                preferences.ResidentSetupVersion = 2;
            }
            if (preferences.ResidentSetupVersion < 3)
            {
                config.SettingsKeyAction = "windowssettings";
                config.AiKeyAction = "none"; // Reserved for XiaoAI; no OEM wakeup or replacement action.
                config.HandleScreenshotKey = false;
                preferences.ResidentSetupVersion = 3;
            }
            MigrateF7Default(preferences, config);
            if (preferences.ResidentSetupVersion < 5)
            {
                if (config.AiKeyAction is "none" or "panel") config.AiKeyAction = "xiaoai";
                config.HoldRefreshRate = false;
                preferences.QuickIcons.Remove("home");
                preferences.CloseToTray = true;
                preferences.ResidentSetupVersion = 5;
            }
            if (!Program.TestMode && preferences.ResidentSetupVersion < 6)
            {
                config.OsdPosition = OsdPosition.Bottom;
                preferences.ResidentSetupVersion = 6;
            }
            // Stay awake is a choice for the current session and starts off whenever the manager
            // starts; the controller then restores any lid action it had changed. Prevent sleep
            // is saved separately and comes back.
            config.Awake = false;
            config.Store = this;
            preferences.XiControl = config;
            Save(config);
            return config;
        }
        private static PerfMode? Parse(string? text) => Enum.TryParse<PerfMode>(text, out var value) && Enum.IsDefined(value) ? value : null;
        public void Save(AppConfig config)
        {
            lock (sync)
            {
            preferences.XiControl = config;
            // An unconfigured fresh test must not acquire a full-charge policy from unrelated saves.
            preferences.ChargeLimit = config.ChargeCare ? config.CarePercent() : preferences.ChargeLimit is not null ? 100 : null;
            preferences.TravelReturnLimit = config.TravelMode ? config.CarePercent() : null;
            preferences.BatteryCareLimit = config.CarePercent();
            preferences.AutoRefresh = config.RefreshRateFeature && config.AutoRefreshRate;
            preferences.AcRefreshRate = config.AcRefreshRate; preferences.BatteryRefreshRate = config.BatteryRefreshRate;
            preferences.AcMode = config.PowerProfiles ? config.AcPerfMode?.ToString() : null;
            preferences.BatteryMode = config.PowerProfiles ? config.BatteryPerfMode?.ToString() : null;
            preferences.TouchpadDeviceId = config.TouchpadDeviceId; preferences.TouchscreenDeviceId = config.TouchscreenDeviceId;
            preferences.TouchpadPersistOff = config.TouchpadPersistOff; preferences.TouchscreenPersistOff = config.TouchscreenPersistOff;
            preferences.Save();
            }
            Changed?.Invoke();
        }
    }
}
