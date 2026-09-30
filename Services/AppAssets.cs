// Purpose: load bundled artwork with optional per-user asset-pack overrides.
// Dependencies: System.Drawing/Windows; output: validated copies under local app data/assets.
// Command: app/XiaomiAIManager.exe --manager, Settings > Import asset pack.
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace XiaomiAIManager.Services;

internal static class AppAssets
{
    internal static string CustomDirectory => Path.Combine(Preferences.DataDirectory, "assets");
    private static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "assets");
    private static string BundledOsdDirectory => Path.Combine(AppContext.BaseDirectory, "www", "osd");
    private static readonly Lazy<Icon> Cached = new(() =>
    {
        try { return new Icon(Path.Combine(CustomDirectory, "app.ico")); }
        catch { /* No user icon: use the bundled one. */ }
        try { return new Icon(Path.Combine(BundledDirectory, "app.ico")); }
        catch (Exception ex) { XiControl.Log.Ex("App icon", ex); return (Icon)SystemIcons.Application.Clone(); }
    });
    internal static Icon Icon => Cached.Value;

    internal static object Manifest()
    {
        try
        {
            string iconDirectory = Path.Combine(CustomDirectory, "icons");
            string[] icons = Directory.Exists(iconDirectory)
                ? Directory.EnumerateFiles(iconDirectory, "*.png").Where(file => ValidIconName(Path.GetFileName(file)))
                    .Select(file => Path.GetFileNameWithoutExtension(file)!).ToArray()
                : [];
            var files = Directory.Exists(CustomDirectory) ? Directory.EnumerateFiles(CustomDirectory, "*", SearchOption.TopDirectoryOnly) : [];
            if (Directory.Exists(iconDirectory)) files = files.Concat(Directory.EnumerateFiles(iconDirectory, "*.png"));
            long revision = files.Select(file => File.GetLastWriteTimeUtc(file).Ticks / 10_000).DefaultIfEmpty(0).Max();
            return new { icons, logo = File.Exists(Path.Combine(CustomDirectory, "app.png")),
                css = File.Exists(Path.Combine(CustomDirectory, "custom.css")), revision };
        }
        catch (Exception ex)
        {
            XiControl.Log.Ex("Custom asset manifest", ex);
            return new { icons = Array.Empty<string>(), logo = false, css = false, revision = 0L };
        }
    }

    internal static string? OsdOverride(string bundledFile)
    {
        string name = Path.GetFileNameWithoutExtension(bundledFile) + ".png";
        string candidate = Path.Combine(CustomDirectory, "osd", name);
        return File.Exists(candidate) ? candidate : null;
    }

    internal static string ImportPack(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing asset-pack folder.");
        var selected = new List<(string source, string target)>();
        foreach (string name in new[] { "app.ico", "app.png", "custom.css" })
        {
            string source = Path.Combine(folder, name);
            if (File.Exists(source)) selected.Add((source, Path.Combine(CustomDirectory, name)));
        }
        string iconSource = Path.Combine(folder, "icons");
        if (Directory.Exists(iconSource))
            foreach (string source in Directory.EnumerateFiles(iconSource, "*.png"))
                if (ValidIconName(Path.GetFileName(source))) selected.Add((source, Path.Combine(CustomDirectory, "icons", Path.GetFileName(source))));
        string osdSource = Path.Combine(folder, "osd");
        if (Directory.Exists(osdSource))
            foreach (string source in Directory.EnumerateFiles(osdSource, "*.png"))
                if (ValidOsdName(Path.GetFileName(source))) selected.Add((source, Path.Combine(CustomDirectory, "osd", Path.GetFileName(source))));
        if (selected.Count == 0) throw new ArgumentException("No supported assets found. Use app.png, app.ico, custom.css, icons/*.png or osd/*.png.");
        if (selected.Count > 200) throw new ArgumentException("An asset pack may contain at most 200 supported files.");
        foreach (var (source, _) in selected) Validate(source);
        foreach (var (source, target) in selected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, overwrite: true);
        }
        return $"Imported {selected.Count} asset{(selected.Count == 1 ? "" : "s")}. Restart PC Manager for tray and OSD changes.";
    }

    internal static string PickPack(IWin32Window owner)
    {
        using var picker = new FolderBrowserDialog { Description = "Choose an asset-pack folder" };
        if (picker.ShowDialog(owner) != DialogResult.OK) return "Asset-pack import cancelled.";
        return ImportPack(picker.SelectedPath);
    }

    private static bool ValidIconName(string name) => Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,31}\\.png$", RegexOptions.CultureInvariant);
    private static bool ValidOsdName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        return Regex.IsMatch(name, "^[A-Za-z0-9_]+\\.png$", RegexOptions.CultureInvariant)
            && (File.Exists(Path.Combine(BundledOsdDirectory, stem + ".png"))
                || File.Exists(Path.Combine(BundledOsdDirectory, stem + ".svg")));
    }
    private static void Validate(string file)
    {
        long size = new FileInfo(file).Length;
        if (size is < 1 or > 5_000_000) throw new ArgumentException($"{Path.GetFileName(file)} must be between 1 byte and 5 MB.");
        if (file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var icon = new Icon(file);
            if (icon.Width > 512 || icon.Height > 512) throw new ArgumentException("The app icon is too large.");
        }
        else if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var image = Image.FromFile(file);
            if (image.RawFormat.Guid != ImageFormat.Png.Guid || image.Width is < 1 or > 2048 || image.Height is < 1 or > 2048)
                throw new ArgumentException($"{Path.GetFileName(file)} must be a PNG no larger than 2048 pixels per side.");
        }
        else if (file.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && size > 128_000)
            throw new ArgumentException("Custom CSS must be smaller than 128 KB.");
    }
}
