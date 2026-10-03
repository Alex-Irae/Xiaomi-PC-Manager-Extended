// Purpose: prevent idle system sleep while allowing the normal display-off timer.
// Dependencies: Windows power-request API; no packages or OEM process.
// Outputs: process-scoped system and execution requests while enabled, no display request.
// Command: app/PCManager.exe --tray (controlled from Quick controls > Prevent sleep).
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XiaomiAIManager.Services;

internal sealed class SleepGuard : IDisposable
{
    private const int SystemRequired = 1;
    private const int ExecutionRequired = 3;
    private IntPtr request;
    internal bool Enabled => request != IntPtr.Zero;

    internal void Set(bool enabled)
    {
        if (enabled == Enabled) return;
        if (!enabled)
        {
            // Windows may expire requests on battery Modern Standby; closing the handle
            // still releases our ownership even if either clear reports an error.
            PowerClearRequest(request, ExecutionRequired);
            PowerClearRequest(request, SystemRequired);
            CloseHandle(request);
            request = IntPtr.Zero;
            return;
        }

        var reason = new Reason { Version = 0, Flags = 1, Text = "PC Manager: prevent idle sleep; allow display off" };
        IntPtr handle = PowerCreateRequest(ref reason);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!PowerSetRequest(handle, SystemRequired))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(handle);
            throw new Win32Exception(error);
        }
        if (!PowerSetRequest(handle, ExecutionRequired))
        {
            int error = Marshal.GetLastWin32Error();
            PowerClearRequest(handle, SystemRequired);
            CloseHandle(handle);
            throw new Win32Exception(error);
        }
        request = handle;
    }

    public void Dispose()
    {
        if (request == IntPtr.Zero) return;
        PowerClearRequest(request, ExecutionRequired);
        PowerClearRequest(request, SystemRequired);
        CloseHandle(request);
        request = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 32)]
    private struct Reason { public uint Version, Flags; [MarshalAs(UnmanagedType.LPWStr)] public string Text; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref Reason context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr handle, int type);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

