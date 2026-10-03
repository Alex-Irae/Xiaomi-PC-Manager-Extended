// Purpose: read available Windows hardware inventory once, outside the fast control path.
// Dependencies: Windows WMI/System.Management already used by the resident. No network.
// Outputs: cached English specs sections; unavailable fields and failed providers remain explicit.
// Command: app/PCManager.exe --manager, This PC.
using System.Management;
using System.Globalization;

namespace XiaomiAIManager.Services;

internal static class ComputerSpecs
{
    private static readonly Lazy<Task<object>> Inventory = new(() => Task.Run(Read));
    internal static object? ReadCached() => Inventory.Value.IsCompletedSuccessfully ? Inventory.Value.Result : null;
    private static object Read()
    {
        var errors = new List<string>();
        object Section(string title, string type, params string[] fields)
        {
            var rows = new List<Dictionary<string, string>>();
            try
            {
                using var query = new ManagementObjectSearcher($"SELECT {string.Join(',', fields)} FROM {type}")
                { Options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true } };
                using var results = query.Get();
                foreach (ManagementObject item in results)
                {
                    using (item) rows.Add(fields.ToDictionary(f => f, f => Format(f, item[f])));
                }
            }
            catch (Exception ex) { XiControl.Log.Ex("Specs " + type, ex); errors.Add(title + " provider unavailable."); }
            return new { title, rows };
        }
        var sections = new[]
        {
            Section("Processor", "Win32_Processor", "Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "L2CacheSize", "L3CacheSize"),
            Section("Graphics", "Win32_VideoController", "Name", "DriverVersion", "VideoProcessor", "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate"),
            Section("Memory modules", "Win32_PhysicalMemory", "Manufacturer", "PartNumber", "Capacity", "Speed", "ConfiguredClockSpeed", "DeviceLocator"),
            Section("Storage devices", "Win32_DiskDrive", "Model", "Size", "InterfaceType", "MediaType", "Status"),
            Section("Volumes", "Win32_LogicalDisk", "Name", "VolumeName", "FileSystem", "Size", "FreeSpace"),
            Section("Network adapters", "Win32_NetworkAdapter WHERE PhysicalAdapter=True", "Name", "Manufacturer", "NetConnectionStatus", "Speed"),
            Section("Windows", "Win32_OperatingSystem", "Caption", "Version", "BuildNumber", "OSArchitecture", "LastBootUpTime"),
            Section("Mainboard", "Win32_BaseBoard", "Manufacturer", "Product", "Version")
        };
        return new { timestamp = DateTimeOffset.Now, sections, errors, source = "Windows WMI inventory; reported hardware and driver values." };
    }
    private static string Format(string field, object? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.ToString())) return "Unavailable";
        if (field is "Capacity" or "Size" or "FreeSpace" && double.TryParse(value.ToString(), out double bytes))
            // Convert reported byte capacity to binary GiB, rather than claiming usable application memory.
            return (bytes / (1024 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
        if (field is "L2CacheSize" or "L3CacheSize") return value + " KiB";
        if (field is "MaxClockSpeed" or "ConfiguredClockSpeed") return value + " MHz";
        if (field == "Speed") return value.ToString()!; // Provider-specific: memory MT/s versus network bits/s.
        if (field == "LastBootUpTime")
            try { return ManagementDateTimeConverter.ToDateTime(value.ToString()!).ToString("yyyy-MM-dd HH:mm"); } catch { }
        return value.ToString()!.Trim();
    }
}

