// Purpose: export and restore portable user settings, profile pictures, and custom artwork.
// Dependencies: .NET ZIP/JSON and System.Drawing. Output: a user-selected PCManager-backup-*.zip.
// Command: app/PCManager.exe --manager, Settings > Export backup / Import backup.
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;

namespace XiaomiAIManager.Services;

internal static class SettingsBackup
{
    private const string Format = "PCManagerBackup";
    private const long MaxArchiveBytes = 40_000_000;
    private const long MaxTotalBytes = 60_000_000;
    internal sealed record Prepared(Dictionary<string, byte[]> Files);

    internal static void Export(string path, Preferences preferences)
    {
        preferences.Save();
        ExportFiles(path, Preferences.DataDirectory);
    }

    internal static void ExportFiles(string path, string root)
    {
        string target = Path.GetFullPath(path);
        if (target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save the backup outside PC Manager's app data so uninstall cannot remove it.");
        string pending = target + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var zip = ZipFile.Open(pending, ZipArchiveMode.Create))
            {
                var manifest = zip.CreateEntry("manifest.json");
                using (var writer = new StreamWriter(manifest.Open()))
                    writer.Write(JsonSerializer.Serialize(new { format = Format, version = 1 }));
                zip.CreateEntryFromFile(Path.Combine(root, "settings.json"), "settings.json");
                foreach (string folder in new[] { "icons", "assets" })
                {
                    string directory = Path.Combine(root, folder);
                    if (!Directory.Exists(directory)) continue;
                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        string name = Path.GetRelativePath(root, file).Replace('\\', '/');
                        if (Allowed(name) && new FileInfo(file).Length <= 10_000_000)
                            zip.CreateEntryFromFile(file, name);
                    }
                }
            }
            if (new FileInfo(pending).Length > MaxArchiveBytes) throw new InvalidOperationException("The backup exceeds 40 MB.");
            File.Move(pending, target, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    internal static Prepared Prepare(string path)
    {
        if (new FileInfo(path).Length > MaxArchiveBytes) throw new ArgumentException("Choose a backup smaller than 40 MB.");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is < 2 or > 250) throw new ArgumentException("This is not a supported PC Manager backup.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!Allowed(name) || files.ContainsKey(name) || entry.Length > 10_000_000 || (total += entry.Length) > MaxTotalBytes)
                throw new ArgumentException("The backup contains an unsupported or oversized file.");
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            if (bytes.Length != entry.Length) throw new InvalidDataException("A backup file is incomplete.");
            files.Add(name, bytes.ToArray());
        }
        if (!files.TryGetValue("manifest.json", out var manifest) || !files.TryGetValue("settings.json", out var settings))
            throw new ArgumentException("The backup is missing its manifest or settings.");
        using var document = JsonDocument.Parse(manifest);
        if (document.RootElement.GetProperty("format").GetString() != Format || document.RootElement.GetProperty("version").GetInt32() != 1)
            throw new ArgumentException("This PC Manager backup version is not supported.");
        using var settingsDocument = JsonDocument.Parse(settings);
        if (settingsDocument.RootElement.ValueKind != JsonValueKind.Object || settingsDocument.RootElement.GetProperty("Appearance").ValueKind != JsonValueKind.String)
            throw new ArgumentException("The backup settings are invalid.");
        foreach (var (name, bytes) in files)
        {
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            using var image = Image.FromStream(new MemoryStream(bytes));
            if (image.Width is < 1 or > 2048 || image.Height is < 1 or > 2048)
                throw new ArgumentException("A backup image exceeds 2048 pixels per side.");
        }
        return new(files);
    }

    internal static void Restore(Prepared backup, string? dataDirectory = null)
    {
        string root = dataDirectory ?? Preferences.DataDirectory;
        Directory.CreateDirectory(root);
        foreach (var (name, bytes) in backup.Files.Where(item => item.Key is not ("settings.json" or "manifest.json")))
        {
            string target = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
        string pending = Path.Combine(root, "settings.json.pending");
        File.WriteAllBytes(pending, backup.Files["settings.json"]);
        File.Move(pending, Path.Combine(root, "settings.json"), overwrite: true);
    }

    internal static void Check()
    {
        string test = Path.Combine(Path.GetTempPath(), "PCManager-backup-check", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(test, "source"), restored = Path.Combine(test, "restored"), archive = Path.Combine(test, "settings.zip");
        Directory.CreateDirectory(Path.Combine(source, "icons"));
        File.WriteAllText(Path.Combine(source, "settings.json"), JsonSerializer.Serialize(new Preferences()));
        using (var icon = new Bitmap(16, 16)) icon.Save(Path.Combine(source, "icons", "profile-0123456789abcdef0123456789abcdef.png"), ImageFormat.Png);
        ExportFiles(archive, source);
        var backup = Prepare(archive);
        Restore(backup, restored);
        if (!File.ReadAllBytes(Path.Combine(source, "settings.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(restored, "settings.json")))
            || !File.Exists(Path.Combine(restored, "icons", "profile-0123456789abcdef0123456789abcdef.png")))
            throw new InvalidDataException("Backup round trip failed.");
        string invalid = Path.Combine(test, "invalid.zip");
        using (var zip = ZipFile.Open(invalid, ZipArchiveMode.Create)) { zip.CreateEntry("../escape.txt"); zip.CreateEntry("settings.json"); }
        try { Prepare(invalid); throw new InvalidDataException("Unsafe archive path was accepted."); }
        catch (ArgumentException) { }
    }

    private static bool Allowed(string name)
    {
        if (name is "manifest.json" or "settings.json") return true;
        string[] parts = name.Split('/');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))) return false;
        return parts[0] switch
        {
            "icons" => parts.Length == 2 && parts[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase),
            "assets" => parts.Length <= 3 && (parts[^1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || parts.Length == 2 && parts[1] is "app.ico" or "custom.css"),
            _ => false
        };
    }
}

