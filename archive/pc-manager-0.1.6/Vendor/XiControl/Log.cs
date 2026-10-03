// Integration shim: replaces XiControl's config-dependent log; never deletes logs.
namespace XiControl;

internal static class Log
{
    private static readonly object Sync = new();
    private static readonly string FileName = "native-" + DateTime.Now.ToString("yyyyMMddTHHmmssfff") + ".log";
    public static bool Enabled { get; set; } = true;
    public static string FilePath => Path.Combine(XiaomiAIManager.Services.Preferences.DataDirectory, FileName);
    public static void Ex(string where, Exception ex) => Write($"{where}: {ex.GetType().Name}: {ex.Message}");
    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Sync)
            {
                string directory = XiaomiAIManager.Services.Preferences.DataDirectory;
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, FileName), $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must not interrupt hardware operation. */ }
    }
}
