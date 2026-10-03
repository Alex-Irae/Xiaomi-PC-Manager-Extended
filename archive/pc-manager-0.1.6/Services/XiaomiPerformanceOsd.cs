using XiControl.Config;
using XiControl.Ui;
using XiControl.Wmi;

namespace XiaomiAIManager.Services;

// Original English OEM cards, hosted locally rather than invoking Xiaomi's running OSD process.
internal sealed class XiaomiPerformanceOsd : Form
{
    private sealed record Card(Image Image, int Number, string Group);
    private readonly Dictionary<PerfMode, Image> cards = new();
    private readonly Dictionary<string, Image> notifications = new();
    private readonly System.Windows.Forms.Timer hide = new();
    private readonly List<Card> visibleCards = new(2);
    private long lastFlashMs;
    internal int VisibleCardCount => visibleCards.Count;

    internal XiaomiPerformanceOsd()
    {
        FormBorderStyle = FormBorderStyle.None;
        Text = "Xiaomi performance notification";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        // A rounded native region avoids color-key artifacts around alpha-blended OEM artwork.
        // Xiaomi's rate artwork has a translucent black backdrop; this charcoal
        // underlay makes it match our opaque battery cards without a color key.
        BackColor = Color.FromArgb(45, 48, 53);
        Opacity = 0.94;
        hide.Tick += (_, _) => { hide.Stop(); Hide(); visibleCards.Clear(); };
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x80 | 0x20; // No activate, tool window, click through.
            return parameters;
        }
    }
    internal void Flash(PerfMode mode, bool online, int durationMs, OsdPosition position)
    {
        if (!ModeVisibility.IsAvailable(mode, online)) return;
        if (!cards.TryGetValue(mode, out var bitmap))
        {
            string file = mode switch
            {
                PerfMode.Quiet => "WorkloadSilent_En_Dark.png",
                PerfMode.Auto => "NewIntelligentMode_Dark.png",
                PerfMode.FullSpeed => "WorkloadDeception_En_Dark.png",
                PerfMode.Eco => "NewLongBatteryMode_Dark.png",
                PerfMode.Turbo => "WorkloadSpeed_En_Dark.png",
                _ => throw new ArgumentException("Unknown performance card.")
            };
            bitmap = LoadArtwork(file);
            cards.Add(mode, bitmap);
        }
        Display(bitmap, ModeVisibility.Available(online).ToList().IndexOf(mode) + 1, "performance", durationMs, position);
    }
    internal bool FlashNotification(OsdKind kind, int durationMs, OsdPosition position, string? value = null)
    {
        string? name = kind switch
        {
            OsdKind.CapsLockOn => "CapsLock", OsdKind.CapsLockOff => "CapsUnlock",
            OsdKind.NumLockOn => "NumLock", OsdKind.NumLockOff => "NumUnlock",
            OsdKind.FnLockOn => "FnLock", OsdKind.FnLockOff => "FnUnlock",
            OsdKind.WinKeyLockOn => "WinKeyDisabled", OsdKind.WinKeyLockOff => "WindowsKeyOn.svg",
            OsdKind.Travel => "TravelOn.svg", OsdKind.TravelOff => "TravelOff.svg",
            OsdKind.MicOn => "MuteOff", OsdKind.MicOff => "MuteOn",
            OsdKind.TouchpadOn => "TouchpadOn", OsdKind.TouchpadOff => "TouchpadOff",
            OsdKind.TouchscreenOn => "TouchScreenOn", OsdKind.TouchscreenOff => "TouchScreenOff",
            OsdKind.Charging when int.TryParse(value, out int level) => "Charging" + Math.Clamp(((level + 9) / 10) * 10, 10, 100),
            OsdKind.OnBattery when int.TryParse(value, out int level) => "OnBattery" + Math.Clamp(((level + 9) / 10) * 10, 10, 100),
            OsdKind.RefreshRate when int.TryParse(value, out int rate) => "DisFre" + rate,
            OsdKind.Backlight when int.TryParse(value, out int level) => level == 128 ? "KeyboardLightAuto" : "KeyboardLight" + Math.Clamp(level, 0, 10),
            _ => null
        };
        if (name is null) return false;
        string file = Path.Combine(AppContext.BaseDirectory, "www", "osd", name.EndsWith(".svg") ? name.Replace(".svg", "_Dark.svg") : name + "_Dark.png");
        if (kind == OsdKind.TouchscreenOn) file = Path.Combine(AppContext.BaseDirectory, "www", "osd", "TouchScreenOn_Dark.svg");
        if (!File.Exists(file) && AppAssets.OsdOverride(file) is null) return false;
        if (!notifications.TryGetValue(file, out var bitmap)) { bitmap = LoadArtwork(Path.GetFileName(file)); notifications[file] = bitmap; }
        string group = kind is OsdKind.Charging or OsdKind.OnBattery ? "power" : kind.ToString();
        if (group.EndsWith("On", StringComparison.Ordinal)) group = group[..^2];
        else if (group.EndsWith("Off", StringComparison.Ordinal)) group = group[..^3];
        Display(bitmap, 0, group, durationMs, position); return true;
    }
    private static Image LoadArtwork(string name)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "www", "osd", name);
        string? custom = AppAssets.OsdOverride(name);
        if (custom is not null)
        {
            try { return Decode(custom); }
            catch (Exception ex) { XiControl.Log.Ex("Custom OSD artwork", ex); }
        }
        return Decode(bundled);
    }
    private static Image Decode(string file)
    {
        if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return Svg.SvgDocument.Open<Svg.SvgDocument>(file).Draw();
        using var source = Image.FromFile(file);
        return new Bitmap(source); // Do not lock a replaceable asset while the resident is running.
    }
    private void Display(Image bitmap, int number, string group, int durationMs, OsdPosition position)
    {
        _ = Handle;
        long now = Environment.TickCount64;
        if (!Visible || now - lastFlashMs > 2200) visibleCards.Clear();
        int existing = visibleCards.FindIndex(item => item.Group == group);
        if (existing >= 0) visibleCards[existing] = new(bitmap, number, group);
        else { if (visibleCards.Count == 2) visibleCards.RemoveAt(0); visibleCards.Add(new(bitmap, number, group)); }
        lastFlashMs = now;
        int size = (int)Math.Round(160 * DeviceDpi / 96f);
        int gap = (int)Math.Round(12 * DeviceDpi / 96f);
        ClientSize = new Size(size * visibleCards.Count + gap * (visibleCards.Count - 1), size);
        var oldRegion = Region;
        Region? shape = null;
        for (int index = 0; index < visibleCards.Count; index++)
        {
            using var outline = Draw.Rounded(new Rectangle(index * (size + gap), 0, size, size), (int)Math.Round(16 * DeviceDpi / 96f));
            if (shape is null) shape = new Region(outline); else shape.Union(outline);
        }
        Region = shape;
        oldRegion?.Dispose();
        var area = OsdPlacement.TargetScreen().WorkingArea;
        Location = OsdPlacement.LocateOsd(area, Size, position);
        hide.Stop();
        hide.Interval = Math.Clamp(durationMs, 500, 10000);
        Invalidate();
        Show();
        hide.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int size = (int)Math.Round(160 * DeviceDpi / 96f);
        int gap = (int)Math.Round(12 * DeviceDpi / 96f);
        for (int index = 0; index < visibleCards.Count; index++)
        {
            var item = visibleCards[index];
            var bounds = new Rectangle(index * (size + gap), 0, size, size);
            e.Graphics.DrawImage(item.Image, bounds);
            if (!Program.TestMode || item.Number == 0) continue;
            // The badge distinguishes this app from the OEM overlay without changing its bitmap.
            var badge = new Rectangle(bounds.X + size * 3 / 4 - 4, 6, size / 4, size / 4);
            e.Graphics.FillEllipse(Brushes.RoyalBlue, badge);
            TextRenderer.DrawText(e.Graphics, item.Number.ToString(), ScaledFonts.Get(DeviceDpi, "Segoe UI Semibold", 20),
                badge, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { hide.Dispose(); foreach (var image in cards.Values) image.Dispose(); foreach (var image in notifications.Values) image.Dispose(); }
        base.Dispose(disposing);
    }
}
