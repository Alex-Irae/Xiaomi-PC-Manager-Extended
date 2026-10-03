using System.Diagnostics;
using System.Text.Json;
using System.Security.Principal;

namespace XiaomiAIManager.Services;

// Service operations happen only on resident startup or an explicit OEM action.
internal static class OemServiceControl
{
    internal static bool IsolationRequested
    {
        get
        {
            if (Program.TestMode) return false;
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            string path = Path.Combine(Preferences.DataDirectory, "service-state", sid, "oem-service-isolation.json");
            string legacy = Path.Combine(AppContext.BaseDirectory, "service-state", sid, "oem-service-isolation.json");
            string json;
            try
            {
                // Preserve the old resident's isolation intent across a move to a per-user install.
                if (!File.Exists(path) && File.Exists(legacy))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(legacy, path, overwrite: false);
                    XiControl.Log.Write("Migrated OEM isolation intent to per-user data.");
                }
                json = File.ReadAllText(path);
            }
            catch (FileNotFoundException) { XiControl.Log.Write("OEM isolation intent missing: " + path); return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (Exception ex) { XiControl.Log.Ex("OEM isolation intent read", ex); throw; }
            using var data = JsonDocument.Parse(json);
            return data.RootElement.GetProperty("enabled").GetBoolean();
        }
    }
    internal static void PrepareExplicitTools()
    {
        if (!IsolationRequested) return;
        Run("Tools");
    }
    internal static void RestoreDailyIsolation()
    {
        if (IsolationRequested) Run("Disable");
    }
    private static void Run(string action)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "tools", "oem-service.ps1"), "-Action", action }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The reversible Xiaomi service controller could not start.");
        // Keep service requests serialized until the native service operation finishes.
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Xiaomi service operation failed. Use Restore Xiaomi service.cmd and inspect its saved records.");
        XiControl.Log.Write("OEM service action: " + action);
    }
}
