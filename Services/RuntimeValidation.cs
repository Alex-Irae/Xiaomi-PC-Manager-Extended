// Purpose: exercise real WebView requests and restore both device state and live resident preferences.
// Dependencies: running elevated resident, internal panel and supplied Xiaomi interfaces; no new packages.
// Outputs: fresh results/NNN_timestamp_controls/{config,baseline,summary}.json, including restoration errors.
// Command: app/PCManager.exe --validate-controls. Briefly changes modes/display/charge/haptics/input.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using XiControl.Config;
using XiControl.SystemIntegration;

namespace XiaomiAIManager.Services;

internal static class RuntimeValidation
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static void CopyProperties<T>(T saved, T live, string? skip = null)
    {
        foreach (var property in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name != skip))
            property.SetValue(live, property.GetValue(saved));
    }
    private static string PowerRequests()
    {
        using var process = Process.Start(new ProcessStartInfo("powercfg.exe", "/requests")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("powercfg did not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("powercfg /requests failed: " + error);
        return output;
    }
    internal static async Task RunAsync(ManagerApplication app, MainWindow window, string phase)
    {
        object[] OemProcesses() => Process.GetProcesses().Where(p => p.ProcessName is "XiaomiPcManager" or "XiaomiPcHost" or "OSDUtility" or "OSDLauncher" or "XiControl")
            .Select(p => { using (p) return (object)new { p.ProcessName, p.Id, image = ProcessImage.PathFor(p.Id) }; }).ToArray();
        var oemBefore = OemProcesses();
        object[]? oemDuring = null;
        string root = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(root);
        int next = Directory.EnumerateDirectories(root).Select(path => int.TryParse(Path.GetFileName(path).Split('_')[0], out int n) ? n : 0).DefaultIfEmpty().Max() + 1;
        string run = Path.Combine(root, $"{next:D3}_{DateTime.Now:yyyyMMddTHHmmssfff}_{phase}");
        Directory.CreateDirectory(run);
        void Save(string file, object value) => File.WriteAllText(Path.Combine(run, file), JsonSerializer.Serialize(value, Json));
        var saved = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(app.Preferences, Json), Json)!;
        var config = app.Advanced?.Configuration;
        var errors = new List<string>();
        JsonElement? report = null;
        object before;
        int? brightness, rate, charge;
        XiControl.Wmi.PerfMode? mode;
        TouchpadHapticsState? haptics;
        await app.Hardware.RefreshDiagnosticsForValidationAsync();
        await app.Queue.WaitAsync();
        try
        {
            lock (app.Hardware.Sync)
            {
                before = app.Hardware.Read(); brightness = Brightness.Get(); rate = RefreshRate.Current();
                charge = app.Hardware.SharedFirmware.GetChargeLimit(); mode = app.Hardware.SharedFirmware.GetPerfMode();
                haptics = app.Hardware.SharedHaptics.Read();
            }
        }
        finally { app.Queue.Release(); }
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MiDeviceService");
        Save("config.json", new { seed = (int?)null, phase, time = DateTimeOffset.Now, executable = Environment.ProcessPath,
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PCManager.dll")))),
            windows = Environment.OSVersion.VersionString, window.DeviceDpi, window.ClientSize,
            displays = Screen.AllScreens.Select(s => new { s.DeviceName, s.Bounds, s.WorkingArea, s.Primary }),
            oemServiceStartup = service?.GetValue("Start"), isolationRequested = OemServiceControl.IsolationRequested,
            oemExecutable = new XiaomiBridge(app.Preferences).Locate(), oemBefore,
            protocol = "Real frontend Native.call through trusted WebView2 dispatcher. Changed brightness values and DOM slider burst; firmware readbacks; reversible input toggles; no stress load or driver installation.",
            limitations = "Programmatic DOM input, not physical mouse latency. API readback is not an optical transition. Physical charging compares direct battery status above a care limit versus full, not a natural threshold crossing or externally metered current. No forced reboot/sleep. Active user/background workloads uncontrolled." });
        Save("baseline.json", new { hardware = before, preferences = saved, brightness, rate, charge, mode, haptics });
        XiControl.Log.Write("Validation started: " + run);
        try
        {
            if (phase == "screenoff") Save("screen-off.json", new { skipped = true, reason = "Display-off is user-operated; automatic display-off testing remains suspended after wake flickering was reported." });
            if (phase is not ("oem" or "screenoff"))
            {
                try
                {
                    var probes = new List<object>();
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        probes.Add(await app.ProbePopupAsync());
                        await Task.Delay(120);
                    }
                    Save("popup.json", new { probes, limitations = "Programmatic repeated show/state/hide, not physical mouse or optical timing." });
                }
                catch (Exception ex) { Save("popup.json", new { error = ex.Message }); }
                window.Show(); window.Activate();
            }
            report = phase == "screenoff" ? JsonSerializer.SerializeToElement(new { phase, probe = "screen-off.json" }) : await window.ValidateBridgeAsync(run, phase);
            if (phase == "oem")
            {
                string? executable = new XiaomiBridge(app.Preferences).Locate();
                object closed = executable is null ? new { error = "No OEM executable." } : OemSession.CloseMainWindowsForValidation(executable);
                foreach (string name in new[] { "XaAppStore" })
                foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    if (executable is not null && string.Equals(ProcessImage.PathFor(process.Id), Path.Combine(Path.GetDirectoryName(executable)!, name + ".exe"), StringComparison.OrdinalIgnoreCase))
                        process.CloseMainWindow();
                object? session = null;
                object? hideInjection = null;
                object? beforeHide = null;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    await Task.Delay(2000);
                    session = app.Router.Handle("xiaomi.session", JsonSerializer.SerializeToElement(new { }));
                    if (!OemProcesses().Any(p => JsonSerializer.Serialize(p).Contains("XiaomiPcManager") || JsonSerializer.Serialize(p).Contains("XiaomiPcHost") || JsonSerializer.Serialize(p).Contains("OSDUtility"))
                        && JsonSerializer.Serialize(session).Contains("daily isolation restored")) break;
                    if (attempt == 5 && executable is not null)
                    {
                        beforeHide = session;
                        hideInjection = OemSession.HideMainWindowsForValidation(executable);
                    }
                }
                var remaining = OemProcesses();
                bool cleanupObserved = !remaining.Any(p => JsonSerializer.Serialize(p).Contains("XiaomiPcManager") || JsonSerializer.Serialize(p).Contains("XiaomiPcHost") || JsonSerializer.Serialize(p).Contains("OSDUtility"));
                Save("oem-close.json", new { closed, beforeHide, hideInjection, session, remaining, cleanupObserved,
                    limitations = "Scoped native close request, then controlled main-window hide if needed. Cleanup measured before fallback restoration. This does not prove the physical OEM X button or completed driver scan." });
            }
            if (phase == "ui")
            {
                // Independent Windows readback proves a system request appears only while the toggle is on.
                const string marker = "PC Manager: prevent idle sleep; allow display off";
                try
                {
                    app.SetPreventSleep(false);
                    string beforeRequests = PowerRequests();
                    app.SetPreventSleep(true);
                    string onRequests = PowerRequests();
                    app.SetPreventSleep(false);
                    string offRequests = PowerRequests();
                    bool visibleOnlyOn = !beforeRequests.Contains(marker, StringComparison.Ordinal)
                        && onRequests.Contains(marker, StringComparison.Ordinal) && !offRequests.Contains(marker, StringComparison.Ordinal);
                    Save("sleep-guard.json", new { visibleOnlyOn, beforeRequests, onRequests, offRequests,
                        limitation = "powercfg confirms the request category and lifetime, not a timed sleep or OLED transition." });
                    if (!visibleOnlyOn) throw new InvalidOperationException("Prevent sleep power request did not toggle cleanly in powercfg /requests.");
                }
                finally { app.SetPreventSleep(saved.PreventSleep); }
                Save("quick-controls.json", await app.ProbeQuickControlsAsync());
                app.OpenManager();
                Save("responsiveness.json", await window.ProbeResponsivenessAsync());
                Save("layout.json", await window.ProbeLayoutAsync());
                await window.CaptureManagerPagesAsync(run);
                using (var preview = new XiaomiPerformanceOsd())
                {
                    bool charging = preview.FlashNotification(XiControl.Ui.OsdKind.Charging, 2800, XiControl.Ui.OsdPosition.Bottom, "70");
                    bool refresh = preview.FlashNotification(XiControl.Ui.OsdKind.RefreshRate, 2800, XiControl.Ui.OsdPosition.Bottom, "120");
                    await Task.Delay(150);
                    int count = preview.VisibleCardCount;
                    using var capture = new Bitmap(preview.Width, preview.Height);
                    using (var graphics = Graphics.FromImage(capture))
                        graphics.CopyFromScreen(preview.Location, Point.Empty, preview.Size);
                    capture.Save(Path.Combine(run, "osd-pair.png"), System.Drawing.Imaging.ImageFormat.Png);
                    preview.Hide();
                    Save("osd-pair.json", new { charging, refresh, count, preview.Width, preview.Height, preview.Opacity });
                    if (!charging || !refresh || count != 2 || preview.Width <= preview.Height * 2)
                        throw new InvalidOperationException("Two near-simultaneous Xiaomi OSDs did not appear side by side.");
                }
                app.Preferences.Appearance = "dark"; app.ApplyAppearance();
                if (!XiControl.Ui.FlyoutPalette.Dark) throw new InvalidOperationException("Native monitor palette did not follow Dark appearance.");
                Save("popup-dark.json", await app.ProbePopupAsync(Path.Combine(run, "popup-dark.png")));
                app.Preferences.Appearance = "light"; app.ApplyAppearance();
                if (XiControl.Ui.FlyoutPalette.Dark) throw new InvalidOperationException("Native monitor palette did not follow Light appearance.");
                Save("popup-light.json", await app.ProbePopupAsync(Path.Combine(run, "popup-light.png")));
                app.Preferences.CompactPanel = false;
                Save("popup-roomy.json", await app.ProbePopupAsync(Path.Combine(run, "popup-roomy.png")));
                app.Preferences.CompactPanel = true;
                Save("popup-compact.json", await app.ProbePopupAsync(Path.Combine(run, "popup-compact.png")));
                app.Preferences.CompactPanel = saved.CompactPanel;
            }
            if (phase == "controls" && config is not null)
            {
                var recovery = new List<object>();
                var baseline = JsonSerializer.SerializeToElement(before, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                foreach (string device in new[] { "touchpad", "touchscreen" })
                {
                    if (baseline.GetProperty(device + "Enabled").ValueKind != JsonValueKind.True) continue;
                    foreach (bool keepOff in new[] { false, true })
                    {
                        await Task.Run(() =>
                        {
                            lock (app.Hardware.Sync)
                            {
                                if (device == "touchpad") config.TouchpadKeepOff = keepOff; else config.TouchscreenKeepOff = keepOff;
                                config.Save();
                                try
                                {
                                    app.Hardware.SetInput(device, false);
                                    app.Hardware.RecoverInputs(); // The actual resident startup recovery routine.
                                    var reading = JsonSerializer.SerializeToElement(app.Hardware.Read(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                                    bool enabled = reading.GetProperty(device + "Enabled").GetBoolean();
                                    recovery.Add(new { device, keepOff, enabled, pass = enabled == !keepOff });
                                }
                                finally { app.Hardware.SetInput(device, true); }
                            }
                        });
                    }
                }
                Save("input-recovery.json", new { recovery, limitations = "Actual HID recovery callback with owned disable flags; no Windows reboot performed." });
            }
            if (phase != "oem" && app.Advanced is not null) Save("notifications.json", app.Advanced.ProbeNotificationPlacement());
            oemDuring = OemProcesses();
        }
        catch (Exception ex) { errors.Add("Suite: " + ex.Message); }
        finally
        {
            if (phase == "oem")
                try { await Task.Run(app.Router.EndOemSessionForValidation); }
                catch (Exception ex) { errors.Add("Restore OEM isolation: " + ex.Message); }
            // Recovery does not depend on frontend success or its thirty-second request timeout.
            try { app.SetAwake(saved.XiControl?.Awake ?? false); }
            catch (Exception ex) { errors.Add("Restore awake: " + ex.Message); }
            await app.Queue.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    lock (app.Hardware.Sync)
                    {
                        void Restore(string name, Action action) { try { action(); } catch (Exception ex) { errors.Add("Restore " + name + ": " + ex.Message); } }
                        if (brightness is int b) Restore("brightness", () => app.Hardware.SetBrightness(b));
                        if (phase != "brightness")
                        {
                        if (charge is int c) Restore("charge", () => app.Hardware.SetChargeLimit(c));
                        if (haptics?.Vibration is HapticsVibration v) Restore("vibration", () => { if (!app.Hardware.SharedHaptics.SetVibration(v)) throw new InvalidOperationException("Readback refused"); });
                        if (haptics is not null) Restore("pressure", () => { if (!app.Hardware.SharedHaptics.SetPressure(haptics.Pressure)) throw new InvalidOperationException("Readback refused"); });
                        var baseline = JsonSerializer.SerializeToElement(before, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        foreach (string device in new[] { "touchpad", "touchscreen" })
                            if (baseline.GetProperty(device + "Enabled").ValueKind is JsonValueKind.True or JsonValueKind.False)
                                Restore(device, () => app.Hardware.SetInput(device, baseline.GetProperty(device + "Enabled").GetBoolean()));
                        }
                        if (config is not null && saved.XiControl is not null) CopyProperties(saved.XiControl, config);
                        CopyProperties(saved, app.Preferences, nameof(Preferences.XiControl));
                        Restore("prevent sleep", () => app.SetPreventSleep(saved.PreventSleep));
                        if (config is not null) { app.Preferences.XiControl = config; config.Save(); }
                        app.Preferences.Save();
                        if (phase is not ("ui" or "brightness" or "screenoff") && mode is not null) Restore("mode", () => app.Hardware.SetMode(mode.Value.ToString(), remember: false));
                    }
                });
            }
            finally { app.Queue.Release(); }
            await Task.Delay(2000); // Let queued configuration notifications finish before checking recovery.
            if (rate is int savedRate)
                try { await Task.Run(() => { lock (app.Hardware.Sync) app.Hardware.SetRefresh(savedRate); }); }
                catch (Exception ex) { errors.Add("Restore refresh: " + ex.Message); }
            app.ApplyAppearance();
            app.ReapplyShortcuts();
            app.History.Clear(); // Validation restores its baseline; test edits must not remain user-undoable.
            await app.Hardware.RefreshDiagnosticsForValidationAsync();
            object after = await Task.Run(() => { lock (app.Hardware.Sync) return app.Hardware.Read(); });
            bool preferencesRestored = JsonSerializer.Serialize(app.Preferences, Json) == JsonSerializer.Serialize(saved, Json);
            var onDisk = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Preferences.DataDirectory, "settings.json")), Json);
            bool diskPreferencesMatch = JsonSerializer.Serialize(onDisk, Json) == JsonSerializer.Serialize(app.Preferences, Json);
            Save("summary.json", new { time = DateTimeOffset.Now, report, errors, preferencesRestored, diskPreferencesMatch, after, oemDuring, oemAfter = OemProcesses(),
                afterHaptics = app.Hardware.SharedHaptics.Read() });
            if (app.Advanced is not null) Save("keys-after.json", app.Advanced.KeyStatus);
            XiControl.Log.Write($"Validation saved: {run}; preferencesRestored={preferencesRestored}; errors={errors.Count}");
        }
    }
    private static async Task<object> ProbeScreenOffAsync(ManagerApplication app)
    {
        var policies = new[] { "SUB_NONE CONSOLELOCK", "SUB_VIDEO VIDEOIDLE", "SUB_SLEEP STANDBYIDLE" };
        string[] ReadPolicies() => policies.Select(setting =>
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", "/qh SCHEME_CURRENT " + setting) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            string output = p.StandardOutput.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) throw new InvalidOperationException("Power policy read failed."); return output;
        }).ToArray();
        var before = await Task.Run(ReadPolicies);
        void WakeDisplay()
        {
            var input = new[] { new WakeInput { Type = 0, MouseFlags = 1, X = 1 }, new WakeInput { Type = 0, MouseFlags = 1, X = -1 } };
            SendInput((uint)input.Length, input, Marshal.SizeOf<WakeInput>());
        }
        int initialState = app.ManualDisplay.CurrentDisplayState;
        WakeDisplay();
        for (int i = 0; i < 100 && app.ManualDisplay.CurrentDisplayState != 1; i++) await Task.Delay(20);
        int beforeRequest = app.ManualDisplay.CurrentDisplayState;
        object? off = null;
        try
        {
            await Task.Run(app.ManualDisplay.Prepare);
            PostMessage(new IntPtr(0xffff), 0x0112, new IntPtr(0xf170), new IntPtr(2));
            await Task.Delay(2200);
            using var desktop = new DesktopHandle(OpenInputDesktop(0, false, 1));
            var name = new System.Text.StringBuilder(128);
            bool readable = desktop.Handle != IntPtr.Zero && GetUserObjectInformation(desktop.Handle, 2, name, 256, out _);
            off = new { initialState, beforeRequest, app.ManualDisplay.CurrentDisplayState, app.ManualDisplay.Active, app.ManualDisplay.DisplayOffSeen,
                desktop = readable ? name.ToString() : "Unavailable", unlocked = readable && name.ToString() == "Default" };
        }
        finally
        {
            WakeDisplay();
            await Task.Delay(800);
            await Task.Run(app.ManualDisplay.Restore);
        }
        var after = await Task.Run(ReadPolicies);
        return new { off, policyActiveAfterWake = app.ManualDisplay.Active, policiesRestored = before.SequenceEqual(after), before, after,
            limitations = "Explicit wake before a 2.2-second display-off, input-desktop check, then synthetic wake. No remote connection or keyboard illumination measurement." };
    }
    private sealed class DesktopHandle(IntPtr handle) : IDisposable { internal IntPtr Handle => handle; public void Dispose() { if (handle != IntPtr.Zero) CloseDesktop(handle); } }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct WakeInput { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public int X; [FieldOffset(20)] public uint MouseFlags; }
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder text, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, WakeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
