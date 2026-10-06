// Purpose: bring a user-requested shortcut target to the foreground after ShellExecute.
// Dependencies: Windows user32; uses exact executable-image checks for custom app targets.
// Outputs: no files; does not activate arbitrary processes with a similar display name.
// Command: app/PCManager.exe --tray, then trigger an assigned app or Settings key.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XiaomiAIManager.Services;

internal static class ForegroundLaunch
{
    internal static void Prepare() => AllowSetForegroundWindow(uint.MaxValue);

    internal static void FocusExecutableSoon(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return;
        string target = Path.GetFullPath(path);
        string processName = Path.GetFileNameWithoutExtension(target);
        FocusSoon(processName, candidate => string.Equals(ProcessImage.PathFor(candidate.Id), target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Brings forward a visible window of the program at this path. False when it is not running or shows none.</summary>
    internal static bool FocusRunning(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        string target = Path.GetFullPath(path);
        bool altSent = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            using (process)
                try
                {
                    if (!string.Equals(ProcessImage.PathFor(process.Id), target, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (nint window in CandidateWindows(process.Id, process.MainWindowHandle))
                    {
                        if (IsIconic(window)) ShowWindowAsync(window, 9); // Restore; a maximized window stays maximized.
                        if (Activate(window, ref altSent)) return true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { /* The program exited while its windows were listed. */ }
        return false;
    }

    internal static void FocusWindowsSettingsSoon() => FocusSoon("SystemSettings", _ => true);

    private static void FocusSoon(string processName, Func<Process, bool> accepted)
    {
        nint previousForeground = GetForegroundWindow();
        _ = Task.Run(async () =>
        {
            bool foundWindow = false;
            bool altSent = false;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100);
                nint current = GetForegroundWindow();
                // A new user action takes priority over our earlier shortcut.
                if (previousForeground != 0 && current != 0 && current != previousForeground
                    && !BelongsTo(current, processName, accepted))
                    return;
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    try
                    {
                        if (!accepted(process)) continue;
                        process.Refresh();
                        foreach (nint window in CandidateWindows(process.Id, process.MainWindowHandle))
                        {
                            foundWindow = true;
                            if (IsIconic(window)) ShowWindowAsync(window, 9); // Preserve maximized windows.
                            if (Activate(window, ref altSent)) return;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    { /* The candidate exited before its window became ready. */ }
                }
            }
            XiControl.Log.Write($"Shortcut focus unavailable for {processName}; windowFound={foundWindow}");
        });
    }

    private static bool BelongsTo(nint window, string processName, Func<Process, bool> accepted)
    {
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName == processName && accepted(process)) return true;
            // Hosted Windows Settings uses ApplicationFrameHost as its top-level owner.
            return processName == "SystemSettings" && HasChildFrom(window, Process.GetProcessesByName("SystemSettings").Select(p =>
            { using (p) return (uint)p.Id; }).ToHashSet());
        }
        catch { return false; }
    }

    private static IEnumerable<nint> CandidateWindows(int pid, nint main)
    {
        var windows = new List<nint>();
        EnumWindows((window, _) =>
        {
            // A program that sits in the tray keeps helper windows (Clash Verge: a visible 13 by 13 tool
            // window). Taking one for the program's window would leave the user looking at nothing.
            if (!IsWindowVisible(window) || (GetWindowLongPtr(window, -20) & 0x80) != 0) return true;
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == (uint)pid || HasChildFrom(window, [(uint)pid]))
                if (!windows.Contains(window)) windows.Add(window);
            return true;
        }, 0);
        if (main != 0 && IsWindowVisible(main) && !windows.Contains(main)) windows.Add(main);
        return windows;
    }

    private static bool HasChildFrom(nint window, HashSet<uint> pids)
    {
        bool found = false;
        EnumChildWindows(window, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint owner);
            if (!pids.Contains(owner)) return true;
            found = true;
            return false;
        }, 0);
        return found;
    }

    private static bool Activate(nint window, ref bool altSent)
    {
        ShowWindowAsync(window, 5);
        if (SetForegroundWindow(window) && GetForegroundWindow() == window) return true;
        nint current = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(current, out _);
        uint callerThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(window, out _);
        if (foregroundThread == 0 || targetThread == 0) return false;
        bool callerAttached = callerThread != foregroundThread && AttachThreadInput(callerThread, foregroundThread, true);
        bool targetAttached = targetThread != foregroundThread && AttachThreadInput(targetThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            if (SetForegroundWindow(window) && GetForegroundWindow() == window) return true;
            // A firmware shortcut does not always count as input to this process.
            // A released Alt key lets Windows reconsider its foreground lock.
            if (!altSent)
            {
                altSent = true;
                keybd_event(0x12, 0, 0, 0);
                keybd_event(0x12, 0, 2, 0);
                return SetForegroundWindow(window) && GetForegroundWindow() == window;
            }
            return false;
        }
        finally
        {
            if (targetAttached) AttachThreadInput(targetThread, foregroundThread, false);
            if (callerAttached) AttachThreadInput(callerThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint sourceThread, uint targetThread, bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);
    private delegate bool EnumWindowProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nint extra);
}

