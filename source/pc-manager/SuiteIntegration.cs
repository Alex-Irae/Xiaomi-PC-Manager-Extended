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
            foreach (var (component, label, arguments, icon) in new[]
            {
                ("file-search", "File Search", "--search", "search"),
                ("screen-translator", "Screen Translator", "--toggle", "translate")
            })
            {
                string path = SuiteEnvironment.Executable(component);
                if (!File.Exists(path) || Preferences.QuickLinks.Any(link => link.Path == path)) continue;
                Preferences.QuickLinks.Add(new() { Id = Guid.NewGuid().ToString("N"), Label = label, Path = path, Arguments = arguments, Icon = icon, ShowInPanel = Preferences.QuickLinks.Count(link => link.ShowInPanel) < 4 });
            }
            Preferences.Save();
        }
        catch (Exception error) { XiControl.Log.Ex("Suite.Start", error); Notify("Suite integration: " + error.Message); }
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
            enabled = true, hubRunning = suite is not null, error = suite?.Error,
            sharedAppearance = document.SharedAppearance, theme = document.Theme, accent = document.Accent,
            bindings = document.Bindings.Where(item => SuiteEnvironment.Installed(item.Key.Split('.')[0])).Select(item => new { action = item.Key, chord = item.Value }),
            components = new[] { "file-search", "screen-translator" }.Select(component =>
            {
                var status = SuiteStore.Status(component);
                bool running = status is not null && SuiteStore.Alive(status.Pid) && DateTime.UtcNow.Ticks - status.Updated < TimeSpan.FromSeconds(5).Ticks;
                return new { component, installed = SuiteEnvironment.Installed(component), running, state = running ? status!.State : "Stopped", error = running ? status!.Error : null, owner = suite is not null ? "PC Manager" : "Standalone" };
            })
        };
    }
    internal object SuiteSave(JsonElement args)
    {
        if (!SuiteEnvironment.Enabled) throw new InvalidOperationException("This installation is not a suite.");
        SuiteStore.SetBinding(args.GetProperty("action").GetString() ?? "", args.GetProperty("chord").GetString() ?? "");
        suite?.Poll(); RefreshWindows();
        return new { message = suite?.Error ?? "Shared shortcut saved.", complete = suite?.Error is null };
    }
    internal object SuiteAppearance(JsonElement args)
    {
        SuiteStore.Edit(value => { value.SharedAppearance = args.GetProperty("on").GetBoolean(); value.Theme = Preferences.Appearance; value.Accent = Preferences.ThemeAccent; });
        suite?.Poll(); return new { message = "Suite appearance updated." };
    }
    internal void PublishSuiteAppearance()
    {
        if (SuiteEnvironment.Enabled && SuiteStore.Read().SharedAppearance)
            SuiteStore.Edit(value => { value.Theme = Preferences.Appearance; value.Accent = Preferences.ThemeAccent; });
    }
    internal void SuiteOpen(string action)
    {
        if (!SuiteEnvironment.Enabled) throw new InvalidOperationException("Suite integration is unavailable.");
        if(action.StartsWith("pc-manager.custom.")) { int index=int.Parse(action["pc-manager.custom.".Length..])-1; if(index>=0&&index<Preferences.Shortcuts.Count)Advanced?.RunCustomAction(Preferences.Shortcuts[index]); return; }
        if(action=="pc-manager.copilot") {Advanced?.RunCustomAction(Preferences.CopilotShortcut);return;}
        string component = action.Split('.')[0];
        if (component is not ("file-search" or "screen-translator")) throw new ArgumentException("Unknown suite application.");
        string path = SuiteEnvironment.Executable(component);
        if (!File.Exists(path)) throw new InvalidOperationException("That optional application is not installed.");
        bool settings = action == component + ".settings";
        if (!settings) SuiteStore.Send(action);
        // These are our fixed companion EXEs. Explorer ShellExecute can silently fail to launch them.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            Arguments = settings ? component == "file-search" ? "--center" : "" : "--tray",
            WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false, CreateNoWindow = true
        });
        if (process is null) throw new InvalidOperationException("Windows could not start " + component + ".");
    }
    internal void OpenAppLink(string id)
    {
        var link=Preferences.QuickLinks.SingleOrDefault(item=>item.Id==id)??throw new ArgumentException("Unknown app link.");
        if(SuiteEnvironment.Enabled)
            foreach(string component in new[] {"file-search","screen-translator"})
                if(string.Equals(link.Path,SuiteEnvironment.Executable(component),StringComparison.OrdinalIgnoreCase))
                { HideWindows(); SuiteOpen(component+(component=="file-search"?".open":".toggle")); return; }
        AppLinks.Open(Preferences,id);
    }
}
