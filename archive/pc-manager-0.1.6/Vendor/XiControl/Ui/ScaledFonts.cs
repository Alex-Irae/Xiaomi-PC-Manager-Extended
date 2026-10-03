namespace XiControl.Ui;

/// <summary>
/// Шрифты, масштабируемые вручную вместе с остальной геометрией (Sc от DeviceDpi).
/// Размер задаётся в пунктах, но хэндл создаётся в пикселях под конкретный DPI:
/// GDI-шрифт в пунктах система при смене DPI/масштаба сама не пересоздаёт, поэтому
/// после смены разрешения текст оставался в старом масштабе и расходился с иконками
/// и отступами до перезапуска. Кэш живёт всё время работы (DPI-значений единицы).
/// </summary>
public static class ScaledFonts
{
    private static readonly Dictionary<(string family, float pt, int dpi), Font> Cache = [];

    /// <summary>Шрифт «family, pt пунктов» для данного DPI (пиксельный, из кэша).</summary>
    public static Font Get(int dpi, string family, float pt)
    {
        var key = (family, pt, dpi);
        if (!Cache.TryGetValue(key, out var f))
            Cache[key] = f = new Font(family, MathF.Max(1f, pt * dpi / 72f), GraphicsUnit.Pixel);
        return f;
    }
}
