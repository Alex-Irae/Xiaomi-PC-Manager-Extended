using System.Runtime.InteropServices;
using Microsoft.Win32;
using XiaomiAIManager.Services;

namespace XiaomiAIManager;

// Owns the tray resident and shared backend. Dismissing the popup never stops policies.
public sealed class ManagerApplication : ApplicationContext
{
    internal readonly Preferences Preferences = Services.Preferences.Load();
    internal readonly HardwareService Hardware;
    internal readonly CapabilityRouter Router;
    internal readonly SettingsHistory History;
    internal readonly SemaphoreSlim Queue = new(1, 1);
    internal readonly SemaphoreSlim OemQueue = new(1, 1);
    internal readonly Task Ready;
    internal readonly Task AdvancedReady;
    internal string IsolationStatus { get; private set; } = "Starting OEM isolation";
    internal AdvancedControls? Advanced { get; private set; }
    internal bool Awake => Advanced?.Awake ?? fallbackAwake;
    private bool fallbackAwake;
    internal bool PopupVisible => popup.Visible;
    private readonly MainWindow popup;
    internal ManualScreenOff ManualDisplay { get; }
    private readonly SleepGuard sleepGuard = new();
    internal bool PreventSleep => sleepGuard.Enabled;
    private readonly GlobalShortcuts shortcuts;
    private readonly CopilotKeyRouter copilot;
    internal string? ShortcutError => shortcuts.Error;
    internal string? CopilotError => copilot.Error;
    internal int CopilotInterceptCount => copilot.InterceptCount;
    private MainWindow? manager;
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray = new() { Text = "PC Manager", Icon = AppAssets.Icon, Visible = true };
    private readonly System.Windows.Forms.Timer policyTimer = new() { Interval = 30000 };
    private System.Windows.Forms.PowerLineStatus lastOsdPowerSource = SystemInformation.PowerStatus.PowerLineStatus;
    internal bool Exiting { get; private set; }
    internal bool RestartAfterImport { get; private set; }
    private SettingsBackup.Prepared? pendingBackup;

    internal void ImportOnRestart(SettingsBackup.Prepared backup) => pendingBackup = backup;

