// Purpose: start one suite app using its private .NET runtime, without reusing another installation.
//          Also the per-app control switch used while testing:
//            "<App>.exe" --disable   stop the app and block every start (Windows startup, PC Manager, shortcuts, restart loop)
//            "<App>.exe" --enable    allow starts again (does not start the app)
//            "<App>.exe" --status    print enabled/disabled and running process ids; exit code 0 enabled, 3 disabled
//          From PowerShell, pipe the status so it waits for the answer: & "...\AI Center.exe" --status | Out-Host
// Dependencies: Windows .NET Framework 4.8. Outputs: owned desktop process; one per-user flag file
//          %LOCALAPPDATA%\XiaomiRevamp\control\<component>.off while disabled.
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
[assembly: AssemblyVersion("0.2.8.0")]
[assembly: AssemblyFileVersion("0.2.8.0")]
#elif FILE_SEARCH
[assembly: AssemblyVersion("0.3.5.0")]
[assembly: AssemblyFileVersion("0.3.5.0")]
#else
[assembly: AssemblyVersion("0.2.7.0")]
[assembly: AssemblyFileVersion("0.2.7.0")]
#endif

internal static class Launcher
{
#if PC_MANAGER
    const string Native = "PCManager.exe", Component = "pc-manager", Title = "PC Manager";
#elif FILE_SEARCH
    const string Native = "AI Center.exe", Component = "file-search", Title = "AI Center";
#else
    const string Native = "ScreenTranslator.dll", Component = "screen-translator", Title = "Screen Translator";
#endif
    static readonly string[] Control = { "--disable", "--enable", "--status" };
    static string Flag { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaomiRevamp", "control", Component + ".off"); } }
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int process);
    // Every process whose image lives in this app's folder, except this launcher itself.
    static Process[] Running(string root)
    {
        int self = Process.GetCurrentProcess().Id;
        return Process.GetProcesses().Where(process =>
        {
            try { return process.Id != self && process.MainModule.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase); }
            catch { return false; } // system and elevated processes cannot be inspected
        }).ToArray();
    }
    static int Status(string root)
    {
        bool disabled = File.Exists(Flag); var running = Running(root);
        string line = Title + ": " + (disabled ? "disabled" : "enabled") + ", " + (running.Length == 0 ? "not running" : "running (" + string.Join(", ", running.Select(process => process.ProcessName + "#" + process.Id)) + ")");
        // A redirected or piped stdout is written directly; otherwise borrow the calling console.
        if (!Console.IsOutputRedirected) AttachConsole(-1);
        Console.WriteLine(line);
        return disabled ? 3 : 0;
    }
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
    const string DesktopProfile = "--revamp-desktop-profile";
    static void LaunchFromDesktop(string executable,string arguments)
    {
        object windows=null,desktop=null,document=null,shell=null;
        try
        {
            // The inherited MSIX identity redirects AppData writes, splitting
            // shortcut queues from the normal Windows startup resident.
            windows=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
            object location=0,root=0;int hwnd;
            desktop=((dynamic)windows).FindWindowSW(ref location,ref root,8,out hwnd,1);
            if(desktop==null)throw new InvalidOperationException("Windows Explorer is unavailable.");
            document=((dynamic)desktop).Document;shell=((dynamic)document).Application;
            ((dynamic)shell).ShellExecute(executable,arguments,Path.GetDirectoryName(executable),"open",1);
        }
        finally {foreach(object value in new[]{shell,document,desktop,windows})if(value!=null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string home = AppDomain.CurrentDomain.BaseDirectory;
            if (args.Contains("--status")) return Status(home);
            if(!args.Contains(DesktopProfile))
            {
                // Inherited file redirection can exist even when Windows reports
                // APPMODEL_ERROR_NO_PACKAGE. Always use the actual desktop broker.
                LaunchFromDesktop(Process.GetCurrentProcess().MainModule.FileName,DesktopProfile+" "+string.Join(" ",args.Select(Quote)));
                return 0;
            }
            args=args.Where(value=>value!=DesktopProfile).ToArray();
            string root = home, runtime = Path.Combine(root, "runtime", "dotnet");
            if (args.Contains("--enable")) { File.Delete(Flag); return 0; }
            bool disable = args.Contains("--disable");
            if (disable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Flag));
                File.WriteAllText(Flag, DateTime.Now.ToString("o"));
                args = new[] { "--quit" }; // ask the running instance to close cleanly first
            }
            else if (File.Exists(Flag) && !args.Contains("--quit") && !args.Any(value => value.StartsWith("--check-", StringComparison.Ordinal)))
            {
                // Automatic starts stay silent; a person opening the app is told why nothing happens.
                if (!args.Contains("--tray")) MessageBox.Show(Title + " is disabled for testing.\n\nRun this to allow it again:\n\"" + Path.Combine(root, Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName)) + "\" --enable", "Xiaomi Revamp");
                return 0;
            }
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
                    if (process.ExitCode != 0 && !disable && !args.Any(value=>value.StartsWith("--check-",StringComparison.Ordinal))) { Thread.Sleep(5000); if (File.Exists(Flag)) return 0; start.Arguments = "--tray"; continue; }
#endif
                    if (disable)
                    {
                        // Give a clean shutdown ten seconds, then end whatever is left in this app's folder.
                        for (int wait = 0; wait < 40 && Running(root).Length > 0; wait++) Thread.Sleep(250);
                        foreach (var leftover in Running(root)) try { leftover.Kill(); } catch { }
                        return 0;
                    }
                    return process.ExitCode;
                }
            }
        }
        catch (Exception error) { MessageBox.Show(error.Message, "Xiaomi Revamp"); return 1; }
    }
}
