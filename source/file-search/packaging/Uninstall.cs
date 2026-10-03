// Purpose: complete confirmed uninstall from an external copy, allowing self-removal.
// Dependencies: Windows .NET Framework 4.x, PowerShell, bundled Uninstall.ps1.
// Output: app/profile/integration removal. Build: packaging/build-release.ps1.
// Run: Uninstall AI Center.exe; --confirmed skips only the deletion confirmation.
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
internal static class Uninstall {
    [STAThread] static int Main(string[] args) {
        try {
            int rootIndex=Array.IndexOf(args,"--root");
            string root=rootIndex>=0?Path.GetFullPath(args[rootIndex+1]):AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if(rootIndex<0){
                if(Array.IndexOf(args,"--confirmed")<0&&MessageBox.Show("Remove AI Center from:\n"+root+"\n\nThis deletes its settings, profile pictures, search history and private index. Original documents and shared runtimes remain untouched.","Uninstall AI Center",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return 0;
                string cache=Path.Combine(Path.GetTempPath(),"AI-Center-Uninstall",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(cache);
                File.Copy(Path.Combine(root,"Uninstall.ps1"),Path.Combine(cache,"Uninstall.ps1"));
                string copy=Path.Combine(cache,"Uninstall.exe");File.Copy(Application.ExecutablePath,copy);
                bool machine=File.ReadAllText(Path.Combine(root,"install-scope.txt")).Trim()=="AllUsers";
                bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                var start=new ProcessStartInfo(copy,"--root \""+root+"\""){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden};
                if(machine&&!admin)start.Verb="runas";
                Process.Start(start);return 0;
            }
            string script=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Uninstall.ps1");
            var command=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+script+"\" -Target \""+root+"\""){UseShellExecute=false,CreateNoWindow=true};
            using(var process=Process.Start(command)){process.WaitForExit();if(process.ExitCode!=0)throw new IOException("Uninstall did not finish. Some app files or profiles may remain.");}
            MessageBox.Show("AI Center and its private data have been removed.","Uninstall AI Center");
            // The external helper removes its own three-file cache after this process exits.
            string cacheRoot=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            Guid cacheId;
            if(Path.GetDirectoryName(cacheRoot)==Path.Combine(Path.GetTempPath(),"AI-Center-Uninstall").TrimEnd('\\')&&Guid.TryParseExact(Path.GetFileName(cacheRoot),"N",out cacheId)){
                string literal=cacheRoot.Replace("'","''");
                string cleanup="while(Get-Process -Id "+Process.GetCurrentProcess().Id+" -ErrorAction SilentlyContinue){Start-Sleep -Milliseconds 250};foreach($name in 'Uninstall.exe','Uninstall.ps1'){Remove-Item -LiteralPath (Join-Path '"+literal+"' $name) -Force};Remove-Item -LiteralPath '"+literal+"' -Force";
                Process.Start(new ProcessStartInfo(command.FileName,"-NoProfile -Command \""+cleanup+"\""){UseShellExecute=false,CreateNoWindow=true});
            }
            return 0;
        }catch(Exception error){MessageBox.Show(error.Message,"Uninstall AI Center");return 1;}
    }
}
