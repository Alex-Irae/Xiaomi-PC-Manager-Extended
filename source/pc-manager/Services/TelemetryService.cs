using XiControl.SystemIntegration;

namespace XiaomiAIManager.Services;

public sealed class TelemetryService : IDisposable
{
    private readonly CpuLoad cpu = new();
    private readonly GpuTelemetry gpu = new();
    private readonly PowerDraw battery = new();
    private readonly EnergyMeterPower package = new();
    private readonly TemperatureSource temperature = new();

    // Sampling is serialized by CapabilityRouter. Counter-based readings need two samples.
    public object ReadBattery()
    {
        battery.TryReadWatts(out float watts);
        return new { batteryWatts = Known(watts), batteryPowerState = battery.LastPowerState };
    }
    public object Read()
    {
        // One full read at a time: the warm-up after the start and a window opened meanwhile share the counters.
        lock (cpu) return ReadAll();
    }
    private object ReadAll()
    {
        cpu.TryRead(out float cpuPercent);
        gpu.TryRead(out float gpuPercent, out float gpuWatts, out float gpuMHz);
        MemoryLoad.TryRead(out float memoryPercent, out float usedGiB, out float totalGiB);
        battery.TryReadWatts(out float batteryWatts);
        float packageWatts = LatestPackageWatts();
        float degrees = temperature.ReadMaxC();
        return new
        {
            timestamp = DateTimeOffset.Now,
            cpuPercent = Known(cpuPercent), gpuPercent = Known(gpuPercent),
            gpuWatts = Known(gpuWatts), gpuMHz = Known(gpuMHz),
            memoryPercent = Known(memoryPercent), memoryUsedGiB = Known(usedGiB), memoryTotalGiB = Known(totalGiB),
            batteryWatts = Known(batteryWatts),
            batteryPowerState = battery.LastPowerState, batteryRateNative = battery.LastRate,
            // RAPL's source convention is negative consumption; show package draw positively.
            cpuPackageWatts = Known(-packageWatts),
            temperatureC = Known(degrees), temperatureSource = temperature.Source.ToString(),
            npuPercent = (float?)null, fanRpm = (float?)null
        };
    }

    // Processor package power comes from Windows' performance provider, and that one query is slow: 265 ms each
    // time, and 5.5 s when nothing has asked for about ten minutes (measured). Every full read waited for it, so
    // the big window got its live values that much later. The query now runs beside the read: a read returns the
    // value of the previous query, at most one refresh old while the window is open, and starts the next one.
    private float latestPackageWatts = float.NaN;
    private int packageQuery;
    private float LatestPackageWatts()
    {
        if (Interlocked.CompareExchange(ref packageQuery, 1, 0) == 0)
            Task.Run(() => { try { latestPackageWatts = package.ReadWatts(); } finally { Volatile.Write(ref packageQuery, 0); } });
        return latestPackageWatts;
    }

    private static float? Known(float value) => float.IsFinite(value) ? value : null;
    public void Dispose() { gpu.Dispose(); battery.Dispose(); package.Dispose(); temperature.Dispose(); }
}