    public ManagerApplication(string command)
    {
        XiControl.Log.Write($"Resident.Start pid={Environment.ProcessId} command={command} test={Program.TestMode}");
        _ = dispatcher.Handle;
        Hardware = new(Preferences);
        Router = new(Preferences, Hardware);
        History = new(this);
        Hardware.Notice = Notify;
        Hardware.PolicyReadingsChanged = () => Post(RefreshWindows);
        Hardware.ReconcileRequested = Reconcile;
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ready = initialized.Task;
        var advancedInitialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AdvancedReady = advancedInitialized.Task;
        popup = new(this, compact: true);
        _ = popup.Handle; // Hidden activation target also exists when started with --tray.
        ManualDisplay = new(popup.Handle);
        try { sleepGuard.Set(Preferences.PreventSleep); }
        catch (Exception ex) { Preferences.PreventSleep = false; XiControl.Log.Ex("SleepGuard.Restore", ex); }
        shortcuts = new(this);
        shortcuts.Configure(Preferences.Shortcuts);
        copilot = new(this);
        copilot.Configure(Preferences.CopilotShortcut);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Quick controls", null, (_, _) => TogglePopup());
        menu.Items.Add("All controls", null, (_, _) => OpenManager());
        menu.Items.Add("Settings", null, (_, _) => OpenManager("settings"));
        var monitorMenu = new ToolStripMenuItem("Hardware monitor");
        foreach (string size in new[] { "small", "medium", "large" })
            monitorMenu.DropDownItems.Add(char.ToUpperInvariant(size[0]) + size[1..], null, (_, _) => OpenMonitor(size));
        menu.Items.Add(monitorMenu);
        tray.Text = Program.PopupTitle;
        menu.Items.Add("Exit", null, async (_, _) => await QuitAsync("tray Exit"));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && Environment.TickCount64 - popup.LastDismissStartedMs > 350) TogglePopup(); };
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        policyTimer.Tick += async (_, _) =>
        {
            if (Exiting || validating || !Ready.IsCompletedSuccessfully || !Queue.Wait(0)) return;
            try { await Task.Run(() => { lock (Hardware.Sync) Hardware.ApplyPolicies(); }); }
            catch (Exception ex) { XiControl.Log.Ex("Policy.Timer", ex); }
            finally { Queue.Release(); }
        };
        policyTimer.Start();
        // Post until after the UI message loop starts. --tray deliberately has no visible window.
        dispatcher.BeginInvoke((Action)(async () =>
        {
            ActivateCommand(command);
            _ = popup.PreloadAsync();
            // OEM service recovery must never gate basic Windows/firmware controls.
            _ = Task.Run(async () =>
            {
                try
                {
                    OemServiceControl.RestoreDailyIsolation();
                    IsolationStatus = Program.TestMode ? "Test mode" : OemServiceControl.IsolationRequested ? "OEM isolation applied" : "OEM isolation not requested";
                    await Ready;
                    await Queue.WaitAsync();
                    try { if (!Exiting) Hardware.ReapplySavedPolicies(); }
                    finally { Queue.Release(); }
                }
                catch (Exception ex) { IsolationStatus = "OEM isolation failed. Another controller may override settings."; XiControl.Log.Ex("OEM isolation", ex); Notify(IsolationStatus); }
                finally { Post(RefreshWindows); }
            });
            try
            {
                await Task.Run(() => { ManualDisplay.RestoreIdleLogged(); ManualDisplay.RestoreLogged(); });
                await Task.Run(Hardware.Initialize);
                initialized.TrySetResult();
                RefreshWindows();
                Advanced = new(this);
                Advanced.Start();
                advancedInitialized.TrySetResult();
                XiControl.Log.Write("Resident.Ready");
                // Warm the one-time WMI inventory after the immediate control path is ready.
                // The manager can then show specs without starting eight providers on first open.
                _ = Task.Run(async () => { await Task.Delay(10000); if (!Exiting) ComputerSpecs.ReadCached(); });
            }
            catch (Exception ex)
            {
                XiControl.Log.Ex("Advanced.Initialize", ex);
                try { Advanced?.Shutdown(); }
                catch (Exception cleanup) { XiControl.Log.Ex("Advanced.InitializeCleanup", cleanup); }
                Advanced?.Dispose();
                Advanced = null;
                Hardware.AttachAdvanced(null);
                // Basic controls remain accessible if advanced policy/page initialization failed.
                initialized.TrySetResult();
                advancedInitialized.TrySetResult();
                Notify("Advanced controls could not initialize. Basic controls remain available; check the native log.");
            }
        }));
    }

    private bool openingOriginal;
    internal async void TogglePopup()
    {
        if (Exiting || openingOriginal) return;
        if (popup.Visible)
        {
            if (!popup.ClosingPopup) await popup.DismissPopupAsync();
        }
        else
        {
            if (Preferences.OriginalPopupEnabled && !openingOriginal)
            {
                openingOriginal = true;
                await OemQueue.WaitAsync();
                try { await Task.Run(() => Router.Handle("xiaomi.open", System.Text.Json.JsonSerializer.SerializeToElement(new { section = "popup" }))); }
                catch (Exception ex) { Notify(ex.Message); }
                finally { OemQueue.Release(); openingOriginal = false; }
            }
            if (!Exiting) popup.ShowPopup();
        }
    }
    internal void OpenManager(string page = "home")
    {
        if (popup.Visible) _ = popup.DismissPopupAsync();
        if (manager is null || manager.IsDisposed) manager = new(this, compact: false);
        manager.StartPage = page;
        if (!manager.Visible) manager.PlaceManager();
        manager.Show();
        if (manager.WindowState == FormWindowState.Minimized) manager.WindowState = FormWindowState.Normal;
        manager.Activate();
        manager.FocusDocument();
        manager.SelectManagerPage(page);
    }
    internal void ReleaseManager(MainWindow window)
    {
        XiControl.Log.Write($"Window.ReleaseManager matching={manager == window} exiting={Exiting}");
        if (manager != window || Exiting) return;
        manager = null;
        window.Hide();
        // A closed manager does not need to retain its WebView renderer. Keep the
        // quick panel warm, since keyboard/tray activation is latency sensitive.
        Post(window.Dispose);
    }
    internal void OpenAdvanced() => OpenManager("settings"); // Legacy callers share the consolidated UI.
    internal void Post(Action action)
    {
        if (Exiting || dispatcher.IsDisposed) return;
        dispatcher.BeginInvoke((Action)(() =>
        {
            if (Exiting) return;
            try { action(); }
            catch (Exception ex) { XiControl.Log.Ex("Advanced.Callback", ex); Notify("The advanced action failed. Check the native log and actual device state."); }
        }));
    }
    internal async void OpenMonitor(string size)
    {
        await AdvancedReady;
        if (Exiting) return;
        if (Advanced is null) { Notify("The monitor is unavailable. Check the native log."); return; }
        try { Advanced.ShowMonitor(size); }
        catch (Exception ex) { XiControl.Log.Ex("Monitor.Open", ex); Notify("The monitor could not open. Check the native log."); }
    }
    internal void RefreshWindows()
    {
        popup.RefreshIfVisible();
        manager?.RefreshIfVisible();
    }
    internal void ApplyAppearance()
    {
        Advanced?.SyncAppearanceTheme();
        popup.ApplyAppearance(); manager?.ApplyAppearance(); RefreshWindows();
    }
    // Shorten the current-source display timer once, then restore the saved value
    // on wake. An explicit SC_MONITORPOWER command bypassed the AC power request
    // on this S0 machine and suspended the user's background task immediately.
    internal async void ScreenOff()
    {
        try
        {
            await popup.DismissPopupAsync();
            await Task.Delay(350); // Let the initiating mouse/key release finish before power-off.
            if (!Exiting) ManualDisplay.RequestIdleDisplayOff();
        }
        catch (Exception ex) { XiControl.Log.Ex("ScreenOff", ex); Notify("The display-off request failed."); }
    }
    internal Task<object> ProbePopupAsync(string? imagePath = null) => popup.ProbeQuickAsync(imagePath);
    internal Task<System.Text.Json.JsonElement> ProbeQuickControlsAsync() => popup.ProbeQuickControlsAsync();
    internal object SaveShortcuts(System.Text.Json.JsonElement args)
    {
        var next = GlobalShortcuts.Validate(args, Preferences);
        if (!shortcuts.Configure(next))
        {
            string error = shortcuts.Error ?? "Shortcut registration failed.";
            shortcuts.Configure(Preferences.Shortcuts);
            throw new InvalidOperationException(error + " The previous shortcuts were restored.");
        }
        Preferences.Shortcuts = next; Preferences.Save();
        return new { message = "Custom shortcuts saved." };
    }
    internal object SaveCopilotShortcut(System.Text.Json.JsonElement args)
    {
        string action = args.GetProperty("action").GetString() ?? "";
        string? appId = args.TryGetProperty("appId", out var id) ? id.GetString() : null;
        if (action != "system" && !GlobalShortcuts.Actions.Contains(action)) throw new ArgumentException("Choose a listed Copilot key action.");
        if (action == "app" && !Preferences.QuickLinks.Any(link => link.Id == appId)) throw new ArgumentException("Choose an existing app link.");
        if (action != "system" && copilot.Error is not null) throw new InvalidOperationException(copilot.Error);
        var next = new KeyboardShortcut { Chord = "Win+Shift+F23", Action = action, AppId = action == "app" ? appId : null };
        Preferences.CopilotShortcut = next;
        Preferences.Save();
        copilot.Configure(next);
        return new { message = action == "system" ? "Windows handles the Copilot key again." : "Copilot key mapping updated." };
    }
    internal void ReapplyShortcuts() => shortcuts.Configure(Preferences.Shortcuts);
    private void ActivateCommand(string command)
    {
        if (command == "toggle") TogglePopup();
        else if (command == "manager") OpenManager();
        else if (command.StartsWith("monitor-", StringComparison.Ordinal)) OpenMonitor(command[8..]);
        else if (command is "cycle-mode" or "brightness-up" or "brightness-down" or "toggle-travel") RunShortcut(Program.CommandMessage(command));
        else if (command == "verify-hardware") VerifyHardware();
        else if (command == "validate-controls") ValidateControls();
        else if (command == "validate-oem") ValidateControls("oem");
        else if (command == "validate-ui") ValidateControls("ui");
        else if (command == "validate-brightness") ValidateControls("brightness");
        else if (command == "validate-screenoff") ValidateControls("screenoff");
    }
    private bool validating;
    private int reconciling;
    private long nextReconcile;
    private void Reconcile()
    {
        if (Exiting || validating || Environment.TickCount64 < nextReconcile || Interlocked.CompareExchange(ref reconciling, 1, 0) != 0) return;
        nextReconcile = Environment.TickCount64 + 15000; // Bound retries when another controller keeps writing.
        _ = Task.Run(async () =>
        {
            await Queue.WaitAsync();
            try { if (!Exiting) { lock (Hardware.Sync) Hardware.ApplyPolicies(); } }
            catch (Exception ex) { XiControl.Log.Ex("Policy reconciliation", ex); }
            finally { Queue.Release(); Interlocked.Exchange(ref reconciling, 0); Post(RefreshWindows); }
        });
    }
    internal async void ValidateControls(string phase = "controls")
    {
        if (validating || Exiting) return;
        validating = true;
        try
        {
            await AdvancedReady;
            OpenManager("display");
            await RuntimeValidation.RunAsync(this, manager!, phase);
            RefreshWindows();
            Notify("Control validation finished. Read the saved report for failures and restoration results.");
        }
        catch (Exception ex) { XiControl.Log.Ex("Validation", ex); Notify("Validation failed. Read the saved results and native log."); }
        finally { validating = false; }
    }
    internal async void VerifyHardware()
    {
        bool held = false;
        try
        {
            await Ready;
            await Queue.WaitAsync(); held = true;
            await Task.Run(() => { lock (Hardware.Sync) HardwareDiagnostics.Run(Hardware, Preferences); });
            Notify("Hardware probe finished. Read its saved results for confirmations and failures.");
            RefreshWindows();
        }
        catch (Exception ex) { XiControl.Log.Ex("Diagnostics", ex); Notify("Hardware probe failed. Check the native log."); }
        finally { if (held) Queue.Release(); }
    }
    internal void HideWindows()
    {
        popup.Hide();
        if (manager is { } window) ReleaseManager(window);
        Advanced?.HideSurfaces();
    }
    internal async void RunShortcut(uint command)
    {
        bool held = false;
        try
        {
            await Ready;
            if (Exiting) return;
            if (command is MainWindow.BrighterMessage or MainWindow.DimmerMessage)
            {
                int confirmed = await Task.Run(() =>
                {
                    int current = XiControl.SystemIntegration.Brightness.Get() ?? throw new InvalidOperationException("Brightness is unavailable.");
                    int target = Math.Clamp(current + (command == MainWindow.BrighterMessage ? 5 : -5), 1, 100);
                    Hardware.SetBrightness(target);
                    return target;
                });
                if (popup.Visible) popup.Send(new { brightness = confirmed });
            }
            else
            {
                await Queue.WaitAsync(); held = true;
                await Task.Run(() =>
                {
                    lock (Hardware.Sync)
                    {
                        if (command == MainWindow.ReapplyMessage) Hardware.ReapplySavedPolicies();
                        else if (command == MainWindow.TravelMessage) Hardware.SetTravel(Preferences.TravelReturnLimit is null);
                        else
                        {
                            var source = XiControl.SystemIntegration.PowerStatus.Read().LineStatus;
                            if (source == XiControl.SystemIntegration.PowerLineStatus.Unknown) throw new InvalidOperationException("Power source is unavailable.");
                            var modes = XiControl.Config.ModeVisibility.Available(source == XiControl.SystemIntegration.PowerLineStatus.Online);
                            int index = modes.ToList().IndexOf(Hardware.SharedFirmware.GetPerfMode() ?? XiControl.Wmi.PerfMode.Auto);
                            Hardware.SetMode(modes[(index + 1) % modes.Count].ToString(), source.ToString());
                        }
                    }
                });
            }
            RefreshWindows();
        }
        catch (Exception ex) { XiControl.Log.Ex("Shortcut", ex); Notify(ex.Message); }
        finally { if (held) Queue.Release(); }
    }

    internal object SetAwake(bool enabled)
    {
        if (Advanced is not null) return Advanced.SetAwake(enabled);
        // Keep this call on the persistent UI thread. It never changes lid or display policy.
        if (SetThreadExecutionState(enabled ? 0x80000001u : 0x80000000u) == 0)
            throw new InvalidOperationException("Windows did not accept the stay-awake request.");
        fallbackAwake = enabled;
        return new { message = enabled ? "Stay awake is on while the manager runs." : "Stay awake is off.", awake = Awake };
    }

    internal object SetPreventSleep(bool enabled)
    {
        sleepGuard.Set(enabled);
        Preferences.PreventSleep = enabled;
        Preferences.Save();
        RefreshWindows();
        return new { preventSleep = enabled, message = enabled
            ? "System and background execution requests are active; the display may turn off. Windows may still limit them on battery."
            : "Normal Windows sleep timing restored." };
    }

    internal void Notify(string message)
    {
        XiControl.Log.Write(message);
        if (Exiting || dispatcher.IsDisposed) return;
        dispatcher.BeginInvoke((Action)(() =>
        {
            if (Exiting) return;
            popup.Send(new { notice = message });
            manager?.Send(new { notice = message });
            if (!popup.Visible && manager?.Visible != true && Advanced?.Visible != true)
            {
                tray.BalloonTipTitle = "Xiaomi quick controls"; tray.BalloonTipText = message; tray.ShowBalloonTip(5000);
            }
        }));
    }
    private void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (Exiting) return;
        if (e.Mode == PowerModes.Suspend) { ManualDisplay.RestoreLogged(); Hardware.BeforeSuspend(); return; }
        // Windows can terminate process power requests on entry to sleep. Restore the user's
        // saved system-only request after resume without asking to keep the OLED display on.
        if (e.Mode == PowerModes.Resume)
        {
            _ = Task.Run(ManualDisplay.RestoreIdleLogged);
            ReassertPreventSleep("resume");
        }
        if (e.Mode == PowerModes.StatusChange)
        {
            var current = SystemInformation.PowerStatus.PowerLineStatus;
            var previous = lastOsdPowerSource;
            lastOsdPowerSource = current;
            if (current == System.Windows.Forms.PowerLineStatus.Online && previous != current)
                ReassertPreventSleep("AC connection");
            if (previous != current && previous != System.Windows.Forms.PowerLineStatus.Unknown && current != System.Windows.Forms.PowerLineStatus.Unknown)
                Advanced?.ShowPowerSourceChanged(current == System.Windows.Forms.PowerLineStatus.Online);
        }
        if (e.Mode == PowerModes.StatusChange && SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
            _ = Task.Run(ManualDisplay.RestoreLogged);
        _ = Task.Run(async () =>
        {
            bool held = false;
            try
            {
                await Ready;
                await Queue.WaitAsync();
                held = true;
                if (!Exiting) Hardware.PowerChanged(e.Mode);
            }
            catch (Exception ex) { XiControl.Log.Ex("Power.Event", ex); }
            finally { if (held) Queue.Release(); Post(RefreshWindows); }
        });
    }
    private void ReassertPreventSleep(string reason) => Post(() =>
    {
        if (!Preferences.PreventSleep) return;
        try { sleepGuard.Dispose(); sleepGuard.Set(true); XiControl.Log.Write("SleepGuard.Reassert " + reason); }
        catch (Exception ex) { XiControl.Log.Ex("SleepGuard.Reassert", ex); Notify("Prevent sleep could not be restored after " + reason + "."); }
    });
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        Hardware.BeforeSuspend();
        if (!dispatcher.IsDisposed) dispatcher.BeginInvoke((Action)(async () => await QuitAsync("session " + e.Reason)));
    }
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Preferences.Appearance == "system") Post(ApplyAppearance);
    }
    internal async Task QuitAsync(string reason = "window close")
    {
        if (Exiting) return;
        XiControl.Log.Write("Resident.Quit reason=" + reason);
        Exiting = true;
        policyTimer.Stop();
        try { await Ready; }
        catch (Exception ex) { XiControl.Log.Ex("Hardware.Shutdown", ex); }
        await Queue.WaitAsync();
        try
        {
            await Task.Run(Hardware.BeforeSuspend);
            await Task.Run(ManualDisplay.Restore);
            Advanced?.Shutdown();
            if (pendingBackup is not null)
            {
                try { SettingsBackup.Restore(pendingBackup); RestartAfterImport = true; }
                catch (Exception ex) { XiControl.Log.Ex("SettingsBackup.Restore", ex); MessageBox.Show("The backup could not be restored. Current settings were retained. Check the native log.", "PC Manager"); }
            }
            sleepGuard.Dispose();
            SetThreadExecutionState(0x80000000u);
            popup.Close(); manager?.Close();
            tray.Visible = false;
            ExitThread();
        }
        finally { Queue.Release(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            XiControl.Log.Write("Resident.Dispose");
            Exiting = true;
            SystemEvents.PowerModeChanged -= OnPower;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            copilot.Dispose(); shortcuts.Dispose(); policyTimer.Dispose(); tray.ContextMenuStrip?.Dispose(); tray.Dispose();
            ManualDisplay.Dispose(); popup.Dispose(); manager?.Dispose(); dispatcher.Dispose();
            sleepGuard.Dispose();
            Advanced?.Dispose();
            Router.Dispose(); Hardware.Dispose();
            Hardware.ReconcileRequested = null; Hardware.PolicyReadingsChanged = null;
        }
        base.Dispose(disposing);
    }
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
