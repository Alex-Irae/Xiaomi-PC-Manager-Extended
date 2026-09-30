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
        cpu.TryRead(out float cpuPercent);
        gpu.TryRead(out float gpuPercent, out float gpuWatts, out float gpuMHz);
        MemoryLoad.TryRead(out float memoryPercent, out float usedGiB, out float totalGiB);
        battery.TryReadWatts(out float batteryWatts);
        float packageWatts = package.ReadWatts();
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

    private static float? Known(float value) => float.IsFinite(value) ? value : null;
    public void Dispose() { gpu.Dispose(); battery.Dispose(); package.Dispose(); temperature.Dispose(); }
}
