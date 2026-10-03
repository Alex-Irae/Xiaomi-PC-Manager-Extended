using System.Management;

namespace XiControl.SystemIntegration;

/// <summary>Снимок здоровья батареи; любое поле null/0 = прошивка не отдала.</summary>
public readonly record struct BatteryReport(int? HealthPercent, int? Cycles, double DesignWh, double FullWh);

/// <summary>
/// Здоровье батареи из штатных WMI-классов ACPI (root\wmi) — driver-free, только чтение.
/// То же, что показывает powercfg /batteryreport, но живьём: проектная и текущая макс. ёмкость
/// (их отношение = «здоровье»), число циклов заряда. Классы заполняет мини-драйвер батареи;
/// на части моделей часть полей пустая → отдаём null и мягко деградируем в UI.
/// </summary>
public static class BatteryInfo
{
    public static BatteryReport Read()
    {
        double design = ReadFirst("BatteryStaticData", "DesignedCapacity");     // mWh
        double full = ReadFirst("BatteryFullChargedCapacity", "FullChargedCapacity"); // mWh
        double cycles = ReadFirst("BatteryCycleCount", "CycleCount");
        // здоровье = текущая макс. / проектная; на свежей батарее бывает >100 (калибровка) — режем до 100
        int? health = design > 0 && full > 0 ? Math.Min(100, (int)Math.Round(100.0 * full / design)) : null;
        return new BatteryReport(health, cycles > 0 ? (int)cycles : null, design / 1000.0, full / 1000.0);
    }

    private static double ReadFirst(string cls, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", $"SELECT {prop} FROM {cls}");
            foreach (ManagementObject o in s.Get())
            {
                object? v = o[prop];
                o.Dispose();
                if (v != null) return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) { Log.Ex($"Battery.{cls}", ex); }
        return 0;
    }
}
