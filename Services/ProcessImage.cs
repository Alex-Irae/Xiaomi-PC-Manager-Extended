// Purpose: identify a process by its kernel-reported executable without reading its module memory.
// Dependencies: Windows kernel32; no packages. Output: verified Win32 image path or null.
// Command: used by XiaomiBridge inside app/XiaomiAIManager.exe --validate-oem.
using System.Runtime.InteropServices;
using System.Text;

namespace XiaomiAIManager.Services;

internal static class ProcessImage
{
    internal static string? PathFor(int processId)
    {
        IntPtr process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768);
            int size = path.Capacity;
            return QueryFullProcessImageNameW(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
