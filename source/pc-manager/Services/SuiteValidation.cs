// Purpose: focused installed WebView shortcut and layout checks, with saved state restored.
// Dependencies: running suite PC Manager and local WebView; no hardware-setting changes.
// Outputs: new timestamped evidence under the PC Manager profile/results.
// Command: PCManager.exe --validate-suite; the final quick-link launch starts translation.
using System.Text.Json;
using XiaomiRevamp.Suite;

namespace XiaomiAIManager.Services;

internal static class SuiteValidation
{
    internal static async Task RunAsync(ManagerApplication app,MainWindow window)
    {
        string run=Path.Combine(SuiteEnvironment.Data("pc-manager"),"results","suite-ui",DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ"));Directory.CreateDirectory(run);
        var baseline=SuiteStore.Read();var shortcuts=app.Preferences.Shortcuts.Select(value=>new KeyboardShortcut{Chord=value.Chord,Action=value.Action,AppId=value.AppId}).ToList();
        File.WriteAllText(Path.Combine(run,"config.json"),JsonSerializer.Serialize(new {seed=0,mode="suite-shortcut-webview",protocol="Actual trusted native requests, action-preserving swaps and DOM spacing; saved keys restored; no hardware changes",bindings=baseline.Bindings,shortcuts}));
        try
        {
            var report=await window.ValidateBridgeAsync(run,"suite");
            File.WriteAllText(Path.Combine(run,"summary.json"),report.GetRawText());
            await window.CaptureManagerPagesAsync(run);
        }
        finally
        {
            app.Preferences.Shortcuts=shortcuts;app.Preferences.Save();
            SuiteStore.Edit(value=> {value.Bindings=new(baseline.Bindings);value.Reservations=new(baseline.Reservations);});
            app.ReapplyShortcuts();
            var restored=SuiteStore.Read();
            File.WriteAllText(Path.Combine(run,"restoration.json"),JsonSerializer.Serialize(new {passed=JsonSerializer.Serialize(restored.Bindings)==JsonSerializer.Serialize(baseline.Bindings)&&JsonSerializer.Serialize(restored.Reservations)==JsonSerializer.Serialize(baseline.Reservations)}));
        }
        // Launch after capture/restoration: the production link closes the manager document.
        var translator=app.Preferences.QuickLinks.FirstOrDefault(link=>string.Equals(link.Path,SuiteEnvironment.Executable("screen-translator"),StringComparison.OrdinalIgnoreCase));
        if(translator is not null)app.OpenAppLink(translator.Id);
    }
}
