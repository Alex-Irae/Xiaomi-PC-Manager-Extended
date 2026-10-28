using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using XiControl.SystemIntegration;
using PowerStatus = XiControl.SystemIntegration.PowerStatus;
using PowerLineStatus = XiControl.SystemIntegration.PowerLineStatus;

namespace XiaomiAIManager.Services;

/// <summary>
/// The automatic display rate, set on the sign-in screen when the PC starts. The resident starts about three seconds
/// after sign-in, and its first changes were refused while the sign-in animation held the screen: the switch, with its
/// black frames, came half a minute later in the middle of work. A task of Windows itself (account SYSTEM, at start)
/// runs this file with <c>--boot-refresh AC BATTERY</c>; it starts a copy of itself in the console session
/// (<c>--boot-refresh-screen</c>), which joins the desktop on screen and sets the rate for the power source. The
/// resident then finds the rate already right and changes nothing. The resident keeps the task in line with its
/// setting (<see cref="Sync"/>); it never reads a user's files, the rates are in its command line.
/// Result of the task (Task Scheduler's "last run result"): 0 changed, 1 already right, 2 display refused,
/// 3 no console session, 4 could not start in the session or bad arguments.
/// </summary>
internal static class BootRefresh
{
    static string TaskName => "XiaomiRevampSuiteBoot_" + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? XiaomiRevamp.Suite.SuiteEnvironment.Identity : "Standalone");
    static readonly object Sync_ = new();
    static string? registered = "unknown"; // command line last given to the task; null = no task

    /// <summary>Registers the task with the current rates, or removes it when the automatic rate is off.</summary>
    // ponytail: one task per install with the rates of the last user whose resident ran; per-user rates at boot
    // would need the signed-in user, who is not known before sign-in.
    internal static void Sync(Preferences preferences)
    {
        if (Program.TestMode || Environment.ProcessPath is not string exe || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return;
        string? wanted = preferences.AutoRefresh ? $"--boot-refresh {preferences.AcRefreshRate} {preferences.BatteryRefreshRate}" : null;
        lock (Sync_) { if (wanted == registered) return; registered = wanted; }
        _ = Task.Run(() =>
        {
            string schtasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
            string? xml = null;
            try
            {
                if (wanted is not null)
                {
                    xml = Path.Combine(Preferences.DataDirectory, "boot-task.xml");
                    File.WriteAllText(xml, Xml(exe, wanted), Encoding.Unicode);
                }
                var start = new ProcessStartInfo(schtasks) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in wanted is null ? new[] { "/Delete", "/TN", TaskName, "/F" } : new[] { "/Create", "/TN", TaskName, "/XML", xml!, "/F" }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                string error = process.StandardError.ReadToEnd(); process.StandardOutput.ReadToEnd(); process.WaitForExit(10000);
                if (wanted is not null && process.ExitCode != 0) { XiControl.Log.Write("BootRefresh: task not registered: " + error.Trim()); lock (Sync_) registered = "unknown"; }
                else if (wanted is not null) XiControl.Log.Write("BootRefresh: task " + wanted);
            }
            catch (Exception ex) { XiControl.Log.Ex("BootRefresh.Sync", ex); lock (Sync_) registered = "unknown"; }
            finally { if (xml is not null) try { File.Delete(xml); } catch { } }
        });
    }

    static string Xml(string exe, string arguments) => $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Xiaomi Revamp PC Manager: display rate for the power source on the sign-in screen</Description></RegistrationInfo>
  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
  <Principals><Principal id=""System""><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>false</StartWhenAvailable>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""System""><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{SecurityElement.Escape(arguments)}</Arguments></Exec></Actions>
</Task>";

    /// <summary>Entry for both modes; returns the process exit code.</summary>
    internal static int Run(string[] args)
    {
        XiControl.Log.Enabled = false; // as SYSTEM there is no user folder for a log; the exit code is the record
        if (args.Length != 3 || !int.TryParse(args[1], out int ac) || !int.TryParse(args[2], out int battery) || ac is < 20 or > 360 || battery is < 20 or > 360) return 4;
        return args[0] == "--boot-refresh-screen" ? Screen(ac, battery) : Launch(args);
    }

    // Session 0 (the task): wait for the console session, then start the screen copy there as SYSTEM.
    static int Launch(string[] args)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        uint session;
        while ((session = WTSGetActiveConsoleSessionId()) is 0 or 0xFFFFFFFF)
        {
            if (DateTime.UtcNow > deadline) return 3;
            Thread.Sleep(500);
        }
        IntPtr own = IntPtr.Zero, token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x1 | 0x2 | 0x8 | 0x80 | 0x100, out own) ||
                !DuplicateTokenEx(own, 0x02000000, IntPtr.Zero, 2, 1, out token) ||
                !SetTokenInformation(token, 12, ref session, 4)) return 4;
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\winlogon" };
            var command = new StringBuilder($"\"{Environment.ProcessPath}\" --boot-refresh-screen {args[1]} {args[2]}");
            if (!CreateProcessAsUserW(token, null, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var info)) return 4;
            try
            {
                WaitForSingleObject(info.hProcess, 90000);
                return GetExitCodeProcess(info.hProcess, out uint code) && code <= 3 ? (int)code : 2;
            }
            finally { CloseHandle(info.hProcess); CloseHandle(info.hThread); }
        }
        finally { if (token != IntPtr.Zero) CloseHandle(token); if (own != IntPtr.Zero) CloseHandle(own); }
    }

    // Console session: follow the desktop on screen (the sign-in screen, then the user's desktop) and set the rate.
    // Display changes are refused from a desktop that is not on screen, which is what failed for the resident.
    static int Screen(int ac, int battery)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        IntPtr desktop = IntPtr.Zero;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                IntPtr input = OpenInputDesktop(0, false, 0x10000000);
                if (input != IntPtr.Zero)
                {
                    if (SetThreadDesktop(input)) { if (desktop != IntPtr.Zero) CloseDesktop(desktop); desktop = input; }
                    else CloseDesktop(input);
                }
                var line = PowerStatus.Read().LineStatus;
                if (line != PowerLineStatus.Unknown && RefreshRate.Current() is int current)
                {
                    int target = line == PowerLineStatus.Online ? ac : battery;
                    if (RefreshRate.Resolve(target) is int resolved && resolved == current) return 1;
                    if (RefreshRate.Apply(target) && RefreshRate.Current() == RefreshRate.Resolve(target)) return 0;
                }
                Thread.Sleep(500);
            }
            return 2;
        }
        finally { if (desktop != IntPtr.Zero) CloseDesktop(desktop); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUserW(IntPtr token, string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
}
