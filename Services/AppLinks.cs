// Purpose: configure popup placement/icons and user-selected app links without an elevated launch fallback.
// Dependencies: WinForms file picker, Windows Explorer automation; no additional packages.
// Outputs: root preferences and PNG icon copies in the app's data/icons directory. Removed links keep their icon files.
// Command: app/PCManager.exe --manager, Settings > Quick panel customization.
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using XiControl.Ui;

namespace XiaomiAIManager.Services;

internal static class AppLinks
{
    internal static readonly string[] Icons = ["settings", "message", "screen", "screenoff", "device", "quiet", "auto", "speed", "travel", "touchpad", "touch", "refresh", "reload", "awake", "calculator", "notepad", "screenshot", "clipboard", "tools", "battery", "cpu", "search", "translate", "update", "home"];
    internal static readonly string[] SystemActionIds = ["screenoff", "awake", "refresh", "autorefresh", "touchpad", "touchscreen", "monitor", "sleepoff", "calculator", "notepad", "screenshot", "clipboard", "projection"];
    internal static string IconDirectory => Path.Combine(Preferences.DataDirectory, "icons");
    internal static object Read(Preferences p) => new { p.PopupPosition, p.PopupOffsetX, p.PopupOffsetY, p.PopupScale, p.CompactPanel, p.PopupAnimations, p.QuickLinks, p.QuickSystemActions, p.QuickIcons, p.Shortcuts, icons = Icons };
    internal static void Save(Preferences p, JsonElement args)
    {
        string rawPosition = args.GetProperty("position").GetString() ?? "";
        if (!Enum.GetNames<OsdPosition>().Contains(rawPosition) || !Enum.TryParse<OsdPosition>(rawPosition, out var position)) throw new ArgumentException("Choose a listed panel position.");
        int x = args.GetProperty("x").GetInt32(), y = args.GetProperty("y").GetInt32();
        if (Math.Abs((long)x) > 2000 || Math.Abs((long)y) > 2000) throw new ArgumentException("Panel offsets must be between -2000 and 2000 logical pixels.");
        int scale = args.TryGetProperty("scale", out var rawScale) ? rawScale.GetInt32() : p.PopupScale;
        bool compact = args.TryGetProperty("compact", out var rawCompact) ? rawCompact.GetBoolean() : p.CompactPanel;
        if (scale is < 75 or > 110) throw new ArgumentException("Panel scale must be between 75% and 110%.");
        var icons = args.GetProperty("icons").Deserialize<Dictionary<string, string>>() ?? [];
        if (icons.Count > Icons.Length || icons.Any(pair => !Icons.Contains(pair.Key) || !Icons.Contains(pair.Value))) throw new ArgumentException("Choose listed icons.");
        var actions = args.TryGetProperty("actions", out var chosen) ? chosen.Deserialize<List<string>>() ?? [] : p.QuickSystemActions;
        if (actions.Count > 8 || actions.Distinct().Count() != actions.Count || actions.Any(id => !SystemActionIds.Contains(id))) throw new ArgumentException("Choose up to eight different system controls.");
        var links = new List<QuickLink>();
        foreach (var value in args.GetProperty("links").EnumerateArray())
        {
            string id = value.GetProperty("id").GetString() ?? "", label = value.GetProperty("label").GetString()?.Trim() ?? "", icon = value.GetProperty("icon").GetString() ?? "";
            var original = Known(p, id);
            if (label.Length is < 1 or > 50 || label.Contains('\0') || (!Icons.Contains(icon) && icon != "app") || links.Any(link => link.Id == id)) throw new ArgumentException("Use a unique app link, a short label and a listed icon.");
            bool showInPanel = value.TryGetProperty("showInPanel", out var visible) ? visible.GetBoolean() : original.ShowInPanel;
            links.Add(new() { Id = id, Label = label, Icon = icon, Path = original.Path, ShowInPanel = showInPanel });
        }
        if (links.Count > 16 || links.Count(link => link.ShowInPanel) > 4) throw new ArgumentException("Show at most four app or file links in the panel.");
        p.PopupPosition = position; p.PopupOffsetX = x; p.PopupOffsetY = y;
        if (args.TryGetProperty("animations", out var animation)) p.PopupAnimations = animation.GetBoolean();
        p.PopupScale = scale; p.CompactPanel = compact;
        p.QuickIcons = icons; p.QuickSystemActions = actions; p.QuickLinks = links; p.Save();
    }
    internal static QuickLink? Add(Preferences p, IWin32Window owner)
    {
        if (p.QuickLinks.Count >= 16) throw new InvalidOperationException("The panel already has 16 app links.");
        var link = Pick(owner);
        if (link is not null) { link.ShowInPanel = p.QuickLinks.Count(value => value.ShowInPanel) < 4; p.QuickLinks.Add(link); p.Save(); }
        return link;
    }
    internal static QuickLink? AddFolder(Preferences p, IWin32Window owner)
    {
        if (p.QuickLinks.Count >= 16) throw new InvalidOperationException("The manager already has 16 saved links.");
        using var picker = new FolderBrowserDialog { Description = "Choose a folder shortcut" };
        if (picker.ShowDialog(owner) != DialogResult.OK) return null;
        var link = new QuickLink { Id = Guid.NewGuid().ToString("N"), Label = Path.GetFileName(picker.SelectedPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : picker.SelectedPath, Path = Path.GetFullPath(picker.SelectedPath), Icon = "tools", ShowInPanel = p.QuickLinks.Count(value => value.ShowInPanel) < 4 };
        p.QuickLinks.Add(link); p.Save();
        return link;
    }
    internal static QuickLink? Pick(IWin32Window owner)
    {
        using var picker = new OpenFileDialog { Title = "Choose an app or file", Filter = "All files (*.*)|*.*", CheckFileExists = true };
        if (picker.ShowDialog(owner) != DialogResult.OK) return null;
        string path = Path.GetFullPath(picker.FileName);
        var link = new QuickLink { Id = Guid.NewGuid().ToString("N"), Label = Path.GetFileNameWithoutExtension(path), Path = path, Icon = "tools" };
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                Directory.CreateDirectory(IconDirectory);
                using var bitmap = icon.ToBitmap(); bitmap.Save(Path.Combine(IconDirectory, link.Id + ".png"), ImageFormat.Png);
                link.Icon = "app";
            }
        }
        catch (Exception ex) { XiControl.Log.Ex("App icon", ex); }
        return link;
    }
    internal static void PickIcon(Preferences p, string id, IWin32Window owner)
    {
        var link = Known(p, id);
        using var picker = new OpenFileDialog { Title = "Choose a PNG app icon", Filter = "PNG image|*.png", CheckFileExists = true };
        if (picker.ShowDialog(owner) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > 5_000_000) throw new ArgumentException("Choose a PNG smaller than 5 MB.");
        using var image = Image.FromFile(picker.FileName);
        if (image.Width > 2048 || image.Height > 2048 || image.RawFormat.Guid != ImageFormat.Png.Guid) throw new ArgumentException("Choose a PNG no larger than 2048 pixels per side.");
        Directory.CreateDirectory(IconDirectory);
        image.Save(Path.Combine(IconDirectory, link.Id + ".png"), ImageFormat.Png);
        link.Icon = "app"; p.Save();
    }
    internal static void Open(Preferences p, string id)
    {
        OpenLink(Known(p, id));
    }
    internal static void OpenKey(Preferences p, string slot)
    {
        if (!AdvancedControls.KeyAppSlots.Contains(slot) || !p.KeyAppLinks.TryGetValue(slot, out var link)) throw new ArgumentException("Choose an app for this key in Keyboard settings.");
        OpenLink(link);
    }
    internal static void OpenLink(QuickLink link)
    {
        if (!Path.IsPathFullyQualified(link.Path) || (!File.Exists(link.Path) && !Directory.Exists(link.Path))) throw new InvalidOperationException("The selected app, file or folder no longer exists. Choose it again in Settings.");
        object? windows = null, desktop = null, document = null, shell = null;
        try
        {
            // Ask the existing desktop shell to launch as its user, not as this elevated resident.
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!);
            object location = 0, root = 0; int hwnd;
            desktop = ((dynamic)windows!).FindWindowSW(ref location, ref root, 8, out hwnd, 1);
            GetWindowThreadProcessId((nint)hwnd, out uint pid);
            if (pid == 0 || !UnelevatedShell(pid)) throw new InvalidOperationException("A normal Windows Explorer desktop is required to launch app links safely.");
            document = ((dynamic)desktop!).Document;
            shell = ((dynamic)document!).Application;
            ForegroundLaunch.Prepare();
            ((dynamic)shell!).ShellExecute(link.Path, "", Path.GetDirectoryName(link.Path), "open", 1);
            ForegroundLaunch.FocusExecutableSoon(link.Path);
        }
        catch (Exception ex) { XiControl.Log.Ex("App link", ex); throw new InvalidOperationException("Windows Explorer could not open this app link. No administrator launch was attempted."); }
        finally { foreach (object? value in new[] { shell, document, desktop, windows }) if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    }
    private static bool UnelevatedShell(uint pid)
    {
        using var process = Process.GetProcessById((int)pid);
        if (process.SessionId != Process.GetCurrentProcess().SessionId || process.ProcessName != "explorer") return false;
        nint handle = OpenProcess(0x1000, false, pid), token = 0;
        try { return handle != 0 && OpenProcessToken(handle, 8, out token) && GetTokenInformation(token, 20, out int elevated, sizeof(int), out _) && elevated == 0; }
        finally { if (token != 0) CloseHandle(token); if (handle != 0) CloseHandle(handle); }
    }
    private static QuickLink Known(Preferences p, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Unknown app link.");
        return p.QuickLinks.SingleOrDefault(l => l.Id == id) ?? throw new ArgumentException("Unknown app link.");
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint handle, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int kind, out int value, int length, out int returned);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
