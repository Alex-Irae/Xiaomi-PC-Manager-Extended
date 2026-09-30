// Purpose: return to daily OEM isolation after an explicitly opened Xiaomi manager closes.
// Dependencies: existing Windows process, WMI and reversible service controller APIs.
// Outputs: scoped cleanup logs; no idle process polling outside an explicit OEM session.
// Command: app/XiaomiAIManager.exe --manager, Toolbox, then open and close Xiaomi Manager.
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace XiaomiAIManager.Services;

internal sealed class OemSession : IDisposable
{
    private CancellationTokenSource? pending;
    private volatile string status = "No explicit OEM session.";
    private object lastWindows = Array.Empty<object>();
    internal Func<bool> CleanupEnabled { get; set; } = () => true;
    internal readonly object Sync = new();
    internal void Cancel() => pending?.Cancel();
    internal string Status => status;
    internal object State => new { status, windows = lastWindows, detection = "Visible landscape main window; six-second close grace; installers delay cleanup." };
    internal void Watch(string executable, bool includeQuickPanel = false)
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = new();
        var token = pending.Token;
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        status = "Waiting for the explicit Xiaomi main window.";
        _ = Task.Run(async () =>
        {
            bool seenMain = false;
            var opened = Stopwatch.StartNew();
            DateTime? closedAt = null;
            try
            {
                while (!token.IsCancellationRequested && CleanupEnabled())
                {
                    var processes = VerifiedProcesses(directory);
                    lastWindows = Snapshot(processes);
                    bool mainVisible = MainVisible(processes, includeQuickPanel);
                    if (mainVisible) { seenMain = true; closedAt = null; status = "Xiaomi main window is open; automatic cleanup is armed."; }
                    else if (seenMain)
                    {
                        closedAt ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - closedAt >= TimeSpan.FromSeconds(6))
                        {
                            if (InstallerActive(directory)) status = "Waiting for an installer to finish before Xiaomi cleanup.";
                            else
                            {
                                lock (Sync)
                                {
                                    token.ThrowIfCancellationRequested();
                                    OemServiceControl.RestoreDailyIsolation();
                                }
                                status = "Xiaomi session closed; daily isolation restored.";
                                XiControl.Log.Write(status);
                                return;
                            }
                        }
                    }
                    else if (opened.Elapsed > TimeSpan.FromMinutes(1))
                    { status = "Xiaomi main window was not detected; automatic cleanup was not attempted."; return; }
                    await Task.Delay(2000, token);
                }
                status = "Automatic Xiaomi cleanup is disabled.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status = "Automatic Xiaomi cleanup failed: " + ex.Message; XiControl.Log.Ex("OEM session", ex); }
        }, token);
    }
    private static HashSet<int> VerifiedProcesses(string directory)
    {
        var ids = new HashSet<int>();
        foreach (string name in new[] { "XiaomiPcManager", "XiaomiPcHost" })
        foreach (var process in Process.GetProcessesByName(name))
        using (process)
            if (process.SessionId == Process.GetCurrentProcess().SessionId &&
                string.Equals(ProcessImage.PathFor(process.Id), Path.Combine(directory, name + ".exe"), StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id);
        return ids;
    }
    private static bool MainVisible(HashSet<int> ids, bool includeQuickPanel = false)
    {
        bool found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (ids.Contains((int)pid) && IsWindowVisible(window) && (CloakFlags(window) & 1) == 0)
            {
                // The OEM quick panel is portrait. Only a landscape main window keeps this session open.
                if (IsIconic(window) || GetWindowRect(window, out var r) &&
                    (r.Right - r.Left >= 600 && r.Bottom - r.Top >= 350 && (r.Right - r.Left) > 1.15 * (r.Bottom - r.Top)
                        || includeQuickPanel && r.Right - r.Left >= 250 && r.Bottom - r.Top >= 300))
                { found = true; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    // App-cloaked windows are closed from the user's view; shell-cloaked virtual desktops stay active.
    private static int CloakFlags(IntPtr window) => DwmGetWindowAttribute(window, 14, out int flags, 4) == 0 ? flags : 0;
    private sealed record WindowInfo(long Handle, int Pid, string Title, string Class, bool Visible, bool Minimized, int Width, int Height, int Cloaked);
    private static WindowInfo[] Snapshot(HashSet<int> ids)
    {
        var windows = new List<WindowInfo>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (ids.Contains((int)pid))
            {
                var title = new StringBuilder(256); var kind = new StringBuilder(256);
                GetWindowTextW(window, title, title.Capacity); GetClassNameW(window, kind, kind.Capacity);
                GetWindowRect(window, out var r);
                windows.Add(new(window.ToInt64(), (int)pid, title.ToString(), kind.ToString(), IsWindowVisible(window), IsIconic(window), r.Right - r.Left, r.Bottom - r.Top, CloakFlags(window)));
            }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
    internal static object CloseMainWindowsForValidation(string executable)
    {
        var windows = Snapshot(VerifiedProcesses(Path.GetDirectoryName(Path.GetFullPath(executable))!));
        var requests = windows.Where(w => w.Visible && (w.Minimized || w.Width >= 600 && w.Height >= 350 && w.Width > 1.15 * w.Height))
            .Select(w => new { window = w, closeRequested = PostMessageW(new IntPtr(w.Handle), 0x0010, IntPtr.Zero, IntPtr.Zero) }).ToArray();
        return new { windows, requests };
    }
    internal static object HideMainWindowsForValidation(string executable)
    {
        // Controlled lifecycle injection, not evidence that the OEM X button works.
        var windows = Snapshot(VerifiedProcesses(Path.GetDirectoryName(Path.GetFullPath(executable))!));
        return windows.Where(w => w.Visible && (w.Minimized || w.Width >= 600 && w.Height >= 350 && w.Width > 1.15 * w.Height))
            .Select(w => new { window = w, hideRequested = ShowWindowAsync(new IntPtr(w.Handle), 0) }).ToArray();
    }
    private static bool InstallerActive(string directory)
    {
        // Delay for MSI globally and other installers inside the verified OEM folder. Never kill installers.
        using var query = new ManagementObjectSearcher("SELECT Name,ExecutablePath FROM Win32_Process WHERE Name='msiexec.exe' OR Name LIKE '%setup%' OR Name LIKE '%install%' OR Name LIKE '%update%'");
        using var results = query.Get();
        foreach (ManagementObject process in results)
        using (process)
        {
            if (string.Equals(process["Name"]?.ToString(), "msiexec.exe", StringComparison.OrdinalIgnoreCase)) return true;
            string? image = process["ExecutablePath"]?.ToString();
            if (image is null || image.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    internal static object Describe(string status) => new { status, detection = "Visible landscape main window; six-second close grace; installers delay cleanup." };
    public void Dispose() { pending?.Cancel(); pending?.Dispose(); }
    private delegate bool WindowVisitor(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}
