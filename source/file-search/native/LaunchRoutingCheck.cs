// Purpose: verify actual resident-instance CLI messages and search-only activation.
// Dependencies: Windows .NET 8; no Python, models or browser are started.
// Outputs: launch-routing-check.json in --data. Run: AI Center.exe --check-launch-routing --data ABSOLUTE_DIR --config CONFIG.
using System.Diagnostics;
using System.Reflection;

namespace LocalAICenter;

internal sealed partial class CenterContext
{
    internal async Task<object> CheckLaunchCommands()
    {
        var checks=new Dictionary<string,bool>();
        async Task Launch(params string[] arguments)
        {
            var start=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true};
            if(Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach(string argument in arguments)start.ArgumentList.Add(argument);
            foreach(string argument in new[]{"--data",Program.Data,"--config",Program.Config,"--no-shortcut","--paused"})start.ArgumentList.Add(argument);
            using var process=Process.Start(start)!;
            await process.WaitForExitAsync();
            if(process.ExitCode!=0)throw new InvalidOperationException("Child launch failed");
            await Task.Delay(250);
        }
        await Launch("--tray");
        checks["residentTrayKeepsBothWindowsHidden"]=center is null&&!Search.Visible;
        // Both standalone Double Ctrl and the shared file-search.open action use this method.
        Search.ShowSearch();await Task.Delay(100);
        checks["shortcutActionShowsOnlySearch"]=Search.Visible&&center is null;
        await Launch("--tray");
        checks["hubBackgroundActivationPreservesSearchOnly"]=Search.Visible&&center is null;
        DismissSearch();await Launch("--search");
        checks["explicitSearchShowsOnlySearch"]=Search.Visible&&center is null;
        DismissSearch();await Launch("--center");
        checks["managerSettingsInvocationShowsCenter"]=center?.Visible==true&&!Search.Visible;
        center!.Hide();await Launch();
        checks["directExeInvocationShowsCenter"]=center.Visible&&!Search.Visible;
        center.Hide();await Launch("--tray");await Launch("--tray");
        checks["repeatedBackgroundInvocationsNeverReopenCenter"]=!center.Visible&&!Search.Visible;
        checks["noBrowserOrInferenceStarted"]=backend is null&&!Search.BrowserLoaded&&!center.BrowserLoaded;
        foreach(var item in checks)Program.Log((item.Value?"PASS: ":"FAIL: ")+item.Key);
        if(checks.Values.Any(value=>!value))Environment.ExitCode=1;
        return new {passed=checks.Values.All(value=>value),checks,physicalKeyboardDelivered=false};
    }
}
