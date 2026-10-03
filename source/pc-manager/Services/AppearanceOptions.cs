// Purpose: validated theme and profile preferences shared by both windows.
// Dependencies: resident preferences and System.Drawing. Output: settings and a decoded PNG copy.
// Command: app/PCManager.exe --manager, Settings > Appearance.
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XiaomiAIManager.Services;

internal static class AppearanceOptions
{
    internal static bool ValidPreset(Preferences p, string value) => value is "blue" or "red" or "pink" or "green" or "custom"
        || p.ThemePalettes.Any(palette => palette.Id == value);
    internal static bool ValidColor(string? value) => value is not null && Regex.IsMatch(value, "^#[a-fA-F0-9]{6}$");
    internal static void Save(Preferences p, JsonElement args)
    {
        string preset = args.GetProperty("preset").GetString() ?? "";
        string accent = args.GetProperty("accent").GetString() ?? "";
        string background = args.GetProperty("background").GetString() ?? "";
        string surface = args.GetProperty("surface").GetString() ?? "";
        string style = args.GetProperty("osdStyle").GetString() ?? "";
        if (!ValidPreset(p, preset) || !ValidColor(accent) || !ValidColor(background) || !ValidColor(surface) || style is not ("xiaomi" or "xicontrol"))
            throw new ArgumentException("Choose a listed style and six-digit hex colors.");
        if (p.ThemePalettes.FirstOrDefault(palette => palette.Id == preset) is { } saved
            && (saved.Accent != accent || saved.Background != background || saved.Surface != surface))
            throw new ArgumentException("Edit a color as Custom before saving a named palette.");
        p.ThemePreset = preset; p.ThemeAccent = accent; p.ThemeBackground = background; p.ThemeSurface = surface;
        p.OsdStyle = style; p.DeveloperMode = args.GetProperty("developerMode").GetBoolean();
        if (args.TryGetProperty("originalPopup", out var popup)) p.OriginalPopupEnabled = popup.GetBoolean();
        p.Save();
    }
    internal static object SavePalette(Preferences p, JsonElement args)
    {
        string name = (args.GetProperty("name").GetString() ?? "").Trim();
        ValidatePaletteName(p, name);
        if (p.ThemePreset != "custom") throw new InvalidOperationException("Edit a color before saving a new palette.");
        if (p.ThemePalettes.Count >= 20) throw new InvalidOperationException("The 20-palette limit is reached.");
        if (!ValidColor(p.ThemeAccent) || !ValidColor(p.ThemeBackground) || !ValidColor(p.ThemeSurface))
            throw new ArgumentException("The selected colors are invalid.");
        var palette = new ThemePalette { Id = "saved:" + Guid.NewGuid().ToString("N"), Name = name,
            Accent = p.ThemeAccent, Background = p.ThemeBackground, Surface = p.ThemeSurface };
        p.ThemePalettes.Add(palette);
        p.ThemePreset = palette.Id;
        p.Save();
        return new { palette.Id, message = "Palette saved." };
    }
    internal static object RenamePalette(Preferences p, JsonElement args)
    {
        string id = args.GetProperty("id").GetString() ?? "";
        string name = (args.GetProperty("name").GetString() ?? "").Trim();
        RenamePaletteInMemory(p, id, name);
        p.Save();
        return new { message = "Palette renamed." };
    }
    internal static object DeletePalette(Preferences p, JsonElement args)
    {
        string id = args.GetProperty("id").GetString() ?? "";
        DeletePaletteInMemory(p, id);
        p.Save();
        return new { message = "Palette deleted. Its colors remain as Custom." };
    }
    internal static void RenamePaletteInMemory(Preferences p, string id, string name)
    {
        var palette = p.ThemePalettes.FirstOrDefault(item => item.Id == id)
            ?? throw new ArgumentException("Select a saved palette to rename.");
        ValidatePaletteName(p, name, id);
        palette.Name = name;
    }
    internal static void DeletePaletteInMemory(Preferences p, string id)
    {
        var palette = p.ThemePalettes.FirstOrDefault(item => item.Id == id)
            ?? throw new ArgumentException("Select a saved palette to delete.");
        if (p.ThemePreset == id)
        {
            // Keep the displayed colors, but stop referring to a deleted palette.
            p.ThemePreset = "custom";
            p.ThemeAccent = palette.Accent;
            p.ThemeBackground = palette.Background;
            p.ThemeSurface = palette.Surface;
        }
        p.ThemePalettes.Remove(palette);
    }
    private static void ValidatePaletteName(Preferences p, string name, string? currentId = null)
    {
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a palette name of 1 to 40 characters.");
        if (name.Equals("Custom", StringComparison.OrdinalIgnoreCase)
            || new[] { "Blue", "Red", "Pink", "Green" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a name different from the built-in presets.");
        if (p.ThemePalettes.Any(palette => palette.Id != currentId && string.Equals(palette.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A palette with that name already exists.");
    }
    internal static void PickProfile(Preferences p, IWin32Window owner)
    {
        using var picker = new OpenFileDialog { Title = "Choose your profile picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (picker.ShowDialog(owner) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > 10_000_000) throw new ArgumentException("Choose a picture smaller than 10 MB.");
        using var source = Image.FromFile(picker.FileName);
        if (source.Width > 8192 || source.Height > 8192) throw new ArgumentException("Choose a picture no larger than 8192 pixels per side.");
        // Decode and resize before publishing; the WebView never receives arbitrary file paths.
        using var bitmap = new Bitmap(256, 256);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            int side = Math.Min(source.Width, source.Height);
            g.DrawImage(source, new Rectangle(0, 0, 256, 256), new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        }
        Directory.CreateDirectory(AppLinks.IconDirectory);
        string name = "profile-" + Guid.NewGuid().ToString("N") + ".png";
        bitmap.Save(Path.Combine(AppLinks.IconDirectory, name), ImageFormat.Png);
        p.ProfileImage = name; p.Save();
    }
}
