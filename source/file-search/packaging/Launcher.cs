// Purpose: launch the GUI using the bundled .NET runtime, without a terminal.
// Dependencies: Windows .NET Framework 4.x; runtime/dotnet and AI Center.dll.
// Output: native UI process, restarted after abnormal exit; explicit Quit stops it.
// Build: packaging/build-release.ps1. Launch: "AI Center.exe" --tray.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
internal static class Launcher {
    internal static string Quote(string value) {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value) {
            if(c=='\\'){slashes++;continue;}
            result.Append('\\',c=='"'?slashes*2+1:slashes);slashes=0;result.Append(c);
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    [STAThread] static int Main(string[] args) {
        try {
            string root=AppDomain.CurrentDomain.BaseDirectory;
            string runtime=Path.Combine(root,"runtime","dotnet");
            // Process-local only: the branded .NET apphost uses the bundled runtime.
            Environment.SetEnvironmentVariable("DOTNET_ROOT_X64",runtime);
            Environment.SetEnvironmentVariable("DOTNET_ROOT",runtime);
            var start=new ProcessStartInfo(Path.Combine(runtime,"AI Center.exe"),string.Join(" ",args.Select(Quote)));
            start.WorkingDirectory=root;start.UseShellExecute=false;start.CreateNoWindow=true;
            // .NET 8 resolves frameworks beside this dotnet.exe. Avoid the .NET
            // Framework environment dictionary, which rejects duplicate Path/PATH.
            bool oneShot=args.Any(a=>a.StartsWith("--check-")||a=="--quit");
            while(true) {
                using(var process=Process.Start(start)) {
                    if(process==null)throw new InvalidOperationException("Could not start the bundled runtime.");
                    process.WaitForExit();if(oneShot||process.ExitCode==0)return process.ExitCode;
                }
                // No GPU worker or browser in this supervisor. Delay crash loops.
                Thread.Sleep(5000);
                start.Arguments=string.Join(" ",args.Where(a=>a!="--center"&&a!="--search"&&a!="--tray").Concat(new[]{"--tray"}).Select(Quote));
            }
        } catch(Exception error) {MessageBox.Show(error.Message,"AI Center launcher");return 1;}
    }
}
