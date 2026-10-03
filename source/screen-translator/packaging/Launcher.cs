// Purpose: start the C#/WebView2 host on its bundled .NET runtime, without a console.
// Dependencies: Windows .NET Framework 4.x and the adjacent runtime/dotnet files.
// Outputs: running native host. Build: tools/build_app.py. Command: ScreenTranslator.exe [--tray].
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8",FrameworkDisplayName=".NET Framework 4.8")]
internal static class Launcher
{
    internal static string Quote(string value)
    {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"'){result.Append('\\',slashes*2+1);result.Append(c);}else{result.Append('\\',slashes);result.Append(c);}slashes=0;}
        result.Append('\\',slashes*2);return result.Append('"').ToString();
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string root=AppDomain.CurrentDomain.BaseDirectory;
            var start=new ProcessStartInfo(Path.Combine(root,"runtime","dotnet","dotnet.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root};
            start.Arguments=Quote(Path.Combine(root,"ScreenTranslator.dll"));foreach(string arg in args)start.Arguments+=" "+Quote(arg);
            start.EnvironmentVariables["DOTNET_ROOT"]=Path.Combine(root,"runtime","dotnet");start.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"]="0";
            using(var child=Process.Start(start)){child.WaitForExit();return child.ExitCode;}
        }
        catch(Exception error){MessageBox.Show(error.Message,"Screen Translator could not start");return 1;}
    }
}
