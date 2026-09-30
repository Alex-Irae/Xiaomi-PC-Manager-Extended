using System.Diagnostics;
using Microsoft.Win32;
using System.Security;
using System.Windows.Automation;

namespace XiaomiAIManager.Services;

public sealed class XiaomiBridge(Preferences preferences) : IDisposable
{
    private readonly OemSession session = new();
    internal Func<bool> CleanupEnabled { set => session.CleanupEnabled = value; }
    internal object SessionState => session.State;
    internal void EndSessionForValidation()
    {
        lock (session.Sync) { session.Cancel(); OemServiceControl.RestoreDailyIsolation(); }
    }
    public void Dispose() => session.Dispose();
    public static readonly IReadOnlyDictionary<string, string> WindowsPages = new Dictionary<string, string>
    {
        ["settings"] = "ms-settings:",
        ["display"] = "ms-settings:display", ["sound"] = "ms-settings:sound",
        ["camera"] = "ms-settings:privacy-webcam", ["network"] = "ms-settings:network-status",
        ["updates"] = "ms-settings:windowsupdate", ["touchpad"] = "ms-settings:devices-touchpad",
        ["keyboard"] = "ms-settings:easeofaccess-keyboard", ["startup"] = "ms-settings:startupapps",
        ["nightlight"] = "ms-settings:nightlight", ["privacy"] = "ms-settings:privacy",
        ["microphone"] = "ms-settings:privacy-microphone", ["storage"] = "ms-settings:storagesense",
        ["about"] = "ms-settings:about", ["personalize"] = "ms-settings:personalization"
    };
    private static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>
    {
        ["home"] = "Home", ["tools"] = "Toolbox", ["battery"] = "Toolbox > Battery and performance", ["drivers"] = "Driver management",
        ["cleaner"] = "Toolbox > PC cleanup", ["optimization"] = "Toolbox > System boost",
        ["store"] = "Store", ["support"] = "Help",
        ["settings"] = "Settings", ["display"] = "Settings > Display", ["touchpad"] = "Settings > Touch",
        ["color"] = "Settings > Display > Color gamut / Eye protection / OLED protection",
        ["camera"] = "Settings > Meeting assistant", ["audio"] = "Settings > Meeting assistant",
        ["keys"] = "Settings > Keyboard / Function keys", ["gestures"] = "Settings > Touchpad > Gestures",
        ["network"] = "Settings > Network / Hotspot", ["privacy"] = "Settings > Privacy / Permissions",
        ["search"] = "Settings > AI file search", ["translation"] = "Toolbox > Screen translation / Subtitles",
        ["personalize"] = "Personalization > Wallpaper / Screensaver", ["account"] = "Account / Xiaomi services",
        ["general"] = "Settings > General", ["maintenance"] = "Settings > Cleanup and boost",
        ["repair"] = "Settings > Anomaly repair", ["thispc"] = "Settings > This PC", ["about"] = "Settings > About"
    };

    public string? Locate() => LocateComponent("manager", preferences);
    public object Components => new { manager = Locate(), store = LocateCompanion("store", preferences), ai = LocateCompanion("ai", preferences) };

    internal static string ExpectedFileName(string kind) => kind switch
    {
        "manager" => "XiaomiPcManager.exe", "store" => "MiAppStore.exe", "ai" => "XiaoaiAgent.exe",
        _ => throw new ArgumentException("Unknown Xiaomi component.")
    };

