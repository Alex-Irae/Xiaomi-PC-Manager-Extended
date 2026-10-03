// Purpose: synchronize search shortcuts with PC Manager and retain standalone ownership.
// Dependencies: existing native search shell and compiled shared/SuiteLink.cs.
// Outputs: isolated search configuration. Run: install/AI Center/AI Center.exe --search.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using XiaomiRevamp.Suite;

namespace LocalAICenter;

internal sealed partial class CenterContext
{
    SuiteClient? suite;
    void InstallSuite()
    {
        keyboard?.Dispose(); keyboard = null;
        if (suite is not null) { SyncSuite(SuiteStore.Read()); return; }
        suite = new("file-search", new() { ["file-search.open"] = settings.GetProperty("shortcut").GetString() ?? "double_ctrl" },
            action => Post(() => { if (action == "file-search.open") Search.ShowSearch(); }), SyncSuite,
            () => IndexingActive ? "Indexing" : backend is null ? "Idle" : backendReady ? "Search ready" : "Starting search");
    }
    void SyncSuite(SuiteDocument document)
    {
        var updates = new Dictionary<string, object>();
        if (document.Bindings.TryGetValue("file-search.open", out var chord)) updates["shortcut"] = chord == "Alt+Space" ? "alt_space" : chord;
        bool follow = !settings.TryGetProperty("follow_suite_appearance", out var setting) || setting.GetBoolean();
        if (document.SharedAppearance && follow) { updates["theme"] = document.Theme; updates["accent_color"] = document.Accent; }
        var current = settings.Deserialize<Dictionary<string, JsonElement>>()!;
        var changed = updates.Where(item => !current.TryGetValue(item.Key, out var previous) || previous.GetString() != (string)item.Value).ToDictionary(item => item.Key, item => item.Value);
        foreach (var item in updates) current[item.Key] = JsonSerializer.SerializeToElement(item.Value);
        settings = JsonSerializer.SerializeToElement(current);
        if (changed.Count > 0)
        {
            var saved = File.Exists(Program.Config) ? JsonNode.Parse(SuiteStore.ReadText(Program.Config))!.AsObject() : JsonSerializer.SerializeToNode(current)!.AsObject();
            foreach (var item in updates) saved[item.Key] = JsonSerializer.SerializeToNode(item.Value);
            SuiteStore.AtomicWrite(Program.Config, saved);
            if (backend is not null && !backend.HasExited) Write(new { owner = "suite", mode = 0, data = new { id = 2147483645, persistent = false, request = new { method = "local_save_config", @params = changed } } });
            center?.Script("window.dispatchEvent(new CustomEvent('suite-settings',{detail:" + JsonSerializer.Serialize(updates) + "}))");
            Search.Script("window.dispatchEvent(new CustomEvent('settings-changed',{detail:" + settings.GetRawText() + "}))");
        }
        center?.Script("window.dispatchEvent(new CustomEvent('suite-owner',{detail:" + JsonSerializer.Serialize(SuiteClient.HubRunning() ? "PC Manager manages shortcuts" : "Standalone shortcut handling") + "}))");
        BroadcastTheme();
    }
    void SaveSuiteShortcut(JsonElement parameters)
    {
        if (SuiteEnvironment.Enabled && parameters.TryGetProperty("shortcut", out var chord)) SuiteStore.SetBinding("file-search.open", chord.GetString() ?? "none");
    }
    void OpenSuiteManager()
    {
        if (!SuiteEnvironment.Installed("pc-manager")) throw new InvalidOperationException("PC Manager is not installed.");
        Process.Start(new ProcessStartInfo(SuiteEnvironment.Executable("pc-manager"), "--manager") { UseShellExecute = true });
    }
}
