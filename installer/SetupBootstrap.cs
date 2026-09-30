// Purpose: unpack the embedded PC Manager payload and start the per-user PowerShell installer.
// Dependencies: Windows .NET Framework 4.x and Windows PowerShell; .NET 8/WebView2 are checked by Install.ps1.
// Outputs: a numbered installer cache under LocalAppData and, when run normally, the installed application.
// Command: PCManager-Setup.exe [--extract C:\path] (the optional command only extracts for inspection).
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class SetupBootstrap
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            bool extractOnly = args.Length == 2 && args[0] == "--extract";
            if (args.Length != 0 && !extractOnly) throw new ArgumentException("Use PCManager-Setup.exe or --extract followed by a destination folder.");
            string root = extractOnly ? Path.GetFullPath(args[1]) : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XiaomiAIManager", "installer-cache", DateTime.Now.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            WriteResource(root, "payload.zip");
            WriteResource(root, "Install.ps1");
            if (extractOnly) return 0;

            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell,
                "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(root, "Install.ps1") + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Windows PowerShell could not start.");
                process.WaitForExit();
                MessageBox.Show(process.ExitCode == 0
                    ? "PC Manager is installed and will start in the tray at sign-in. You can open it from Start."
                    : "PC Manager setup did not finish. Run the extracted Install.ps1 from a PowerShell window to see the error.",
                    "PC Manager Setup", MessageBoxButtons.OK,
                    process.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "PC Manager Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void WriteResource(string root, string name)
    {
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (input == null) throw new FileNotFoundException("The setup payload is missing: " + name);
            using (Stream output = File.Create(Path.Combine(root, name))) input.CopyTo(output);
        }
    }
}