    internal static string ValidateSelection(string kind, string path)
    {
        string file = Path.GetFullPath(path);
        if (!File.Exists(file) || !Path.GetFileName(file).Equals(ExpectedFileName(kind), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Select an installed {ExpectedFileName(kind)} file.");
        return file;
    }

    private static string? LocateComponent(string kind, Preferences? settings = null)
    {
        string? selected = kind switch
        {
            "manager" => settings?.XiaomiExecutable, "store" => settings?.MiAppStoreExecutable,
            "ai" => settings?.XiaoAiExecutable, _ => throw new ArgumentException("Unknown Xiaomi component.")
        };
        string file = ExpectedFileName(kind);
        if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected)
            && Path.GetFileName(selected).Equals(file, StringComparison.OrdinalIgnoreCase)) return selected;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string root in kind switch
        {
            "manager" => new[] { Path.Combine(programFiles, "MI", "XiaomiPCManager"), Path.Combine(programFilesX86, "MI", "XiaomiPCManager") },
            "store" => new[] { Path.Combine(programFilesX86, "MiAppStore"), Path.Combine(programFiles, "MiAppStore") },
            _ => new[] { Path.Combine(programFiles, "MI", "XiaoaiAgent"), Path.Combine(programFilesX86, "MI", "XiaoaiAgent") }
        })
            if (NewestExecutable(root, file) is { } installed) return installed;
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (string name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                string display = entry?.GetValue("DisplayName") as string ?? "";
                if (!Matches(kind, display)) continue;
                string location = entry?.GetValue("InstallLocation") as string ?? "";
                if (NewestExecutable(location, file) is { } installed) return installed;
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { XiControl.Log.Ex("Xiaomi.Discovery", ex); }
        return null;
    }

    private static bool Matches(string kind, string display) => kind switch
    {
        "manager" => display.Contains("Xiaomi PC Manager", StringComparison.OrdinalIgnoreCase) || display.Contains("小米电脑管家"),
        "store" => display.Contains("Mi App Store", StringComparison.OrdinalIgnoreCase) || display.Contains("MiAppStore", StringComparison.OrdinalIgnoreCase) || display.Contains("小米应用商店"),
        _ => display.Contains("Xiaoai", StringComparison.OrdinalIgnoreCase) || display.Contains("小爱同学")
    };

