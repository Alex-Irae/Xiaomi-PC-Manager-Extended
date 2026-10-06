// Purpose: PC Manager's suite launcher, shared shortcut editor and appearance hub.
// Dependencies: existing resident, Explorer launch broker and compiled shared/SuiteLink.cs.
// Outputs: isolated suite configuration. Run: install/PC Manager/PCManager.exe --manager.
using System.Text.Json;
using XiaomiAIManager.Services;
using XiaomiRevamp.Suite;

namespace XiaomiAIManager;

public sealed partial class ManagerApplication
{
    private SuiteClient? suite;
    private void StartSuite()
    {
        if (!SuiteEnvironment.Enabled || Program.TestMode) return;
        try
        {
            var defaults = SuiteStore.Defaults.Where(item => SuiteEnvironment.Installed(item.Key.Split('.')[0])).ToDictionary(item => item.Key, item => item.Value);
            SuiteStore.Initialize(defaults);
            SyncSuiteReservations(SuiteStore.Read());
            SuiteStore.Reserve(SuiteReservations(Preferences.Shortcuts, Preferences.CopilotShortcut));
            // The default no-action hook must not swallow the translator's Copilot shortcut.
            copilot.Configure(EffectiveCopilot(Preferences.CopilotShortcut));
            suite = new("pc-manager", new(), action => Post(() => { try { SuiteOpen(action); } catch (Exception error) { Notify(error.Message); } }), document =>
            {
                SyncSuiteReservations(document);
                if (document.SharedAppearance && (Preferences.Appearance != document.Theme || Preferences.ThemeAccent != document.Accent))
                {
                    Preferences.Appearance = document.Theme; Preferences.ThemeAccent = document.Accent;
                    Preferences.ThemePreset = "custom"; Preferences.Save(); ApplyAppearance();
                }
                Post(RefreshWindows);
            }, () => "Shortcut hub running");
            XiControl.Log.Write($"Shortcuts.Start root={SuiteEnvironment.Root} data={SuiteEnvironment.DataRoot} error={suite.Error}");
            PublishSuiteAppearance();
            foreach (var (component, label, arguments, icon) in new[]
            {
                ("file-search", "File Search", "--search", "search"),
                ("screen-translator", "Screen Translator", "--screen", "translate")
            })
            {
                string path = SuiteEnvironment.Executable(component);
                if (!File.Exists(path) || Preferences.QuickLinks.Any(link => link.Path == path)) continue;
                Preferences.QuickLinks.Add(new() { Id = Guid.NewGuid().ToString("N"), Label = label, Path = path, Arguments = arguments, Icon = icon, ShowInPanel = Preferences.QuickLinks.Count(link => link.ShowInPanel) < 4 });
            }
            Preferences.Save();
            if(SuiteEnvironment.Installed("file-search"))
            {
                string config=Path.Combine(SuiteEnvironment.Data("file-search"),"config.json");
                using var saved=File.Exists(config)?JsonDocument.Parse(File.ReadAllText(config)):null;
                // File search is started in the background only when its own startup settings allow it.
                bool Wanted(string key,bool fallback)=>saved is null||!saved.RootElement.TryGetProperty(key,out var value)?fallback:value.ValueKind==JsonValueKind.True;
                if((saved is null || saved.RootElement.TryGetProperty("model_standby",out var mode)&&mode.GetString()=="keep_loaded")&&(Wanted("run_at_startup",true)||Wanted("center_at_startup",false)))
                    ProcessSearchResident();
            }
        }
        catch (Exception error) { XiControl.Log.Ex("Suite.Start", error); Notify("Suite integration: " + error.Message); }
    }
    static void ProcessSearchResident()
    {
        string path=SuiteEnvironment.Executable("file-search");
        using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path,"--tray")
        {WorkingDirectory=Path.GetDirectoryName(path)!,UseShellExecute=false,CreateNoWindow=true});
    }
    internal static Dictionary<string, string> SuiteReservations(IEnumerable<KeyboardShortcut> shortcuts, KeyboardShortcut copilot)
    {
        var result = shortcuts.Select((shortcut, index) => new KeyValuePair<string, string>("PC Manager shortcut " + (index + 1), shortcut.Chord)).ToDictionary(item => item.Key, item => item.Value);
        if (copilot.Action is not ("none" or "system")) result["PC Manager Copilot"] = "Copilot";
        return result;
    }
    private KeyboardShortcut EffectiveCopilot(KeyboardShortcut value) => SuiteEnvironment.Enabled || Preferences.Shortcuts.Any(item=>SuiteChord.Parse(item.Chord).Text=="Copilot") ? new() { Chord = value.Chord, Action = "system" } : value;
    void SyncSuiteReservations(SuiteDocument document)
    {
        bool changed=false;
        for(int index=0;index<Preferences.Shortcuts.Count;index++)
        {
            if(document.Reservations.TryGetValue("PC Manager shortcut "+(index+1),out var chord) && chord!=SuiteChord.Parse(Preferences.Shortcuts[index].Chord).Text)
            { Preferences.Shortcuts[index].Chord=chord; changed=true; }
        }
        if(changed)
        {
            if(!shortcuts.Configure(Preferences.Shortcuts)) throw new InvalidOperationException(shortcuts.Error);
            Preferences.Save(); copilot.Configure(EffectiveCopilot(Preferences.CopilotShortcut));
        }
    }
    internal object RecordShortcut(IWin32Window owner,JsonElement args)
    {
        string current=args.TryGetProperty("chord",out var chord)?chord.GetString()??"none":"none";
        shortcuts.Configure(Array.Empty<KeyboardShortcut>()); copilot.Configure(new() {Action="system",Chord="Win+Shift+F23"});
        try
        {
            suite?.Suspend(true);
            using var dialog=new SuiteShortcutDialog(current);
            return dialog.ShowDialog(owner)==DialogResult.OK ? new {chord=dialog.Selected,cancelled=false} : new {chord=current,cancelled=true};
        }
        finally { shortcuts.Configure(Preferences.Shortcuts); copilot.Configure(EffectiveCopilot(Preferences.CopilotShortcut)); suite?.Suspend(false); }
    }
    internal object SuiteRead()
    {
        if (!SuiteEnvironment.Enabled) return new { enabled = false };
        var document = SuiteStore.Read();
        return new
        {
            enabled = true, hubRunning = suite is not null && SuiteClient.HubRunning(), error = suite?.Error,
            sharedAppearance = document.SharedAppearance, theme = document.Theme, accent = document.Accent,
            bindings = document.Bindings.Where(item => SuiteEnvironment.Installed(item.Key.Split('.')[0])).Select(item => new { action = item.Key, chord = item.Value }),
            components = new[] { "file-search", "screen-translator" }.Select(component =>
            {
                var status = SuiteRunning(component);
                bool running = status is not null;
                return new { component, title = component == "file-search" ? "File Search (AI Center)" : "Screen Translator", installed = SuiteEnvironment.Installed(component), running, state = running ? status!.State : "Not running", error = running ? status!.Error : null, owner = SuiteClient.HubRunning() ? "PC Manager" : "Standalone", startup = StartsWithWindows(component) };
            }).Append(FileSyncRow())
        };
    }
    // --- Companion apps on the Toolbox page ---------------------------------------------------------------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static SuiteStatus? SuiteRunning(string component)
    {
        var status = SuiteStore.Status(component);
        return status is not null && SuiteStore.Alive(status.Pid) && DateTime.UtcNow.Ticks - status.Updated < TimeSpan.FromSeconds(5).Ticks ? status : null;
    }
    // FileSync is its own product with its own installer; it is listed when its uninstall entry points to a real program.
    static string? FileSyncExecutable()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiRevamp.FileSync");
        string? path = key?.GetValue("InstallLocation") is string root ? Path.Combine(root, "FileSync.exe") : null;
        return path is not null && File.Exists(path) ? path : null;
    }
    static bool ProcessRunning(string executable)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            using (process)
                try { if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return false;
    }
    static object FileSyncRow()
    {
        string? executable = FileSyncExecutable();
        bool running = executable is not null && ProcessRunning(executable);
        return new { component = "filesync", title = "FileSync", installed = executable is not null, running, state = running ? "Running" : "Not running", error = (string?)null, owner = "", startup = executable is not null && StartsWithWindows("filesync") };
    }
    static string StartupName(string component) => component == "filesync" ? "FileSync" : "XiaomiRevampSuite." + (component == "file-search" ? "Search." : "Translator.") + SuiteEnvironment.Identity;
    static bool AppSetting(string component, string key, bool fallback)
    {
        string path = Path.Combine(SuiteEnvironment.Data(component), "config.json");
        try
        {
            if (!File.Exists(path)) return fallback;
            using var saved = JsonDocument.Parse(SuiteStore.ReadText(path));
            return saved.RootElement.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.True : fallback;
        }
        catch (Exception error) when (error is IOException or JsonException) { return fallback; }
    }
    // File search has two startup choices of its own; the switch here is the one for search itself.
    static bool StartsWithWindows(string component)
    {
        if (component == "file-search") return AppSetting(component, "run_at_startup", true);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(StartupName(component)) is string;
    }
    static void RunQuietly(string executable, string arguments, bool wait)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, arguments) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("Windows could not start " + Path.GetFileName(executable) + ".");
        if (wait && !process.WaitForExit(15000)) throw new TimeoutException(Path.GetFileName(executable) + " did not answer.");
    }
    internal object SuiteStartup(JsonElement args)
    {
        string component = args.GetProperty("component").GetString() ?? ""; bool on = args.GetProperty("on").GetBoolean();
        if (component == "filesync")
        {
            // FileSync owns its setting and its Windows entry, running or not.
            RunQuietly(FileSyncExecutable() ?? throw new InvalidOperationException("FileSync is not installed."), "--startup " + (on ? "on" : "off"), true);
        }
        else
        {
            if (component is not ("file-search" or "screen-translator") || !SuiteEnvironment.Installed(component)) throw new ArgumentException("Unknown suite application.");
            if (SuiteRunning(component) is not null) SuiteStore.Send(component + (on ? ".startup-on" : ".startup-off"));
            else
            {
                // The app is closed: write what it would have written itself, its setting and its Windows entry.
                string setting = component == "file-search" ? "run_at_startup" : "autostart", path = Path.Combine(SuiteEnvironment.Data(component), "config.json");
                if (File.Exists(path)) { var saved = System.Text.Json.Nodes.JsonNode.Parse(SuiteStore.ReadText(path))!.AsObject(); saved[setting] = on; SuiteStore.AtomicWrite(path, saved); }
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (on || component == "file-search" && AppSetting(component, "center_at_startup", false)) key.SetValue(StartupName(component), "\"" + SuiteEnvironment.Executable(component) + "\" --tray");
                else key.DeleteValue(StartupName(component), false);
            }
        }
        string name = component == "file-search" ? "File Search" : component == "screen-translator" ? "Screen Translator" : "FileSync";
        return new { message = name + (on ? " starts with Windows." : " no longer starts with Windows.") };
    }
    internal object SuiteQuit(JsonElement args)
    {
        string component = args.GetProperty("component").GetString() ?? "";
        if (component == "filesync") RunQuietly(FileSyncExecutable() ?? throw new InvalidOperationException("FileSync is not installed."), "--exit", false);
        else if (component is "file-search" or "screen-translator" && SuiteEnvironment.Installed(component)) RunQuietly(SuiteEnvironment.Executable(component), "--quit", false);
        else throw new ArgumentException("Unknown suite application.");
        return new { message = "Asked " + (component == "file-search" ? "File Search" : component == "screen-translator" ? "Screen Translator" : "FileSync") + " to close." };
    }
    internal object SuiteSave(JsonElement args)
    {
        if (!SuiteEnvironment.Enabled) throw new InvalidOperationException("This installation is not a suite.");
        string action=args.GetProperty("action").GetString()??"",chord=SuiteChord.Parse(args.GetProperty("chord").GetString()??"").Text;
        SuiteStore.SetBinding(action,chord);
        suite?.Poll(); RefreshWindows();
        bool saved=SuiteStore.Read().Bindings.GetValueOrDefault(action)==chord;
        bool active=saved && (chord=="none" || suite?.Registered(action)==true);
        return new { saved,active,action,chord,message=active?"Shortcut saved.":suite?.Error??"Shortcut saved, but the shortcut hub is unavailable.",complete=active };
    }
    internal object SuiteAppearance(JsonElement args)
    {
        SuiteStore.Edit(value => { value.SharedAppearance = args.GetProperty("on").GetBoolean(); value.Theme = Preferences.Appearance; value.Accent = Preferences.ThemeAccent; (value.Background, value.Surface) = SharedColours(); });
        suite?.Poll(); return new { message = "Suite appearance updated." };
    }
    internal void PublishSuiteAppearance()
    {
        if (!SuiteEnvironment.Enabled) return;
        // Also run at start, so a palette chosen before the window colours were shared reaches the other apps
        // without pressing Apply. Nothing is written when the shared values are already these.
        var shared = SuiteStore.Read(); var (background, surface) = SharedColours();
        if (!shared.SharedAppearance || shared.Theme == Preferences.Appearance && shared.Accent == Preferences.ThemeAccent && shared.Background == background && shared.Surface == surface) return;
        SuiteStore.Edit(value => { value.Theme = Preferences.Appearance; value.Accent = Preferences.ThemeAccent; (value.Background, value.Surface) = (background, surface); });
    }
    // The four built-in presets keep each theme's own window colours; a custom or saved palette brings its own.
    (string, string) SharedColours() => Preferences.ThemePreset is "blue" or "red" or "pink" or "green" ? ("", "") : (Preferences.ThemeBackground, Preferences.ThemeSurface);
    internal void SuiteOpen(string action)
    {
        if (action == "filesync.open") { RunQuietly(FileSyncExecutable() ?? throw new InvalidOperationException("FileSync is not installed."), "", false); return; }
        XiControl.Log.Write("Shortcuts.Invoke action="+action);
        if (!SuiteEnvironment.Enabled) throw new InvalidOperationException("Suite integration is unavailable.");
        if(action.StartsWith("pc-manager.custom.")) { int index=int.Parse(action["pc-manager.custom.".Length..])-1; if(index>=0&&index<Preferences.Shortcuts.Count)Advanced?.RunCustomAction(Preferences.Shortcuts[index]); return; }
        if(action=="pc-manager.copilot") {Advanced?.RunCustomAction(Preferences.CopilotShortcut);return;}
        string component = action.Split('.')[0];
        if (component is not ("file-search" or "screen-translator")) throw new ArgumentException("Unknown suite application.");
        string path = SuiteEnvironment.Executable(component);
        if (!File.Exists(path)) throw new InvalidOperationException("That optional application is not installed.");
        bool settings = action == component + ".settings";
        var status=SuiteStore.Status(component);
        if(!settings&&status is not null&&SuiteStore.Alive(status.Pid)&&DateTime.UtcNow.Ticks-status.Updated<TimeSpan.FromSeconds(3).Ticks)
        {
            AllowSetForegroundWindow((uint)status.Pid);
            SuiteStore.Send(action);return;
        }
        string arguments=settings?(component=="file-search"?"--center":""):component=="file-search"?"--search":action[(component.Length+1)..] switch
        {"toggle"=>"--toggle","screen"=>"--screen","region"=>"--region","original"=>"--original","filter"=>"--filter",_=>throw new ArgumentException("Unknown translation action.")};
        // These are our fixed companion EXEs. Explorer ShellExecute can silently fail to launch them.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false, CreateNoWindow = true
        });
        if (process is null) throw new InvalidOperationException("Windows could not start " + component + ".");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool AllowSetForegroundWindow(uint process);
    internal void OpenAppLink(string id)
    {
        var link=Preferences.QuickLinks.SingleOrDefault(item=>item.Id==id)??throw new ArgumentException("Unknown app link.");
        if(SuiteEnvironment.Enabled)
            foreach(string component in new[] {"file-search","screen-translator"})
                if(string.Equals(link.Path,SuiteEnvironment.Executable(component),StringComparison.OrdinalIgnoreCase))
                { HideWindows(); SuiteOpen(component+(component=="file-search"?".open":".screen")); return; }
        AppLinks.Open(Preferences,id);
    }
}
