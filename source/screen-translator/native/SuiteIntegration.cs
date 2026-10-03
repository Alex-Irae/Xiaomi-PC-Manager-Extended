// Purpose: synchronize translator bindings, share appearance and preserve standalone shortcut handling.
// Dependencies: native translator and compiled shared/SuiteLink.cs. Outputs: isolated local configuration.
// Run: install/Screen Translator/ScreenTranslator.exe --toggle, --region or --tray.
using System.Diagnostics;
using System.Text.Json;
using XiaomiRevamp.Suite;

namespace LocalScreenTranslator;

internal sealed partial class MainWindow
{
    SuiteClient? suite;
    string SuiteClientHubOwner()=>SuiteClient.HubRunning()?"PC Manager":"Standalone";
    void StartSuite()
    {
        if (!SuiteEnvironment.Enabled || Program.CheckUi&&!Program.CheckShortcuts || suite is not null) return;
        var defaults = SuiteStore.Defaults.Where(item => item.Key.StartsWith("screen-translator.")).ToDictionary(item => item.Key, item => item.Value);
        defaults["screen-translator.toggle"] = config["shortcut"]!.GetValue<string>();
        suite = new("screen-translator", defaults, action => Post(() => _ = SuiteAction(action)), document =>
        {
            bool changed = false;
            if (document.Bindings.TryGetValue("screen-translator.toggle", out var chord) && chord != config["shortcut"]!.GetValue<string>()) { config["shortcut"] = chord; changed = true; }
            if (document.SharedAppearance && config["follow_suite_appearance"]!.GetValue<bool>())
            {
                changed |= config["theme"]!.GetValue<string>() != document.Theme || config["accent"]!.GetValue<string>() != document.Accent;
                config["theme"] = document.Theme; config["accent"] = document.Accent; ApplyAppearance(config);
            }
            if (changed)
            {
                SaveConfig();
                if (navigated) web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "suite-settings", shortcut = config["shortcut"]!.GetValue<string>(), theme = config["theme"]!.GetValue<string>(), accent = config["accent"]!.GetValue<string>() }));
            }
            Publish();
        }, () => busy ? ready ? "Working" : "Models loading" : ready ? "Models ready" : "Models unloaded");
    }
    async Task SuiteAction(string action)
    {
        try
        {
            if (action == "screen-translator.toggle") await Hotkey(6);
            else if (action == "screen-translator.screen") await Hotkey(1);
            else if (action == "screen-translator.region") await Hotkey(2);
            else if (action == "screen-translator.original") await Hotkey(3);
            else if (action == "screen-translator.filter") await Hotkey(4);
        }
        catch (Exception error) { Fail(error.Message, false); }
    }
    void OpenSuiteManager()
    {
        if (!SuiteEnvironment.Installed("pc-manager")) throw new InvalidOperationException("PC Manager is not installed.");
        Process.Start(new ProcessStartInfo(SuiteEnvironment.Executable("pc-manager"), "--manager") { UseShellExecute = true });
    }
}
