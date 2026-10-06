using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XiControl.SystemIntegration;

/// <summary>
/// NPU load as Task Manager reports it: Windows lists an NPU's work in the "GPU Engine" performance
/// counters under the engine type "Neural", one instance per process that uses it. The load is the sum
/// of those instances. No driver and no administrator rights are needed.
/// The first reading after opening only primes the rate counters and returns NaN.
/// </summary>
public sealed class NpuLoad : IDisposable
{
    private const uint FormatDouble = 0x200;
    private const int MoreData = unchecked((int)0x800007D2);
    private IntPtr _query, _counter;
    private bool _off;

    /// <summary>True when Windows has a neural processor device installed.</summary>
    public static bool Present
    {
        get
        {
            // Device class "ComputeAccelerator" (neural processors); each installed device is a numbered subkey.
            try { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}\0000"); return key is not null; }
            catch (Exception ex) { Log.Ex("NpuLoad.Present", ex); return false; }
        }
    }

    public float Read()
    {
        if (_off) return float.NaN;
        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0 ||
                    PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0) { _off = true; return float.NaN; }
                PdhCollectQueryData(_query);
                return float.NaN;
            }
            if (PdhCollectQueryData(_query) != 0) return 0f; // no engine instance exists while nothing uses a GPU or the NPU
            uint size = 0;
            if (PdhGetFormattedCounterArray(_counter, FormatDouble, ref size, out uint count, IntPtr.Zero) != MoreData) return 0f;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, FormatDouble, ref size, out count, buffer) != 0) return float.NaN;
                // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer (8), status (4), padding (4), value (8).
                double total = 0;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * 24;
                    if (Marshal.ReadInt32(item, 8) != 0) continue;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    if (name.EndsWith("engtype_Neural", StringComparison.OrdinalIgnoreCase)) total += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                }
                return (float)Math.Clamp(total, 0, 100);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) { Log.Ex("NpuLoad.Read", ex); _off = true; return float.NaN; }
    }

    /// <summary>Forget the previous sample, so a reading after a pause does not average over the pause.</summary>
    public void Reset() => Dispose();

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}
