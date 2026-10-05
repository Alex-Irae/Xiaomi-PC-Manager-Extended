// Purpose: English Xiaomi-style hardware manager, without modifying OEM applications.
// Dependencies: Windows x64, .NET 8 Desktop, WebView2 Runtime; packages in the csproj.
// Outputs: %LOCALAPPDATA%\XiaomiAIManager\settings.json and timestamped native logs.
// Build: dotnet build XiaomiAIManager.csproj -c Release
// Run/popup key binding: .\bin\Release\net8.0-windows\PCManager.exe --toggle
// Other launch modes: --manager opens the secondary app; --tray starts hidden.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace XiaomiAIManager;

internal static class Program
{
    internal static bool TestMode { get; private set; }
    internal static string PopupTitle => (TestMode ? "PC Manager · Test" : "PC Manager") + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? " · " + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "");
    internal static string InstanceName => "XiaomiAIManager." + WindowsIdentity.GetCurrent().User!.Value + (TestMode ? ".Test" : "") + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? ".Suite." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--check-settings" }))
        {
            try { Services.AdvancedControls.CheckSettingValidation(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return; // Pure checks do not initialize WinForms, elevate, subscribe or acquire hardware.
        }
        if (args.SequenceEqual(new[] { "--check-backup" }))
        {
            try { Services.SettingsBackup.Check(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        TestMode = args.Contains("--test", StringComparer.Ordinal);
        var commands = args.Where(arg => arg != "--test").ToArray();
        string command = commands.Length == 0 ? "toggle" : commands.Length == 1 ? commands[0] switch
        {
            "--toggle" => "toggle", "--manager" => "manager", "--tray" => "tray",
            "--monitor-small" => "monitor-small", "--monitor-medium" => "monitor-medium", "--monitor-large" => "monitor-large",
            "--hide" => "hide", "--quit" => "quit", "--install-startup" => "install-startup", "--register-startup" => "register-startup",
            "--cycle-mode" => "cycle-mode", "--brightness-up" => "brightness-up", "--brightness-down" => "brightness-down", "--toggle-travel" => "toggle-travel", "--screen-off" => "screen-off", "--verify-hardware" => "verify-hardware", "--reapply-policies" => "reapply-policies", "--validate-controls" => "validate-controls", "--validate-oem" => "validate-oem", "--validate-ui" => "validate-ui", "--validate-brightness" => "validate-brightness", _ => "invalid"
        } : "invalid";
        // Testing control switch ("PCManager.exe" --disable, see shared/Launcher.cs). The startup task
        // runs this native EXE directly every minute, so the automatic start is refused here as well.
        if (command == "tray" && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaomiRevamp", "control", "pc-manager.off"))) return;
        if (commands.SequenceEqual(new[] { "--validate-ui" })) command = "validate-ui";
        if (commands.SequenceEqual(new[] { "--validate-suite" })) command = "validate-suite";
        if (commands.SequenceEqual(new[] { "--validate-screenoff" })) command = "validate-screenoff";
        if (XiaomiRevamp.Suite.SuiteEnvironment.Enabled && !TestMode && command is not ("register-startup" or "install-startup"))
        {
            foreach (string component in new[] { "file-search", "screen-translator" })
                if (XiaomiRevamp.Suite.SuiteEnvironment.Installed(component) && XiaomiRevamp.Suite.SuiteEnvironment.LegacyRunning(component))
                { MessageBox.Show("An original " + component + " copy is running. Quit the original app before opening Xiaomi Revamp, so shortcuts cannot compete.", "PC Manager"); return; }
        }
        if (command == "invalid")
        {
            MessageBox.Show("Use --toggle, --manager, --tray, or --monitor-small/medium/large. Add --test for separate settings and numbered Xiaomi OSDs.", "PC Manager");
            return;
        }
        // Service recovery only notifies an existing resident, never starts another hardware owner.
        if (command == "reapply-policies" && FindWindowW(null, PopupTitle) == IntPtr.Zero) return;
        string otherTitle = (TestMode ? "PC Manager" : "PC Manager · Test") + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? " · " + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "");
        if (FindWindowW(null, otherTitle) != IntPtr.Zero)
        {
            MessageBox.Show("The other daily/test resident is already controlling this laptop. Exit it from its tray before starting this instance, so charging and display policies cannot compete.", "PC Manager");
            return;
        }
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (command == "register-startup" && admin)
        {
            if (TestMode) { Environment.ExitCode = 1; return; }
            XiControl.SystemIntegration.AutoStart.Set(true);
            if (!XiControl.SystemIntegration.AutoStart.IsEnabled()) Environment.ExitCode = 1;
            return;
        }
        if (command == "install-startup" && admin)
        {
            if (TestMode) { MessageBox.Show("Startup registration is disabled for test mode."); return; }
            XiControl.SystemIntegration.AutoStart.Set(true);
            if (!XiControl.SystemIntegration.AutoStart.IsEnabled()) { MessageBox.Show("Windows refused startup registration."); return; }
            command = "tray";
        }
        // Only fixed, validated commands cross the privilege boundary. No paths or arbitrary code.
        IntPtr popup = FindWindowW(null, PopupTitle);
        if (popup != IntPtr.Zero && command is not ("install-startup" or "register-startup"))
        {
            if (command != "tray")
            {
                GetWindowThreadProcessId(popup, out uint processId);
                AllowSetForegroundWindow(processId);
                uint message = CommandMessage(command);
                if (!PostMessageW(popup, message, IntPtr.Zero, IntPtr.Zero))
                    MessageBox.Show("The running manager could not be activated. Open Quick controls from its tray icon.", "PC Manager");
            }
            return;
        }
        if (command is "quit" or "hide") return;
        if (!admin)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas", Arguments = (TestMode ? "--test " : "") + "--" + command,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("The hardware manager was not started. Firmware controls require administrator access.", "PC Manager");
            }
            return;
        }
        bool restart = false;
        using (var instance = new Mutex(true, "Local\\" + InstanceName, out bool first))
        {
            if (!first)
            {
                MessageBox.Show("The manager is starting. Its quick panel will be available from the system tray.", "PC Manager");
                return;
            }
            using var hardwareOwner = new Mutex(true, "Local\\XiaomiAIManager.Hardware." + WindowsIdentity.GetCurrent().User!.Value, out bool onlyOwner);
            if (!onlyOwner) { MessageBox.Show("A manager instance is starting or stopping. Only one daily/test hardware owner can run.", "PC Manager"); return; }
            using var app = new ManagerApplication(command);
            Application.Run(app);
            restart = app.RestartAfterImport;
            XiControl.Log.Write("Resident.MessageLoopEnded");
        }
        if (restart) Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, Arguments = (TestMode ? "--test " : "") + "--manager", WorkingDirectory = AppContext.BaseDirectory
        });
    }
    internal static uint CommandMessage(string command) => command switch
    {
        "manager" => MainWindow.ManagerMessage, "monitor-small" => MainWindow.MonitorSmallMessage,
        "monitor-medium" => MainWindow.MonitorMediumMessage, "monitor-large" => MainWindow.MonitorLargeMessage,
        "hide" => MainWindow.HideMessage, "quit" => MainWindow.QuitMessage, "cycle-mode" => MainWindow.CycleModeMessage,
        "brightness-up" => MainWindow.BrighterMessage, "brightness-down" => MainWindow.DimmerMessage,
        "toggle-travel" => MainWindow.TravelMessage, "verify-hardware" => MainWindow.VerifyMessage, "reapply-policies" => MainWindow.ReapplyMessage,
        "validate-controls" => MainWindow.ValidationMessage, "validate-oem" => MainWindow.OemValidationMessage, "validate-ui" => MainWindow.UiValidationMessage, "validate-suite" => MainWindow.SuiteValidationMessage, "validate-brightness" => MainWindow.BrightnessValidationMessage, "validate-screenoff" => MainWindow.ScreenOffValidationMessage, "screen-off" => MainWindow.ScreenOffMessage, _ => MainWindow.ToggleMessage
    };
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? className, string windowName);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}

