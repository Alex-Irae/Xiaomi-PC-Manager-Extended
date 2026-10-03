// Purpose: start one suite app using its private .NET runtime, without reusing another installation.
// Dependencies: Windows .NET Framework 4.8. Outputs: owned desktop process; no settings writes.
// Build: python tools/build_suite.py. Run the component EXE in install/<component>.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("Xiaomi Revamp")]
#if PC_MANAGER
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
#elif FILE_SEARCH
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
#else
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
#endif

internal static class Launcher
{
#if PC_MANAGER
    const string Native = "PCManager.exe";
#elif FILE_SEARCH
    const string Native = "AI Center.exe";
#else
    const string Native = "ScreenTranslator.dll";
#endif
    static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int process);
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory, runtime = Path.Combine(root, "runtime", "dotnet");
            string executable = Path.Combine(runtime, Native.EndsWith(".dll") ? "dotnet.exe" : Native);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            start.EnvironmentVariables["DOTNET_ROOT"] = runtime;
            start.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";
            string arguments = string.Join(" ", args.Select(Quote));
            start.Arguments = Native.EndsWith(".dll") ? Quote(Path.Combine(root, Native)) + " " + arguments : arguments;
            for (;;)
            {
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("Windows refused to start the application.");
                    AllowSetForegroundWindow(process.Id); process.WaitForExit();
#if FILE_SEARCH
                    if (process.ExitCode != 0) { Thread.Sleep(5000); start.Arguments = "--tray"; continue; }
#endif
                    return process.ExitCode;
                }
            }
        }
        catch (Exception error) { MessageBox.Show(error.Message, "Xiaomi Revamp"); return 1; }
    }
}