    // Version folders are checked numerically; a stale older install never wins by directory order.
    private static string? NewestExecutable(string root, string file)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        try
        {
            string? versioned = Directory.EnumerateDirectories(root)
                .Select(path => (path, version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
                .Where(item => item.version is not null).OrderByDescending(item => item.version)
                .Select(item => Path.Combine(item.path, file)).FirstOrDefault(File.Exists);
            if (versioned is not null) return versioned;
            string direct = Path.Combine(root, file);
            return File.Exists(direct) ? direct : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { XiControl.Log.Ex("Xiaomi.Discovery", ex); return null; }
    }

    public object Open(string section)
    {
        if (section == "popup")
        {
            string file = Locate() ?? throw new MissingXiaomiComponentException("manager");
            if (!Path.GetFileName(file).Equals("XiaomiPcManager.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Select XiaomiPcManager.exe.");
            var popupStart = new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file)! };
            popupStart.ArgumentList.Add("--open_controlcenter"); // Verified Options.IsOpenControlCenter attribute in the local assembly.
            lock (session.Sync) { session.Cancel(); OemServiceControl.PrepareExplicitTools(); Process.Start(popupStart)?.Dispose(); session.Watch(file, includeQuickPanel: true); }
            return new { message = "Requested the original Xiaomi popup.", exactPage = false };
        }
        if (section == "ai") return OpenCompanion("ai", preferences);
        if (section == "store") return OpenCompanion("store", preferences);
        if (section is not ("home" or "tools" or "drivers" or "store"))
            throw new ArgumentException("Use Open Xiaomi Manager explicitly for proprietary OEM settings. Ordinary controls never launch the OEM manager.");
        if (!Sections.TryGetValue(section, out string? destination)) throw new ArgumentException("Unknown Xiaomi tool.");
        string executable = Locate() ?? throw new MissingXiaomiComponentException("manager");
        if (!Path.GetFileName(executable).Equals("XiaomiPcManager.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select XiaomiPcManager.exe in Settings. A launcher wrapper cannot be assumed to forward the driver command.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        // Verified in the supplied 5.8.1.121 Options and MainHelper IL, not guessed from frontend route IDs.
        if (section == "drivers") start.ArgumentList.Add("--scan_driver");
        lock (session.Sync)
        {
            session.Cancel();
            OemServiceControl.PrepareExplicitTools();
            Process.Start(start)?.Dispose();
            session.Watch(executable);
        }
        if (section == "drivers")
            return new { message = "Requested the Xiaomi driver scan. The original manager hosts its driver page.", exactPage = false };
        string[] navigation = section switch
        {
            "home" => ["Home"], "tools" => ["Toolbox"], "store" => ["Store"], "support" => ["Help"],
            "battery" => ["Toolbox", "Battery and performance"], "cleaner" => ["Toolbox", "PC cleanup"],
            "optimization" => ["Toolbox", "System boost"],
            "display" or "color" => ["Settings", "Display"], "touchpad" or "gestures" => ["Settings", "Touch"],
            "audio" or "camera" => ["Settings", "Meeting assistant"],
            "general" => ["Settings", "General"], "maintenance" => ["Settings", "Cleanup and boost"],
            "repair" => ["Settings", "Anomaly repair"], "thispc" => ["Settings", "This PC"], "about" => ["Settings", "About"],
            _ => ["Settings"]
        };
        bool sent = Navigate(executable, navigation);
        return new { message = sent ? $"Requested Xiaomi {string.Join(" > ", navigation)}." : $"Xiaomi navigation was unavailable. Open {destination} in the original manager.", exactPage = false, navigationRequested = sent };
    }

    internal static string? LocateCompanion(string kind, Preferences? settings = null) => LocateComponent(kind, settings);
    internal static object OpenCompanion(string kind, Preferences? settings = null)
    {
        string path = LocateCompanion(kind, settings) ?? throw new MissingXiaomiComponentException(kind);
        AppLinks.OpenLink(new QuickLink { Path = path });
        return new { message = kind == "ai" ? "Opened XiaoAI." : "Opened Xiaomi MiAppStore.", exactPage = true };
    }

    // Native accessibility patterns, on the worker. Scoped to the verified executable's own window.
    // ponytail: English labels target the supplied patch; a different patch requires updating these labels.
    private static bool Navigate(string executable, string[] labels)
    {
        foreach (string label in labels)
        {
            bool invoked = false;
            var timeout = Stopwatch.StartNew();
            while (!invoked && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                foreach (var process in Process.GetProcessesByName("XiaomiPcManager"))
                {
                    using (process)
                    try
                    {
                        if (!string.Equals(ProcessImage.PathFor(process.Id), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) continue;
                        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                        foreach (AutomationElement window in windows)
                        {
                            var matches = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, label));
                            foreach (AutomationElement match in matches)
                            {
                                AutomationElement? control = match;
                                for (int depth = 0; control is not null && depth < 3 && !control.Equals(window); depth++)
                                {
                                    if (control.Current.IsEnabled && !control.Current.IsOffscreen)
                                    {
                                        if (control.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection))
                                        { ((SelectionItemPattern)selection).Select(); invoked = true; break; }
                                        if (control.TryGetCurrentPattern(InvokePattern.Pattern, out object invoke))
                                        { ((InvokePattern)invoke).Invoke(); invoked = true; break; }
                                    }
                                    control = TreeWalker.ControlViewWalker.GetParent(control);
                                }
                                if (invoked) break;
                            }
                            if (invoked) break;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ElementNotAvailableException or System.Runtime.InteropServices.COMException)
                    { XiControl.Log.Ex("Xiaomi.Navigate", ex); }
                    if (invoked) break;
                }
                if (!invoked) Thread.Sleep(150);
            }
            if (!invoked) return false;
        }
        return true;
    }

    public static object OpenWindows(string page)
    {
        if (!WindowsPages.TryGetValue(page, out string? uri)) throw new ArgumentException("Unknown Windows settings page.");
        ForegroundLaunch.Prepare();
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        ForegroundLaunch.FocusWindowsSettingsSoon();
        return new { message = "Opened Windows Settings." };
    }
    public static object OpenOfficial(string page)
    {
        string url = page switch
        {
            "drivers" => "https://www.mi.com/service/notebook/drivers",
            "help" => "https://pc.mi.com/pc-manager-help",
            "manager" => "https://pc.mi.com/",
            _ => throw new ArgumentException("Unknown official Xiaomi destination.")
        };
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return new { message = "Opened the official Xiaomi " + page + " page." };
    }
}

internal sealed class MissingXiaomiComponentException(string kind) : InvalidOperationException(
    $"{(kind == "manager" ? "Xiaomi PC Manager" : kind == "store" ? "Xiaomi Store" : "XiaoAI")} is not installed or its executable could not be found. Select {XiaomiBridge.ExpectedFileName(kind)} to use this shortcut.")
{
    public string Kind { get; } = kind;
}
