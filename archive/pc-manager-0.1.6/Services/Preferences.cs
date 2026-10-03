using System.Text.Json;
using System.Text.Json.Serialization;
using XiControl.Config;

namespace XiaomiAIManager.Services;

public sealed class Preferences
{
    public static string DataDirectory => Program.TestMode ? Path.Combine(AppContext.BaseDirectory, "test-data") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaomiAIManager");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string Appearance { get; set; } = "light";
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public bool PreventSleep { get; set; }
    public string ThemePreset { get; set; } = "blue";
    public string ThemeAccent { get; set; } = "#3482ff";
    public string ThemeBackground { get; set; } = "#1f242c";
    public string ThemeSurface { get; set; } = "#2b333f";
    public List<ThemePalette> ThemePalettes { get; set; } = [];
    public bool DeveloperMode { get; set; }
    public bool OriginalPopupEnabled { get; set; }
    public string OsdStyle { get; set; } = "xiaomi";
    public string? ProfileImage { get; set; }
    public int PopupScale { get; set; } = 85;
    public bool CompactPanel { get; set; } = true;
    public bool PopupAnimations { get; set; } = true;
    public int BatteryCareLimit { get; set; } = 80;
    public List<KeyboardShortcut> Shortcuts { get; set; } = [];
    public KeyboardShortcut CopilotShortcut { get; set; } = new() { Chord = "Win+Shift+F23", Action = "none" };
    public string? XiaomiExecutable { get; set; }
    public string? MiAppStoreExecutable { get; set; }
    public string? XiaoAiExecutable { get; set; }
    public int? ChargeLimit { get; set; }
    public int? TravelReturnLimit { get; set; }
    public bool AutoRefresh { get; set; }
    public string? LastRefreshPowerSource { get; set; }
    public int AcRefreshRate { get; set; } = 120;
    public int BatteryRefreshRate { get; set; } = 60;
    public string? AcMode { get; set; }
    public string? BatteryMode { get; set; }
    public bool PowerModeMigrationDone { get; set; }
    public int ModeLayoutVersion { get; set; }
    public int ResidentSetupVersion { get; set; }
    public global::XiControl.Ui.OsdPosition PopupPosition { get; set; } = global::XiControl.Ui.OsdPosition.BottomRight;
    public int PopupOffsetX { get; set; }
    public int PopupOffsetY { get; set; }
    public List<QuickLink> QuickLinks { get; set; } = [];
    public List<string> QuickSystemActions { get; set; } = ["screenoff", "awake", "refresh", "autorefresh", "touchpad", "touchscreen", "monitor", "sleepoff"];
    public Dictionary<string, QuickLink> KeyAppLinks { get; set; } = [];
    public Dictionary<string, string> QuickIcons { get; set; } = [];
    public string? TouchpadDeviceId { get; set; }
    public string? TouchscreenDeviceId { get; set; }
    public bool TouchpadPersistOff { get; set; }
    public bool TouchscreenPersistOff { get; set; }
    public AppConfig? XiControl { get; set; }
    private readonly object saveSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Preferences Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath), JsonOptions) ?? new();
            if (settings.Appearance is not ("light" or "dark" or "system")) settings.Appearance = "light";
            if (settings.ChargeLimit is not (null or 40 or 50 or 60 or 70 or 80 or 100)) settings.ChargeLimit = null;
            if (settings.TravelReturnLimit is not (null or 40 or 50 or 60 or 70 or 80)) settings.TravelReturnLimit = null;
            if (settings.AcRefreshRate is < 20 or > 360) settings.AcRefreshRate = 120;
            if (settings.BatteryRefreshRate is < 20 or > 360) settings.BatteryRefreshRate = 60;
            if (!Enum.IsDefined(settings.PopupPosition)) settings.PopupPosition = global::XiControl.Ui.OsdPosition.BottomRight;
            settings.PopupOffsetX = Math.Clamp(settings.PopupOffsetX, -2000, 2000);
            settings.PopupOffsetY = Math.Clamp(settings.PopupOffsetY, -2000, 2000);
            settings.QuickLinks ??= [];
            settings.QuickSystemActions ??= [];
            // Existing untouched seven-button layouts gain the new eighth control; customized layouts stay intact.
            if (settings.QuickSystemActions.SequenceEqual(new[] { "screenoff", "awake", "refresh", "autorefresh", "touchpad", "touchscreen", "monitor" }))
                settings.QuickSystemActions.Add("sleepoff");
            settings.QuickSystemActions = settings.QuickSystemActions.Where(AppLinks.SystemActionIds.Contains).Distinct().Take(8).ToList();
            int visibleLinks = 0;
            foreach (var link in settings.QuickLinks) if (link.ShowInPanel && ++visibleLinks > 4) link.ShowInPanel = false;
            settings.KeyAppLinks ??= [];
            settings.QuickIcons ??= [];
            settings.Shortcuts ??= [];
            settings.CopilotShortcut ??= new() { Chord = "Win+Shift+F23", Action = "none" };
            settings.PopupScale = Math.Clamp(settings.PopupScale, 75, 110);
            if (settings.BatteryCareLimit is not (40 or 50 or 60 or 70 or 80)) settings.BatteryCareLimit = 80;
            if (settings.OsdStyle is not ("xiaomi" or "xicontrol")) settings.OsdStyle = "xiaomi";
            settings.ThemePalettes ??= [];
            settings.ThemePalettes = settings.ThemePalettes.Where(p => p is not null && p.Id is not null
                && System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^saved:[a-f0-9]{32}$")
                && p.Name is { Length: > 0 and <= 40 } && AppearanceOptions.ValidColor(p.Accent)
                && AppearanceOptions.ValidColor(p.Background) && AppearanceOptions.ValidColor(p.Surface))
                .DistinctBy(p => p.Id).Take(20).ToList();
            if (!AppearanceOptions.ValidPreset(settings, settings.ThemePreset)) settings.ThemePreset = "blue";
            if (!AppearanceOptions.ValidColor(settings.ThemeAccent)) settings.ThemeAccent = "#3482ff";
            if (!AppearanceOptions.ValidColor(settings.ThemeBackground)) settings.ThemeBackground = "#1f242c";
            if (!AppearanceOptions.ValidColor(settings.ThemeSurface)) settings.ThemeSurface = "#2b333f";
            if (settings.ProfileImage is not null && !System.Text.RegularExpressions.Regex.IsMatch(settings.ProfileImage, "^profile-[a-f0-9]{32}\\.png$")) settings.ProfileImage = null;
            return settings;
        }
        catch (Exception ex)
        {
            global::XiControl.Log.Ex("Preferences.Load", ex);
            // Preserve the unreadable settings before a later intentional save.
            File.Copy(SettingsPath, SettingsPath + ".unreadable-" + DateTime.Now.ToString("yyyyMMddTHHmmssfff"));
            return new();
        }
    }

    public void Save()
    {
        lock (saveSync)
        {
        Directory.CreateDirectory(DataDirectory);
        string pending = SettingsPath + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(pending, SettingsPath, overwrite: true);
        }
    }

    public string SaveSnapshot()
    {
        lock (saveSync)
        {
            Save();
            string folder = Path.Combine(DataDirectory, "settings-snapshots");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"settings-{DateTime.Now:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
            File.Copy(SettingsPath, path);
            return path;
        }
    }
}

public sealed class KeyboardShortcut
{
    public string Chord { get; set; } = "";
    public string Action { get; set; } = "panel";
    public string? AppId { get; set; }
}

public sealed class ThemePalette
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Accent { get; set; } = "#3482ff";
    public string Background { get; set; } = "#171a1f";
    public string Surface { get; set; } = "#1e2228";
}

public sealed class QuickLink
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public string Icon { get; set; } = "tools";
    public bool ShowInPanel { get; set; } = true;
}
