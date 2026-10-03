// Integration paths: keep XiControl's own installed data and portable markers untouched.
namespace XiControl.Config;
public static class AppPaths
{
    public static string DataDir => XiaomiAIManager.Services.Preferences.DataDirectory;
    public static bool Portable => false;
    public static string? FallbackReason => null;
    public static string? ExeDir => AppContext.BaseDirectory;
    public static string AppDataDir => DataDir;
}
