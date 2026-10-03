using System.Drawing.Drawing2D;

namespace XiControl.Ui;

/// <summary>Палитра тёмного меню (под OLED — глубокий тёмный, мягкие акценты).</summary>
public static class DarkPalette
{
    public static readonly Color Bg = Color.FromArgb(32, 32, 32);      // фон меню
    public static readonly Color Gutter = Color.FromArgb(40, 40, 40);  // левый жёлоб
    public static readonly Color Hover = Color.FromArgb(58, 58, 58);   // выделение
    public static readonly Color Border = Color.FromArgb(64, 64, 64);  // рамка/разделитель
    public static readonly Color Text = Color.FromArgb(240, 240, 240);
    public static readonly Color TextDim = Color.FromArgb(130, 130, 130);
    public static readonly Color Accent = Color.FromArgb(120, 200, 255); // галочка
}

public static class Theme
{
    /// <summary>true, если в Windows включена тёмная тема приложений.</summary>
    public static bool IsDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* нет ключа → считаем светлой */ }
        return false;
    }

    /// <summary>true, если панель задач светлая (для выбора цвета значка трея).</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("SystemUsesLightTheme") is int v) return v != 0;
        }
        catch { /* нет ключа → считаем тёмной */ }
        return false;
    }
}

file sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => DarkPalette.Bg;
    public override Color ImageMarginGradientBegin => DarkPalette.Gutter;
    public override Color ImageMarginGradientMiddle => DarkPalette.Gutter;
    public override Color ImageMarginGradientEnd => DarkPalette.Gutter;
    public override Color MenuItemSelected => DarkPalette.Hover;
    public override Color MenuItemSelectedGradientBegin => DarkPalette.Hover;
    public override Color MenuItemSelectedGradientEnd => DarkPalette.Hover;
    public override Color MenuItemBorder => DarkPalette.Hover;
    public override Color MenuBorder => DarkPalette.Border;
    public override Color SeparatorDark => DarkPalette.Border;
    public override Color SeparatorLight => DarkPalette.Border;
    public override Color CheckBackground => DarkPalette.Hover;
    public override Color CheckSelectedBackground => DarkPalette.Hover;
    public override Color CheckPressedBackground => DarkPalette.Hover;
}

/// <summary>Тёмный рендерер меню: тёмный фон, светлый текст, аккуратная галочка/стрелка.</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? DarkPalette.Text : DarkPalette.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = DarkPalette.Text;
        base.OnRenderArrow(e);
    }

    // Своя светлая галочка вместо тёмной системной
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        var r = e.ImageRectangle;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(DarkPalette.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var p1 = new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f);
        var p2 = new PointF(r.X + r.Width * 0.44f, r.Y + r.Height * 0.72f);
        var p3 = new PointF(r.X + r.Width * 0.78f, r.Y + r.Height * 0.28f);
        g.DrawLines(pen, [p1, p2, p3]);
    }
}
