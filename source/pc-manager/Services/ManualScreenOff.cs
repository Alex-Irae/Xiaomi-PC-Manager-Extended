// Purpose: request normal display idle promptly and restore its original timeout after wake.
// Dependencies: Windows power policy and display notifications; no OEM process or packages.
// Outputs: a temporary display-timeout recovery journal in app data.
// Command: app/PCManager.exe --screen-off (recovery runs at resident startup).
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace XiaomiAIManager.Services;

internal sealed class ManualScreenOff : IDisposable
{
    private sealed record Journal(Guid Scheme, uint Ac, uint Dc, int? Delay, bool Active);
    private sealed record IdleJournal(Guid Scheme, uint OriginalSeconds, bool OnAc, bool Active);
    private static Guid Subgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static Guid Password = new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");
    private static Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    // Interactive applications must use session display status, not the kernel/console channel.
    private static Guid DisplayState = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    private readonly object sync = new();
    private readonly string journalPath = Path.Combine(Preferences.DataDirectory, "manual-screen-off.json");
    private readonly string idleJournalPath = Path.Combine(Preferences.DataDirectory, "manual-display-idle.json");
    private IntPtr notification;
    private volatile bool sawOff;
    private volatile bool active;
    private volatile bool idleActive;
    private volatile bool sawIdleOff;
    private volatile int displayState = -1;
    private IntPtr executionRequest;
    internal ManualScreenOff(IntPtr window) => Bind(window);
    internal void Bind(IntPtr window)
    {
        if (notification != IntPtr.Zero) UnregisterPowerSettingNotification(notification);
        notification = RegisterPowerSettingNotification(window, ref DisplayState, 0);
        if (notification == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        XiControl.Log.Write($"ScreenOff.Notifications window={window} subscription={notification}");
    }
    internal bool Active => active;
    internal bool DisplayOffSeen => sawOff;
    internal int CurrentDisplayState => displayState;
    private void Save(Journal state) { Directory.CreateDirectory(Preferences.DataDirectory); File.WriteAllText(journalPath, JsonSerializer.Serialize(state)); }
    private static void Check(uint result) { if (result != 0) throw new System.ComponentModel.Win32Exception((int)result); }
    private static Guid CurrentScheme()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); } finally { LocalFree(pointer); }
    }
    // This uses Windows' ordinary display timeout, not SC_MONITORPOWER, which this
    // S0 laptop treats as an explicit Modern Standby request. Restore on wake so
    // the user's saved timeout is not changed during normal use.
    internal void RequestIdleDisplayOff()
    {
        lock (sync)
        {
            if (idleActive || displayState == 0) return; // The panel is already off.
            var scheme = CurrentScheme();
            bool onAc = SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            uint original;
            if (onAc) Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, out original));
            else Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, out original));
            Directory.CreateDirectory(Preferences.DataDirectory);
            var state = new IdleJournal(scheme, original, onAc, true);
            File.WriteAllText(idleJournalPath, JsonSerializer.Serialize(state));
            try
            {
                if (onAc) Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, 1));
                else Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, 1));
                Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
                idleActive = true; sawIdleOff = displayState == 0;
                XiControl.Log.Write($"ScreenOff.IdleTimeout started source={(onAc ? "AC" : "battery")} previous={original}s");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10000);
                    if (idleActive && !sawIdleOff) RestoreIdleLogged();
                });
            }
            catch { RestoreIdle(); throw; }
        }
    }
    internal void RestoreIdleLogged()
    {
        try { RestoreIdle(); }
        catch (Exception ex) { XiControl.Log.Ex("ScreenOff.RestoreIdle", ex); }
    }
    internal void RestoreIdle()
    {
        lock (sync)
        {
            if (!File.Exists(idleJournalPath)) { idleActive = false; return; }
            var state = JsonSerializer.Deserialize<IdleJournal>(File.ReadAllText(idleJournalPath));
            if (state is null || !state.Active) { idleActive = false; return; }
            var scheme = state.Scheme;
            uint current;
            if (state.OnAc) Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, out current));
            else Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, out current));
            if (current == 1)
            {
                if (state.OnAc) Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, state.OriginalSeconds));
                else Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, state.OriginalSeconds));
                if (CurrentScheme() == scheme) Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
            }
            File.WriteAllText(idleJournalPath, JsonSerializer.Serialize(state with { Active = false }));
            idleActive = false; sawIdleOff = false;
            XiControl.Log.Write($"ScreenOff.IdleTimeout restored previous={state.OriginalSeconds}s");
        }
    }
    internal void Prepare()
    {
        lock (sync)
        {
            if (active || Program.TestMode) return;
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (machine?.GetValue("InactivityTimeoutSecs") is int seconds && seconds > 0)
                throw new InvalidOperationException("Windows has an enforced inactivity-lock policy. Screen off cannot override it.");
            var scheme = CurrentScheme();
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref Password, out uint ac));
            Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref Password, out uint dc));
            using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true) ?? throw new InvalidOperationException("Windows sign-in preferences are unavailable.");
            var before = new Journal(scheme, ac, dc, desktop.GetValue("DelayLockInterval") as int?, true);
            Save(before); // Crash recovery exists before any policy is changed.
            try
            {
                Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref Password, 0));
                Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref Password, 0));
                desktop.SetValue("DelayLockInterval", -1, RegistryValueKind.DWord);
                Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
                // On AC, keep this resident runnable during Modern Standby's screen-off phase.
                // Battery sleep and the user's display/sleep timeout values remain untouched.
                if (SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online)
                {
                    var reason = new Reason { Version = 0, Flags = 1, Text = "Xiaomi manual screen off: background controls" };
                    executionRequest = PowerCreateRequest(ref reason);
                    if (executionRequest == new IntPtr(-1) || !PowerSetRequest(executionRequest, 3))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                active = true; sawOff = displayState == 0; // A remote caller can request off while it is already off.
            }
            catch { Restore(); throw; }
        }
    }
    internal void DisplayChanged(IntPtr data)
    {
        if (Marshal.PtrToStructure<Guid>(data) != DisplayState || Marshal.ReadInt32(data, 16) != 4) return;
        int value = Marshal.ReadInt32(data, 20);
        displayState = value;
        XiControl.Log.Write($"ScreenOff.Display state={value} active={active}");
        if (idleActive)
        {
            if (value == 0) sawIdleOff = true;
            else if (value == 1 && sawIdleOff) _ = Task.Run(RestoreIdleLogged);
        }
        // The first registration notification is On; it must not undo a request still preparing.
        if (!active) return;
        if (value == 0) sawOff = true;
        else if (value == 1 && sawOff) _ = Task.Run(RestoreLogged);
    }
    internal void RestoreLogged()
    {
        try { Restore(); }
        catch (Exception ex) { XiControl.Log.Ex("ScreenOff.Restore", ex); }
    }
    internal void Restore()
    {
        lock (sync)
        {
            if (executionRequest != IntPtr.Zero && executionRequest != new IntPtr(-1))
            { PowerClearRequest(executionRequest, 3); CloseHandle(executionRequest); }
            executionRequest = IntPtr.Zero;
            if (!File.Exists(journalPath)) return;
            var saved = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath));
            if (saved is null || !saved.Active) return;
            var scheme = saved.Scheme;
            // Preserve a concurrent external edit rather than restoring over the user's choice.
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref Password, out uint ac));
            Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref Password, out uint dc));
            if (ac == 0) Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref Password, saved.Ac));
            if (dc == 0) Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref Password, saved.Dc));
            using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
            if (desktop?.GetValue("DelayLockInterval") is int delay && delay == -1)
            {
                if (saved.Delay is int original) desktop.SetValue("DelayLockInterval", original, RegistryValueKind.DWord);
                else desktop.DeleteValue("DelayLockInterval", false); // Only our temporary registry value.
            }
            if (CurrentScheme() == scheme) Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
            Save(saved with { Active = false }); active = false; sawOff = false;
        }
    }
    public void Dispose() { RestoreIdleLogged(); RestoreLogged(); if (notification != IntPtr.Zero) UnregisterPowerSettingNotification(notification); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 32)]
    private struct Reason { public uint Version, Flags; [MarshalAs(UnmanagedType.LPWStr)] public string Text; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref Reason context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr handle, int type);
    [DllImport("kernel32.dll")] private static extern bool PowerClearRequest(IntPtr handle, int type);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr window, ref Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}

