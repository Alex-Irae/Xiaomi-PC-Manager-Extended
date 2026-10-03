// Purpose: verify the resident's actual brightness and charging handlers, restoring saved charging intent.
// Dependencies: initialized HardwareService, connected charger, configured care limit, administrator access.
// Outputs: fresh results/NNN_timestamp_hardware/{config.json,summary.json}; never replaces earlier evidence.
// Command: app/PCManager.exe --verify-hardware (full/travel charging tested briefly; no mode changes).
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using XiControl.SystemIntegration;
using PowerStatus = XiControl.SystemIntegration.PowerStatus;
using PowerLineStatus = XiControl.SystemIntegration.PowerLineStatus;

namespace XiaomiAIManager.Services;

internal static class HardwareDiagnostics
{
    internal static void Run(HardwareService hardware, Preferences preferences)
    {
        string root = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "results");
        Directory.CreateDirectory(root);
        int next = Directory.EnumerateDirectories(root).Select(path => int.TryParse(Path.GetFileName(path).Split('_')[0], out int number) ? number : 0).DefaultIfEmpty().Max() + 1;
        string run = Path.Combine(root, $"{next:D3}_{DateTime.Now:yyyyMMddTHHmmssfff}_hardware");
        Directory.CreateDirectory(run);
        var rows = new List<object>();
        int? before = null;
        int restore = preferences.TravelReturnLimit is not null ? 100 : preferences.ChargeLimit ?? 100;
        bool touched = false;
        string? error = null;
        string? restoreError = null;
        void Save(string file, object value) => File.WriteAllText(Path.Combine(run, file), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        using var oemService = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MiDeviceService");
        Save("config.json", new { seed = (int?)null, source = PowerStatus.Read().LineStatus.ToString(), restore, chargeCare = preferences.ChargeLimit, travel = preferences.TravelReturnLimit,
            oemServiceStartup = oemService?.GetValue("Start"), isolationRequested = OemServiceControl.IsolationRequested,
            protocol = "Same-value immediate brightness write, followed by full/travel charge readback after two seconds, with charging intent restored in finally.",
            limitations = "Readback confirms firmware codes, not the physical charging cutoff or optical brightness transition. No unplug/resume coverage.", executable = Environment.ProcessPath, settleMilliseconds = 2000 });
        try
        {
            rows.Add(new { action = "hardware-read", result = hardware.Read() });
            if (Brightness.Get() is int brightness)
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    object result = hardware.SetBrightness(brightness);
                    rows.Add(new { action = "brightness-same-value", elapsedMs = watch.Elapsed.TotalMilliseconds, result });
                }
                catch (Exception ex) { rows.Add(new { action = "brightness-same-value", error = ex.Message }); }
            }
            before = hardware.SharedFirmware.GetChargeLimit();
            if (PowerStatus.Read().LineStatus != PowerLineStatus.Online || preferences.TravelReturnLimit is not null || restore is not (40 or 50 or 60 or 70 or 80))
                throw new InvalidOperationException("Charging probe skipped: connect AC and configure a care limit, with travel off.");
            touched = true;
            hardware.SetChargeLimit(100);
            Thread.Sleep(2000);
            int? full = hardware.SharedFirmware.GetChargeLimit();
            rows.Add(new { action = "full-charge", requested = 100, actual = full, confirmed = full == 100 });
            hardware.SetChargeLimit(restore);
            hardware.SetTravel(true);
            Thread.Sleep(2000);
            int? travel = hardware.SharedFirmware.GetChargeLimit();
            rows.Add(new { action = "travel-charge", requested = 100, actual = travel, confirmed = travel == 100, recovery = preferences.TravelReturnLimit });
            hardware.SetTravel(false);
            rows.Add(new { action = "travel-cancel", requested = restore, actual = hardware.SharedFirmware.GetChargeLimit() });
        }
        catch (Exception ex) { error = ex.Message; XiControl.Log.Ex("Diagnostics", ex); }
        finally
        {
            if (touched)
                try { hardware.SetChargeLimit(restore); }
                catch (Exception ex) { restoreError = ex.Message; XiControl.Log.Ex("Diagnostics.Restore", ex); }
            Save("summary.json", new { time = DateTimeOffset.Now, before, restore, actual = hardware.SharedFirmware.GetChargeLimit(), error, restoreError, rows });
            XiControl.Log.Write("Hardware diagnostics saved: " + run);
        }
    }
}

