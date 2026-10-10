using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace XiaomiAIManager;

// Primary: borderless white quick panel. Secondary: conventional manager window.
public sealed class MainWindow : Form
{
    internal const uint ToggleMessage = 0x8000 + 71;
    internal const uint ManagerMessage = 0x8000 + 72;
    internal const uint MonitorSmallMessage = 0x8000 + 73;
    internal const uint MonitorMediumMessage = 0x8000 + 74;
    internal const uint MonitorLargeMessage = 0x8000 + 75;
    internal const uint HideMessage = 0x8000 + 76;
    internal const uint QuitMessage = 0x8000 + 77;
    internal const uint CycleModeMessage = 0x8000 + 78;
    internal const uint BrighterMessage = 0x8000 + 79;
    internal const uint DimmerMessage = 0x8000 + 80;
    internal const uint TravelMessage = 0x8000 + 81;
    internal const uint VerifyMessage = 0x8000 + 82;
    internal const uint ReapplyMessage = 0x8000 + 83;
    internal const uint ValidationMessage = 0x8000 + 84;
    internal const uint OemValidationMessage = 0x8000 + 85;
    internal const uint UiValidationMessage = 0x8000 + 86;
    internal const uint BrightnessValidationMessage = 0x8000 + 87;
    internal const uint ScreenOffValidationMessage = 0x8000 + 88;
    internal const uint ScreenOffMessage = 0x8000 + 89;
    internal const uint SuiteValidationMessage = 0x8000 + 90;
    private const string LocalOrigin = "https://xiaomi-ai.local";
    private readonly ManagerApplication app;
    private readonly bool compact;
    private WebView2 web = new() { Dock = DockStyle.Fill };
    private bool recoveringWeb;
    private long lastWebRecovery;
    private bool webReady, modalOpen;
    private bool managerPlaced;
    private MouseHook? mouseCallback;
    private IntPtr mouseHook;
    private int motionEpoch;
    internal long LastDismissStartedMs { get; private set; }
    internal bool ClosingPopup { get; private set; }
    private Task? initialization;
    private string? validationToken;
    private string? validationPhase;
    private TaskCompletionSource<JsonElement>? validationResult;
    internal string StartPage { get; set; } = "home";
    // Since this window was created; the log lines built on it say where an opening spends its time.
    private readonly System.Diagnostics.Stopwatch lifetime = System.Diagnostics.Stopwatch.StartNew();
    private string DocumentPath => compact ? "/quick.html" : "/index.html";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    internal MainWindow(ManagerApplication app, bool compact)
    {
        this.app = app;
        this.compact = compact;
        Text = compact ? Program.PopupTitle : "PC Manager" + (Program.TestMode ? " -test" : "");
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = compact ? new Size(420, 680) : new Size(1080, 740);
        MinimumSize = compact ? Size.Empty : new Size(900, 650);
        FormBorderStyle = FormBorderStyle.None;
        if (!compact) Padding = new Padding(6);
        ShowInTaskbar = !compact;
        if (!compact) MinimizeBox = true;
        TopMost = compact;
        if (compact) Opacity = 0.999; // Keep layered style stable across fade endpoints and HWND registrations.
        StartPosition = compact ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        BackColor = compact ? Color.FromArgb(240, 241, 243) : Color.White;
        Icon = Services.AppAssets.Icon;
        Controls.Add(web);
        Shown += async (_, _) => await EnsureWebAsync();
        // Display changes can briefly transfer activation to Windows. Outside mouse clicks
        // dismiss through the visible-only hook; activation loss alone is not a user click.
        VisibleChanged += (_, _) =>
        {
            if (!compact) return;
            if (Visible) StartOutsideClicks();
            else { StopOutsideClicks(); ClosingPopup = false; motionEpoch++; }
        };
        VisibleChanged += async (_, _) => await UpdateVisibilityAsync();
        VisibleChanged += (_, _) => TrimHiddenMemory();
        FormClosing += (_, e) =>
        {
            XiControl.Log.Write($"Window.FormClosing compact={compact} reason={e.CloseReason} exiting={app.Exiting}");
            if (app.Exiting || e.CloseReason == CloseReason.WindowsShutDown) return;
            e.Cancel = true;
            if (compact) Hide();
            else app.ReleaseManager(this);
        };
    }
    internal void ApplyAppearance()
    {
        bool dark = app.Preferences.Appearance == "dark";
        if (app.Preferences.Appearance == "system")
        {
            using var theme = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = theme?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        BackColor = dark ? Color.FromArgb(23, 26, 31) : compact ? Color.FromArgb(240, 241, 243) : Color.White;
        web.DefaultBackgroundColor = BackColor;
        if (IsHandleCreated) { int useDark = dark ? 1 : 0; DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int)); }
        Send(new { appearance = app.Preferences.Appearance });
    }

    internal void ShowPopup()
    {
        int epoch = ++motionEpoch; ClosingPopup = false;
        bool animate = AnimationsEnabled;
        if (!Visible) Opacity = animate ? 0.01 : 0.999;
        Rectangle area = PopupWorkArea(Screen.FromPoint(Cursor.Position));
        float scale = app.Preferences.PopupScale / 100f;
        int width = Math.Min((int)(390 * scale * DeviceDpi / 96f), area.Width - 24);
        int rows = (app.Preferences.QuickLinks.Count(link => link.ShowInPanel) + 3) / 4;
        int systemRows = (app.Preferences.QuickSystemActions.Count + 3) / 4;
        int logicalHeight = (app.Preferences.CompactPanel ? 650 : 730) + rows * 83 + (systemRows - 2) * 75;
        int height = Math.Min((int)(logicalHeight * scale * DeviceDpi / 96f), area.Height - 24);
        Size = new Size(width, height);
        if (web.CoreWebView2 is not null) web.ZoomFactor = scale;
        int margin = (int)Math.Round(5 * DeviceDpi / 96f);
        var desired = XiControl.Ui.OsdPlacement.Locate(area, Size, app.Preferences.PopupPosition, margin);
        desired.Offset((int)Math.Round(app.Preferences.PopupOffsetX * DeviceDpi / 96f), (int)Math.Round(app.Preferences.PopupOffsetY * DeviceDpi / 96f));
        if (app.Preferences.OriginalPopupEnabled) desired.Offset(-(int)Math.Round(360 * DeviceDpi / 96f), 0);
        Location = new Point(Math.Clamp(desired.X, area.Left, Math.Max(area.Left, area.Right - width)), Math.Clamp(desired.Y, area.Top, Math.Max(area.Top, area.Bottom - height)));
        Show();
        if (animate) _ = FadeAsync(0.999, epoch);
        if (!app.Preferences.OriginalPopupEnabled) { SetForegroundWindow(Handle); Activate(); web.Focus(); }
    }
    internal async Task DismissPopupAsync()
    {
        if (!compact || !Visible || ClosingPopup) return;
        int epoch = ++motionEpoch; ClosingPopup = true; LastDismissStartedMs = Environment.TickCount64;
        if (AnimationsEnabled) await FadeAsync(0, epoch);
        if (!IsDisposed && epoch == motionEpoch) Hide();
    }
    private async Task FadeAsync(double target, int epoch)
    {
        double start = Opacity;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!IsDisposed && Visible && epoch == motionEpoch)
        {
            double fraction = Math.Min(1, elapsed.Elapsed.TotalMilliseconds / 220);
            double eased = fraction * fraction * (3 - 2 * fraction);
            Opacity = Math.Clamp(start + (target - start) * eased, 0, 0.999);
            if (fraction == 1) break;
            await Task.Delay(16);
        }
    }
    private bool AnimationsEnabled => app.Preferences.PopupAnimations && SystemParametersInfo(0x1042, 0, out bool enabled, 0) && enabled;
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    private void StartOutsideClicks()
    {
        if (mouseHook != IntPtr.Zero) return;
        mouseCallback = (code, message, data) =>
        {
            if (code >= 0 && message.ToInt64() is 0x201 or 0x204 or 0x207)
            {
                var point = Marshal.PtrToStructure<Point>(data);
                if (!modalOpen && !Bounds.Contains(point)) app.Post(() => _ = DismissPopupAsync());
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
    }
    private void StopOutsideClicks()
    {
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
    }
    // Anchor to screen bounds so an auto-hide taskbar cannot shift the popup's position.
    internal static Rectangle PopupWorkArea(Screen screen) => screen.Bounds;
    protected override bool ShowWithoutActivation => compact && app.Preferences.OriginalPopupEnabled;
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyAppearance();
        if (!compact)
        {
            PlaceManager();
        }
        if (compact)
        {
            app.ManualDisplay?.Bind(Handle);
            int corners = 2; // DWMWCP_ROUND, native Windows 11 rounded window.
            DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
            ChangeWindowMessageFilterEx(Handle, ToggleMessage, 1, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, ManagerMessage, 1, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, MonitorSmallMessage, 1, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, MonitorMediumMessage, 1, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, MonitorLargeMessage, 1, IntPtr.Zero);
            for (uint command = HideMessage; command <= SuiteValidationMessage; command++)
                ChangeWindowMessageFilterEx(Handle, command, 1, IntPtr.Zero);
        }
    }
    internal void PlaceManager()
    {
        if (compact || managerPlaced) return;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(Math.Min((int)(900 * DeviceDpi / 96f), area.Width - 32), Math.Min((int)(650 * DeviceDpi / 96f), area.Height - 32));
        Size = new Size(Math.Min((int)(1080 * DeviceDpi / 96f), area.Width - 32), Math.Min((int)(740 * DeviceDpi / 96f), area.Height - 32));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        managerPlaced = true;
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!compact && WindowState == FormWindowState.Minimized && app.Preferences.MinimizeToTray)
            BeginInvoke((Action)(async () => { await Task.Delay(240); if (!IsDisposed && WindowState == FormWindowState.Minimized && app.Preferences.MinimizeToTray) app.ReleaseManager(this); }));
        if (!compact) Padding = WindowState == FormWindowState.Maximized ? Padding.Empty : new Padding(Math.Max(6, (int)Math.Round(6 * DeviceDpi / 96f)));
    }
    protected override void WndProc(ref Message message)
    {
        if (!compact && message.Msg == 0x0083 && message.WParam != IntPtr.Zero) // WM_NCCALCSIZE: retain native animation styles without a visible system frame.
        {
            message.Result = IntPtr.Zero;
            return;
        }
        if (!compact && message.Msg == 0x0084 && WindowState == FormWindowState.Normal) // WM_NCHITTEST
        {
            base.WndProc(ref message);
            if (message.Result == new IntPtr(1)) // HTCLIENT; leave WebView content interactive.
            {
                int packed = unchecked((int)message.LParam.ToInt64());
                Point point = PointToClient(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
                int grip = Padding.Left + 2;
                bool left = point.X < grip, right = point.X >= Width - grip;
                bool top = point.Y < grip, bottom = point.Y >= Height - grip;
                int hit = top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : 1;
                message.Result = new IntPtr(hit);
            }
            return;
        }
        if (compact && message.Msg == 0x0218 && message.WParam.ToInt64() == 0x8013 && message.LParam != IntPtr.Zero)
            app.ManualDisplay?.DisplayChanged(message.LParam);
        if (compact && message.Msg == ToggleMessage) { app.TogglePopup(); return; }
        if (compact && message.Msg == ManagerMessage) { app.OpenManager(); return; }
        if (compact && message.Msg == MonitorSmallMessage) { app.OpenMonitor("small"); return; }
        if (compact && message.Msg == MonitorMediumMessage) { app.OpenMonitor("medium"); return; }
        if (compact && message.Msg == MonitorLargeMessage) { app.OpenMonitor("large"); return; }
        if (compact && message.Msg == HideMessage) { app.HideWindows(); return; }
        if (compact && message.Msg == QuitMessage) { _ = app.QuitAsync("launcher --quit"); return; }
        if (compact && message.Msg == VerifyMessage) { app.VerifyHardware(); return; }
        if (compact && message.Msg == ValidationMessage) { app.ValidateControls(); return; }
        if (compact && message.Msg == OemValidationMessage) { app.ValidateControls("oem"); return; }
        if (compact && message.Msg == UiValidationMessage) { app.ValidateControls("ui"); return; }
        if (compact && message.Msg == SuiteValidationMessage) { app.ValidateControls("suite"); return; }
        if (compact && message.Msg == BrightnessValidationMessage) { app.ValidateControls("brightness"); return; }
        if (compact && message.Msg == ScreenOffValidationMessage) { app.ValidateControls("screenoff"); return; }
        if (compact && message.Msg == ScreenOffMessage) { app.ScreenOff(); return; }
        if (compact && message.Msg == ReapplyMessage) { app.RunShortcut(ReapplyMessage); return; }
        if (compact && message.Msg >= CycleModeMessage && message.Msg <= TravelMessage)
        { app.RunShortcut((uint)message.Msg); return; }
        base.WndProc(ref message);
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (compact) parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW.
            else parameters.Style |= 0x00C00000 | 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000; // Caption/frame enable Windows minimize/maximize transitions; WM_NCCALCSIZE keeps the Xiaomi chrome.
            return parameters;
        }
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (compact && keyData == Keys.Escape && !webReady) { Hide(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }
    private Task EnsureWebAsync() => initialization ??= InitializeWebAsync();
    internal Task PreloadAsync() => EnsureWebAsync();
    internal async Task<object> ProbeQuickAsync(string? imagePath = null)
    {
        bool wasVisible = Visible;
        var opened = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ShowPopup();
            await EnsureWebAsync();
            if (AnimationsEnabled) await Task.Delay(200); // Check the settled browser entrance and focus.
            while (!webReady && opened.ElapsedMilliseconds < 10000) await Task.Delay(20);
            if (!webReady) throw new TimeoutException("Quick-panel document did not load within ten seconds.");
            web.CoreWebView2.Resume();
            await web.CoreWebView2.ExecuteScriptAsync("globalThis.__residentProbe = null; (async () => { const start = performance.now(); try { const r = await Native.call('quick.read'); const w = await Native.call('window.state'); globalThis.__residentProbe = { elapsedMs: performance.now() - start, ready: w.ready, firmwareAvailable: r.hardware.firmwareAvailable, mode: r.hardware.mode, chargeLimit: r.hardware.chargeLimit, careSelectorPresent: Boolean(document.querySelector('[data-limit]')), travelButton: document.getElementById('travel')?.textContent, theme: document.documentElement.dataset.theme, background: getComputedStyle(document.body).backgroundColor, deviceLabel: document.getElementById('device-name')?.textContent, managerTarget: document.querySelector('.panel-header [data-native=\"window.manager\"]')?.dataset.page }; } catch (e) { globalThis.__residentProbe = { error: e.message }; } })();");
            while (opened.ElapsedMilliseconds < 10000)
            {
                string value = await web.CoreWebView2.ExecuteScriptAsync("globalThis.__residentProbe");
                if (value != "null" && value != "undefined")
                {
                    string geometry = await web.CoreWebView2.ExecuteScriptAsync("({width:innerWidth,height:innerHeight,panelHeight:document.querySelector('.quick-panel').getBoundingClientRect().height,horizontalOverflow:document.documentElement.scrollWidth>innerWidth,verticalOverflow:document.documentElement.scrollHeight>innerHeight,footerPresent:!!document.querySelector('footer')})");
                    if (imagePath is not null) await CaptureWebAsync(imagePath);
                    return new { openedMs = opened.Elapsed.TotalMilliseconds, bounds = Bounds, foreground = HasForeground, visible = Visible, workArea = PopupWorkArea(Screen.FromControl(this)), result = JsonSerializer.Deserialize<JsonElement>(value), geometry = JsonSerializer.Deserialize<JsonElement>(geometry) };
                }
                await Task.Delay(20);
            }
            throw new TimeoutException("Quick-panel state request did not return within ten seconds.");
        }
        finally { if (!wasVisible) Hide(); }
    }
    internal async Task<JsonElement> ProbeQuickControlsAsync()
    {
        await EnsureWebAsync(); ShowPopup();
        await web.CoreWebView2.ExecuteScriptAsync("""
            globalThis.__quickControls = null;
            (async () => {
              const rows = [], baseline = (await Native.call('quick.read')).hardware;
              const wait = async () => {
                const start = performance.now();
                while ((busy || reading) && performance.now() - start < 10000) await new Promise(r => setTimeout(r, 10));
                if (busy || reading) throw new Error('Quick action did not settle.');
              };
              try {
                await refresh();
                if (baseline.requestedChargeLimit != null) for (let i = 0; i < 2; i++) {
                  const before = (await Native.call('quick.read')).hardware;
                  document.getElementById('battery-care').click(); await wait();
                  const after = (await Native.call('quick.read')).hardware;
                  rows.push({name: 'battery-care-' + i, pass: after.requestedChargeLimit === (before.requestedChargeLimit < 100 ? 100 : before.careLimit) && !after.travel, before: before.requestedChargeLimit, after: after.requestedChargeLimit});
                }
                if (baseline.refreshRates.length > 1) for (let i = 0; i < 2; i++) {
                  const before = (await Native.call('quick.read')).hardware.refreshRate;
                  document.querySelector('[data-toggle=refresh]').click(); await wait();
                  const after = (await Native.call('quick.read')).hardware.refreshRate;
                  rows.push({name: 'cycle-display-' + i, pass: before !== after, before, after});
                }
                if (baseline.refreshRates.length > 1) for (let i = 0; i < 2; i++) {
                  const before = (await Native.call('quick.read')).hardware.preferences.autoRefresh;
                  document.querySelector('[data-toggle=autorefresh]').click(); await wait();
                  const state = (await Native.call('quick.read')).hardware;
                  const after = state.preferences.autoRefresh;
                  const target = state.powerSource === 'Online' ? state.preferences.acRefreshRate : state.preferences.batteryRefreshRate;
                  rows.push({name: 'automatic-display-' + i, pass: after === !before && (!after || state.refreshRate === target), before, after, rate: state.refreshRate, target});
                }
                if (baseline.powerSource === 'Online' && baseline.requestedChargeLimit < 100 && !baseline.travel) for (let i = 0; i < 2; i++) {
                  document.getElementById('travel').click(); await wait();
                  const after = (await Native.call('quick.read')).hardware;
                  rows.push({name: 'travel-' + i, pass: after.travel === (i === 0) && after.chargeLimit === (i === 0 ? 100 : baseline.requestedChargeLimit)});
                }
              } catch (error) { rows.push({name: 'exception', pass: false, error: error.message}); }
              finally {
                try {
                  if (baseline.requestedChargeLimit != null) await Native.call('battery.limit', {value: baseline.travel ? baseline.careLimit : baseline.requestedChargeLimit});
                  if (baseline.travel) await Native.call('battery.travel', {on: true});
                  await Native.call('display.automatic', {on: baseline.preferences.autoRefresh, ac: baseline.preferences.acRefreshRate, battery: baseline.preferences.batteryRefreshRate});
                  if (baseline.refreshRate != null) await Native.call('display.refresh', {value: baseline.refreshRate});
                  await refresh();
                } catch (error) { rows.push({name: 'restore', pass: false, error: error.message}); }
              }
              globalThis.__quickControls = {rows, restored: (await Native.call('quick.read')).hardware};
            })();
            """);
        for (int attempt = 0; attempt < 600; attempt++)
        {
            string json = await web.CoreWebView2.ExecuteScriptAsync("globalThis.__quickControls");
            if (json is not ("null" or "undefined")) return JsonSerializer.SerializeToElement(new { visible = Visible, report = JsonSerializer.Deserialize<JsonElement>(json), foreground = HasForeground });
            await Task.Delay(50);
        }
        throw new TimeoutException("Quick control checks did not finish.");
    }
    internal async Task<object> ProbeResponsivenessAsync()
    {
        await EnsureWebAsync();
        var gaps = new List<double>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = clock.Elapsed.TotalMilliseconds;
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 16 };
        heartbeat.Tick += (_, _) => { double now = clock.Elapsed.TotalMilliseconds; gaps.Add(now - last); last = now; };
        heartbeat.Start();
        await web.CoreWebView2.ExecuteScriptAsync("""
            globalThis.__fluidProbe = null;
            (async () => {
              const frames = [], state = (await Native.call('state.read')).hardware;
              if (!state.hapticsAvailable || !state.vibration) { globalThis.__fluidProbe = { unavailable: true }; return; }
              let previous = performance.now();
              const beat = setInterval(() => { const now = performance.now(); frames.push(now - previous); previous = now; }, 16);
              const start = performance.now();
              try {
                // Same confirmed value still executes the real HID write/commit/readback, with native history.
                await Native.call('touchpad.vibration', {value: state.vibration});
                globalThis.__fluidProbe = { operationMs: performance.now() - start, samples: frames.length, maxBrowserGapMs: Math.max(0, ...frames), value: state.vibration };
              } catch (error) { globalThis.__fluidProbe = { error: error.message }; }
              finally { clearInterval(beat); }
            })();
            """);
        for (int attempt = 0; attempt < 600; attempt++)
        {
            await Task.Delay(25);
            string result = await web.CoreWebView2.ExecuteScriptAsync("globalThis.__fluidProbe");
            if (result is "null" or "undefined") continue;
            heartbeat.Stop();
            return new { nativeSamples = gaps.Count, maxNativeGapMs = gaps.DefaultIfEmpty().Max(), report = JsonSerializer.Deserialize<JsonElement>(result),
                limitations = "Native and browser event-loop heartbeat during a real HID write, not optical frame pacing or finger input." };
        }
        throw new TimeoutException("UI responsiveness probe did not finish.");
    }
    // A hidden window (the quick panel waits loaded for its next opening) asks WebView2 to give memory back;
    // showing it restores the normal level. This is a request to the browser, not the suspend and resume that
    // invalidated the controller on this device.
    private void TrimHiddenMemory()
    {
        try
        {
            if (IsDisposed || web.IsDisposed || web.CoreWebView2 is not { } core) return;
            core.MemoryUsageTargetLevel = Visible ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException) { XiControl.Log.Ex("Window.Memory", ex); }
    }
    private Task UpdateVisibilityAsync()
    {
        // WinForms owns controller visibility. Explicit suspend/resume repeatedly invalidated
        // the controller on this device; hidden frontend refreshes already skip their work.
        if (IsDisposed || web.IsDisposed || app.Exiting || !Visible) return Task.CompletedTask;
        try { Send(new { activated = true }); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase))
        {
            XiControl.Log.Ex("Window.Visibility", ex);
            return RecoverWebAsync();
        }
        return Task.CompletedTask;
    }
    private async Task RecoverWebAsync()
    {
        if (recoveringWeb || IsDisposed || app.Exiting || Environment.TickCount64 - lastWebRecovery < 2000) return;
        recoveringWeb = true;
        lastWebRecovery = Environment.TickCount64;
        try
        {
            XiControl.Log.Write("Window.Recover " + DocumentPath);
            var previous = web;
            webReady = false;
            initialization = null;
            web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Remove(previous);
            Controls.Add(web);
            previous.Dispose();
            await EnsureWebAsync();
        }
        finally { recoveringWeb = false; }
    }
    private async Task InitializeWebAsync()
    {
        try
        {
            string assets = Path.Combine(AppContext.BaseDirectory, "www");
            if (!File.Exists(Path.Combine(assets, DocumentPath.TrimStart('/'))))
                throw new FileNotFoundException("The www UI folder is missing beside the application.");
            // The pages are served from disk under made-up host names. Chromium still tried to resolve those
            // names for every window and waited about two seconds for the lookup to fail before loading
            // scripts and styles. Nothing here uses the network, so every lookup fails at once instead.
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Services.Preferences.DataDirectory, "WebView2"), new CoreWebView2EnvironmentOptions("--host-resolver-rules=\"MAP * ~NOTFOUND\""));
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("xiaomi-ai.local", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            Directory.CreateDirectory(Services.AppLinks.IconDirectory);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("xiaomi-icons.local", Services.AppLinks.IconDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("xiaomi-assets.local", Path.Combine(AppContext.BaseDirectory, "assets"), CoreWebView2HostResourceAccessKind.DenyCors);
            Directory.CreateDirectory(Services.AppAssets.CustomDirectory);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("xiaomi-custom-assets.local", Services.AppAssets.CustomDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;
            web.CoreWebView2.Settings.IsZoomControlEnabled = false;
            if (compact) web.ZoomFactor = app.Preferences.PopupScale / 100f;
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!Trusted(e.Uri)) e.Cancel = true; };
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.ProcessFailed += (_, e) =>
            {
                XiControl.Log.Write("Window.ProcessFailed " + e.ProcessFailedKind);
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    app.Post(async () => await RecoverWebAsync());
                else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                    app.Post(() => web.CoreWebView2?.Reload());
            };
            web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                webReady = e.IsSuccess;
                if (!compact) XiControl.Log.Write($"Window.Loaded manager {lifetime.ElapsedMilliseconds} ms");
                Send(new { activated = true, backendReady = app.Ready.IsCompletedSuccessfully });
                if (!compact) SelectManagerPage(StartPage);
                _ = UpdateVisibilityAsync();
                TrimHiddenMemory();
            };
            web.Source = new Uri(LocalOrigin + DocumentPath);
        }
        catch (Exception ex)
        {
            XiControl.Log.Ex("Window.Initialize", ex);
            MessageBox.Show("The local interface could not initialize. Check the WebView2 Runtime and the www folder.\n\n" + ex.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Hide();
        }
    }
    private bool Trusted(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "xiaomi-ai.local" && uri.IsDefaultPort
        && uri.AbsolutePath == DocumentPath;
    internal void SelectManagerPage(string page)
    {
        if (!compact) Send(new { managerPage = page });
    }
    private bool SelectXiaomiComponent(string kind)
    {
        string file = Services.XiaomiBridge.ExpectedFileName(kind);
        bool previousModal = modalOpen;
        modalOpen = true;
        try
        {
            using var picker = new OpenFileDialog
            {
                Title = $"Select {file}", Filter = $"{file}|{file}", CheckFileExists = true
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return false;
            string selected = Services.XiaomiBridge.ValidateSelection(kind, picker.FileName);
            switch (kind)
            {
                case "manager": app.Preferences.XiaomiExecutable = selected; break;
                case "store": app.Preferences.MiAppStoreExecutable = selected; break;
                case "ai": app.Preferences.XiaoAiExecutable = selected; break;
            }
            lock (app.Hardware.Sync) app.Preferences.Save();
            return true;
        }
        finally { modalOpen = previousModal; }
    }
    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (app.Exiting || !Trusted(e.Source)) return;
        if (e.WebMessageAsJson.Length > (validationResult is null ? 8192 : 2 * 1024 * 1024))
        {
            validationResult?.TrySetException(new InvalidOperationException("Validation report exceeds its bounded 2 MiB bridge limit."));
            return;
        }
        string? id = null;
        bool held = false;
        bool oemHeld = false;
        bool restartAfterReply = false;
        string? timed = null;
        var took = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            // Only an explicitly running local test can return results. This is not a hardware capability.
            if (root.TryGetProperty("validationToken", out var token))
            {
                if (validationResult is null || token.GetString() != validationToken) return;
                if (root.TryGetProperty("progress", out var progress)) XiControl.Log.Write("Validation: " + progress.GetString());
                if (root.TryGetProperty("result", out var report)) validationResult.TrySetResult(report.Clone());
                return;
            }
            if (e.WebMessageAsJson.Length > 8192) return;
            id = root.GetProperty("id").GetString();
            string? method = root.GetProperty("method").GetString();
            if (id is null || id.Length > 80 || method is null || method.Length > 80) return;
            timed = method;
            JsonElement args = root.GetProperty("args").Clone();
            if (method == "window.shown")
            {
                // The page reports that its first complete set of live values is on screen.
                if (!compact) XiControl.Log.Write($"Window.Live manager {lifetime.ElapsedMilliseconds} ms, page {StartPage}");
                Send(new { id, ok = true, data = new { } });
                return;
            }
            // Window actions must remain responsive while a firmware read/write is waiting.
            if (method.StartsWith("window.", StringComparison.Ordinal) && method != "window.awake")
            {
                object windowResult;
                switch (method)
                {
                    case "window.awake": await app.Ready; windowResult = app.SetAwake(args.GetProperty("on").GetBoolean()); break;
                    case "window.live": windowResult = await Task.Run(Services.HardwareService.ReadLive); break;
                    case "window.state": windowResult = new { version = typeof(Program).Assembly.GetName().Version?.ToString(3) + (Program.TestMode ? "-test" : ""), awake = app.Awake, preventSleep = app.PreventSleep, ready = app.Ready.IsCompletedSuccessfully, isolationStatus = app.IsolationStatus, maximized = WindowState == FormWindowState.Maximized, screenBottomGap = compact ? (int?)null : Screen.FromControl(this).Bounds.Bottom - Bounds.Bottom }; break;
                    case "window.reconnect":
                        await app.Ready;
                        await app.Queue.WaitAsync();
                        try { await Task.Run(app.Hardware.Reconnect); }
                        finally { app.Queue.Release(); }
                        app.RefreshWindows();
                        windowResult = new { message = "Firmware interface reconnected; device diagnostics are updating. Imported native icons still require a full app restart." };
                        break;
                    case "window.preventSleep": windowResult = app.SetPreventSleep(args.GetProperty("on").GetBoolean()); break;
                    case "window.maximize":
                        if (compact) throw new ArgumentException("Open the larger manager to maximize it.");
                        var bounds = Screen.FromControl(this).Bounds;
                        MaximizedBounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom - 1); // One physical pixel still activates an auto-hide taskbar.
                        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                        windowResult = new { maximized = WindowState == FormWindowState.Maximized }; break;
                    case "window.minimize":
                        if (compact) throw new ArgumentException("Only the larger manager can be minimized.");
                        WindowState = FormWindowState.Minimized; windowResult = new { minimized = true }; break;
                    case "window.drag":
                        if (compact || WindowState != FormWindowState.Normal) throw new ArgumentException("Only the restored manager window can be moved.");
                        ReleaseCapture();
                        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
                        windowResult = new { moved = true }; break;
                    case "window.screenOff": app.ScreenOff(); windowResult = new { message = app.PreventSleep
                        ? "Display-off timer shortened once. Your saved value returns on wake; Windows may still enter Modern Standby."
                        : "Display-off timer shortened once. Your saved value returns on wake; normal sleep rules still apply." }; break;
                    case "window.manager":
                        string page = args.TryGetProperty("page", out var selected) ? selected.GetString() ?? "home" : "home";
                        if (page is not ("home" or "device" or "performance" or "battery" or "display" or "touchpad" or "keyboard" or "notifications" or "monitor" or "settings" or "tools" or "official"))
                            throw new ArgumentException("Unknown manager page.");
                        app.OpenManager(page);
                        windowResult = new { message = "Opened all controls." };
                        break;
                    case "window.quick": app.TogglePopup(); windowResult = new { message = "Quick controls." }; break;
                    case "window.advanced": app.OpenAdvanced(); windowResult = new { message = "Opening advanced controls." }; break;
                    case "window.monitor":
                        string size = args.TryGetProperty("size", out var view) ? view.GetString() ?? "large" : "large";
                        if (size is not ("small" or "medium" or "large")) throw new ArgumentException("Unknown monitor size.");
                        app.OpenMonitor(size); windowResult = new { message = "Opening " + size + " monitor." }; break;
                    case "window.osdPreview":
                        await app.Ready;
                        if (app.Advanced is null) throw new InvalidOperationException("Advanced controls are unavailable.");
                        app.Advanced.PreviewPerformanceOsd(args.GetProperty("number").GetInt32());
                        windowResult = new { message = "OSD preview only; hardware was not changed." }; break;
                    case "window.lockPreview":
                        await app.AdvancedReady;
                        if (app.Advanced is null) throw new InvalidOperationException("Notification controls are unavailable.");
                        app.Advanced.PreviewLockOsd(); windowResult = new { message = "Notification preview." }; break;
                    case "window.hide":
                        if (compact) await DismissPopupAsync();
                        else BeginInvoke((Action)(() => app.ReleaseManager(this)));
                        windowResult = new { message = "Running in the system tray." };
                        break;
                    default: throw new ArgumentException("Unknown window action.");
                }
                Send(new { id, ok = true, data = windowResult });
                return;
            }
            await app.Ready;
            if (method == "xiaomi.open")
            {
                await app.OemQueue.WaitAsync(); oemHeld = true;
                object oem;
                try { oem = await Task.Run(() => app.Router.Handle(method, args)); }
                catch (Services.MissingXiaomiComponentException missing)
                {
                    if (MessageBox.Show(this, missing.Message + "\n\nLocate it now?", "Optional Xiaomi component",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes
                        || !SelectXiaomiComponent(missing.Kind))
                        throw;
                    oem = await Task.Run(() => app.Router.Handle(method, args));
                }
                Send(new { id, ok = true, data = oem }); return;
            }
            if (method == "display.brightness")
            {
                // Brightness has its own serialized writer and never waits behind diagnostics/OEM navigation.
                object brightness = await Task.Run(() => app.Router.Handle(method, args));
                Send(new { id, ok = true, data = brightness });
                return;
            }
            await app.Queue.WaitAsync();
            held = true;
            if (app.Exiting) return;
            Services.SettingsHistory.Snapshot? before = null;
            if (!compact && validationPhase!="suite" && Services.SettingsHistory.Tracks(method))
            {
                await app.AdvancedReady;
                if (app.Advanced is not null) before = await Task.Run(() => app.History.Capture(method is "settings.reset" or "performance.set" or "battery.limit" or "battery.care" or "battery.travel" or "display.refresh" or "display.cycle" or "input.enabled" or "touchpad.vibration" or "touchpad.pressure"));
            }
            object result;
            switch (method)
            {
                case "window.awake": result = app.SetAwake(args.GetProperty("on").GetBoolean()); break;
                case "settings.history": result = app.History.State; break;
                case "settings.undo": result = await app.History.Move(false); break;
                case "settings.redo": result = await app.History.Move(true); break;
                case "settings.read":
                    await app.AdvancedReady;
                    if (app.Advanced is null) throw new InvalidOperationException("The settings backend did not initialize. Basic controls remain available.");
                    result = await Task.Run(app.Advanced.ReadSettings); break;
                case "settings.apply":
                    await app.AdvancedReady;
                    if (app.Advanced is null) throw new InvalidOperationException("The settings backend did not initialize.");
                    result = await app.Advanced.ApplySettingAsync(args.GetProperty("key").GetString() ?? "", args.GetProperty("value")); break;
                case "settings.reset":
                    await app.AdvancedReady;
                    if (app.Advanced is null) throw new InvalidOperationException("The settings backend did not initialize.");
                    result = await app.Advanced.ResetCategoryAsync(args.GetProperty("group").GetString() ?? ""); break;
                case "settings.save":
                    result = await Task.Run(() => app.Router.Handle(method, args));
                    app.ApplyAppearance(); break;
                case "settings.snapshot":
                    string snapshotPath = await Task.Run(app.Preferences.SaveSnapshot);
                    result = new { message = "Settings snapshot saved to " + snapshotPath, path = snapshotPath }; break;
                case "settings.backupExport":
                    modalOpen = true;
                    try
                    {
                        using var save = new SaveFileDialog { Title = "Export PC Manager settings", Filter = "PC Manager backup (*.zip)|*.zip", DefaultExt = "zip",
                            FileName = "PCManager-backup-" + DateTime.Now.ToString("yyyyMMdd") + ".zip" };
                        if (save.ShowDialog(this) == DialogResult.OK)
                        {
                            await Task.Run(() => Services.SettingsBackup.Export(save.FileName, app.Preferences));
                            result = new { message = "Backup saved outside app data: " + save.FileName };
                        }
                        else result = new { message = "Backup export cancelled." };
                    }
                    finally { modalOpen = false; }
                    break;
                case "settings.backupImport":
                    modalOpen = true;
                    try
                    {
                        using var open = new OpenFileDialog { Title = "Import PC Manager settings", Filter = "PC Manager backup (*.zip)|*.zip", CheckFileExists = true };
                        if (open.ShowDialog(this) == DialogResult.OK)
                        {
                            var backup = await Task.Run(() => Services.SettingsBackup.Prepare(open.FileName));
                            app.ImportOnRestart(backup);
                            restartAfterReply = true;
                            result = new { message = "Backup accepted. PC Manager is restarting to apply it." };
                        }
                        else result = new { message = "Backup import cancelled." };
                    }
                    finally { modalOpen = false; }
                    break;
                case "settings.minimizeToTray":
                    app.Preferences.MinimizeToTray = args.GetProperty("on").GetBoolean();
                    app.Preferences.Save(); result = new { message = "Minimize behavior updated." }; break;
                case "settings.apiToken":
                    await app.AdvancedReady;
                    if (app.Advanced is null) throw new InvalidOperationException("The API backend did not initialize.");
                    result = app.Advanced.GenerateApiToken(); break;
                case "settings.customization": result = Services.AppLinks.Read(app.Preferences); break;
                case "suite.read": result = app.SuiteRead(); break;
                case "settings.recordShortcut": result = app.RecordShortcut(this,args); break;
                case "suite.shortcut": result = app.SuiteSave(args); break;
                case "suite.appearance": result = app.SuiteAppearance(args); break;
                case "suite.startup": result = app.SuiteStartup(args); break;
                case "suite.quit": result = app.SuiteQuit(args); break;
                case "suite.open": app.SuiteOpen(args.GetProperty("action").GetString() ?? ""); result = new { message = "Opened suite application." }; break;
                case "settings.appearance":
                    Services.AppearanceOptions.Save(app.Preferences, args); app.PublishSuiteAppearance(); app.ApplyAppearance(); result = new { message = "Appearance saved." }; break;
                case "settings.paletteSave":
                    result = Services.AppearanceOptions.SavePalette(app.Preferences, args); app.ApplyAppearance(); break;
                case "settings.paletteRename":
                    result = Services.AppearanceOptions.RenamePalette(app.Preferences, args); break;
                case "settings.paletteDelete":
                    result = Services.AppearanceOptions.DeletePalette(app.Preferences, args); app.ApplyAppearance(); break;
                case "settings.profile":
                    modalOpen = true;
                    try { Services.AppearanceOptions.PickProfile(app.Preferences, this); } finally { modalOpen = false; }
                    app.RefreshWindows(); result = new { message = "Profile picture selection finished." }; break;
                case "settings.profileReset":
                    app.Preferences.ProfileImage = null; app.Preferences.Save(); app.RefreshWindows(); result = new { message = "Profile picture reset." }; break;
                case "settings.assetPack":
                    modalOpen = true;
                    try { result = new { message = Services.AppAssets.PickPack(this) }; } finally { modalOpen = false; }
                    app.RefreshWindows(); break;
                case "settings.shortcuts": result = app.SaveShortcuts(args); break;
                case "settings.copilot": result = app.SaveCopilotShortcut(args); break;
                case "settings.customize":
                    await Task.Run(() => { lock (app.Hardware.Sync) Services.AppLinks.Save(app.Preferences, args); });
                    app.RefreshWindows(); result = new { message = "Panel customization saved." }; break;
                case "settings.addApp":
                    modalOpen = true;
                    try { Services.AppLinks.Add(app.Preferences, this); } finally { modalOpen = false; }
                    app.RefreshWindows(); result = new { message = "App link selection finished." }; break;
                case "settings.addFolder":
                    modalOpen = true;
                    try { Services.AppLinks.AddFolder(app.Preferences, this); } finally { modalOpen = false; }
                    app.RefreshWindows(); result = new { message = "Folder link selection finished." }; break;
                case "settings.shortcutApp":
                    modalOpen = true;
                    try { result = (object?)Services.AppLinks.Add(app.Preferences, this) ?? new { message = "App selection cancelled." }; } finally { modalOpen = false; }
                    app.RefreshWindows(); break;
                case "settings.keyApp":
                    await app.AdvancedReady;
                    if (app.Advanced is null) throw new InvalidOperationException("The settings backend did not initialize.");
                    modalOpen = true;
                    try { result = app.Advanced.AssignKeyApp(args.GetProperty("slot").GetString() ?? "", this); } finally { modalOpen = false; }
                    break;
                case "settings.appIcon":
                    modalOpen = true;
                    try { Services.AppLinks.PickIcon(app.Preferences, args.GetProperty("id").GetString() ?? "", this); } finally { modalOpen = false; }
                    app.RefreshWindows(); result = new { message = "Icon selection finished." }; break;
                case "app.open":
                    app.OpenAppLink(args.GetProperty("id").GetString() ?? "");
                    result = new { message = "App link requested." }; break;
                case "shortcut.open":
                    string shortcut = args.GetProperty("name").GetString() ?? "";
                    // Reject unknown shortcuts before allowing this window to dismiss itself.
                    if (!DesktopShortcuts.IsKnown(shortcut)) throw new ArgumentException("Unknown desktop shortcut.");
                    if (compact || shortcut is "Screenshot" or "Clipboard" or "Screen") Hide();
                    await Task.Delay(150); // Give focus back to the previous app before Win+V/Win+Shift+S.
                    DesktopShortcuts.Open(shortcut);
                    result = new { message = "Opened " + shortcut + "." };
                    break;
                case "settings.selectXiaomi":
                    modalOpen = true;
                    try
                    {
                        string kind = args.TryGetProperty("kind", out var selectedKind) ? selectedKind.GetString() ?? "manager" : "manager";
                        SelectXiaomiComponent(kind);
                    }
                    finally { modalOpen = false; }
                    result = app.Router.Handle("xiaomi.components", JsonSerializer.SerializeToElement(new { }));
                    break;
                default: result = await Task.Run(() => app.Router.Handle(method, args)); break;
            }
            if (before is not null) app.History.Record(before, await Task.Run(() => app.History.Capture(before.Physical)), Services.SettingsHistory.Describe(method, args));
            Send(new { id, ok = true, data = result });
            if (restartAfterReply) BeginInvoke((Action)(async () => await app.QuitAsync("settings import")));
        }
        catch (Exception ex)
        {
            XiControl.Log.Ex("Capability", ex);
            string message = ex is ArgumentException or InvalidOperationException ? ex.Message
                : "The operation could not be completed. Refresh the state and check the native log for details.";
            Send(new { id, ok = false, error = message });
        }
        finally
        {
            if (held) app.Queue.Release(); if (oemHeld) app.OemQueue.Release();
            if (!compact && timed is not null && took.ElapsedMilliseconds >= 150) XiControl.Log.Write($"Window.Call {timed} {took.ElapsedMilliseconds} ms");
        }
    }
    internal void Send(object value)
    {
        // Deferred page scripts can issue requests before NavigationCompleted fires. Dropping
        // those replies caused a full 30-second timeout before the popup could retry.
        if (IsDisposed || app.Exiting || web.CoreWebView2 is null || !Trusted(web.CoreWebView2.Source)) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, JsonOptions));
    }
    internal void RefreshIfVisible() { if (Visible) Send(new { activated = true }); }
    internal async Task<object> ProbeLayoutAsync()
    {
        await EnsureWebAsync();
        const string script = "(() => { const reports = []; const old = page; busy = true; for (const key of Object.keys(pages)) { page = key; render(); const nav = document.getElementById('navigation'); const overlap = [...document.querySelectorAll('.MiSettingRow')].filter(row => { const a = row.firstElementChild.getBoundingClientRect(), b = row.lastElementChild.getBoundingClientRect(); return b.top < a.bottom && a.top < b.bottom && b.left < a.right - 1; }).length; const tightActions = [...document.querySelectorAll('.MiCard > .actions')].filter(actions => { const prev = actions.previousElementSibling; return prev?.classList.contains('MiSettingRow') && actions.getBoundingClientRect().top - prev.getBoundingClientRect().bottom < 15; }).length; reports.push({ page: key, navScroll: nav.scrollHeight > nav.clientHeight + 1, overlap, tightActions }); } page = old; busy = false; render(); return { reports, viewport: {width: innerWidth,height:innerHeight} }; })()";
        var original = Size;
        try
        {
            var regular = JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(script));
            Size = MinimumSize;
            await Task.Delay(100);
            var minimum = JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(script));
            return new { regular, minimum, limitations = "DOM bounds at regular and minimum resizable sizes; manual edge dragging and optical layout remain unverified." };
        }
        finally { Size = original; }
    }
    internal async Task CaptureWebAsync(string path)
    {
        await EnsureWebAsync();
        using var output = File.Create(path);
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
    }
    internal async Task CaptureManagerPagesAsync(string directory)
    {
        foreach (string pageName in new[] { "home", "settings", "keyboard", "display" })
        {
            await web.CoreWebView2.ExecuteScriptAsync("showPage(" + JsonSerializer.Serialize(pageName) + ")");
            await Task.Delay(350);
            if (pageName == "display") await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-curve]')?.scrollIntoView({block:'center'})");
            await CaptureWebAsync(Path.Combine(directory, "manager-" + pageName + ".png"));
            if (pageName == "settings")
            {
                await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('.MiCard')].find(card => card.querySelector('h2')?.textContent === 'Optional Xiaomi apps')?.scrollIntoView({block:'center'})");
                await CaptureWebAsync(Path.Combine(directory, "manager-settings-xiaomi-apps.png"));
            }
            if (pageName == "keyboard")
            {
                await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('shortcut-list').innerHTML = shortcutRow({chord:'Ctrl+Alt+K',action:'modes'},shortcutActions()) + shortcutRow({chord:'Ctrl+Alt+S',action:'hz'},shortcutActions())");
                await CaptureWebAsync(Path.Combine(directory, "manager-keyboard-shortcuts.png"));
                await web.CoreWebView2.ExecuteScriptAsync("showPage('keyboard')");
            }
        }
    }
    internal async Task<JsonElement> ValidateBridgeAsync(string run, string phase)
    {
        await EnsureWebAsync();
        web.Focus();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (webReady && await web.CoreWebView2.ExecuteScriptAsync("typeof Native !== 'undefined' && document.getElementById('brightness') !== null") == "true") break;
            if (attempt == 99) throw new InvalidOperationException("The control document did not become ready.");
            await Task.Delay(100);
        }
        validationToken = Guid.NewGuid().ToString("N");
        validationPhase=phase;
        validationResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "www", phase=="suite"?"suite-validation.js":"validation.js"));
            await web.CoreWebView2.ExecuteScriptAsync(script + "\n"+(phase=="suite"?"runSuiteValidation":"runControlValidation")+"(" + JsonSerializer.Serialize(validationToken) + "," + JsonSerializer.Serialize(phase) + ");");
            return await validationResult.Task.WaitAsync(TimeSpan.FromMinutes(4));
        }
        finally { validationResult = null; validationToken = null; validationPhase=null; }
    }
    internal void FocusDocument() => web.Focus();
    private bool HasForeground => GetAncestor(GetForegroundWindow(), 2) == Handle;
    protected override void Dispose(bool disposing) { if (disposing) StopOutsideClicks(); base.Dispose(disposing); }
    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int kind, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilter);
}
