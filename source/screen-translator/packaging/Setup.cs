// Purpose: selectable-folder offline installer and scoped uninstaller for Screen Translator.
// Dependencies: Windows .NET Framework 4.x; setup needs adjacent payload.zip and payload.sha256.
// Outputs: selected app folder, current-user Start menu/uninstall/startup entries; keeps user data.
// Build: tools/build_app.py. Command: ScreenTranslator-Setup.exe [--quiet --destination path --no-register].
// Uninstall: Uninstall.exe [--quiet --no-register]. The installer never downloads or installs packages.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8",FrameworkDisplayName=".NET Framework 4.8")]
internal static class Setup
{
    const string Marker="packaged.json",Owned="installed-files.json",Key=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalScreenTranslator";
    static readonly string Base=AppDomain.CurrentDomain.BaseDirectory;
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int RegisterWindowMessage(string value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string type,string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,int message,IntPtr a,IntPtr b);
    static string Quote(string value){return "\""+value.Replace("\"","")+"\"";}
    static bool Admin(){return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}
    static string Destination(string path)
    {
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(full==Path.GetPathRoot(full).TrimEnd('\\')||full==Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)||full==Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)||full==Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)||full==Environment.GetFolderPath(Environment.SpecialFolder.Windows))throw new InvalidOperationException("Choose a dedicated application subfolder");
        for(var current=new DirectoryInfo(full);current!=null;current=current.Parent)if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Install folders must not contain junctions or symbolic links");
        return full;
    }
    static string Inside(string root,string relative)
    {
        if(Path.IsPathRooted(relative)||relative.Split('/','\\').Any(p=>p==".."||p==""))throw new InvalidDataException("Unsafe payload path");
        string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Payload escaped installation folder");
        for(var current=new DirectoryInfo(Path.GetDirectoryName(full));current!=null&&current.FullName.StartsWith(root,StringComparison.OrdinalIgnoreCase);current=current.Parent)if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Application files contain a junction");
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Application file is a symbolic link");
        return full;
    }
    static Dictionary<string,string> Files(string root)
    {
        var serializer=new JavaScriptSerializer{MaxJsonLength=20*1024*1024};return serializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Inside(root,Owned)));
    }
    static string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    static void StopApp(string root)
    {
        // Only request an orderly stop when the registered install is this exact folder.
        using(var key=Registry.CurrentUser.OpenSubKey(Key))if(String.Equals(key==null?null:key.GetValue("InstallLocation") as string,root,StringComparison.OrdinalIgnoreCase))
        {PostMessage(new IntPtr(0xffff),RegisterWindowMessage("LocalScreenTranslator.Quit"),IntPtr.Zero,IntPtr.Zero);System.Threading.Thread.Sleep(1500);}
    }
    static void Extract(string destination)
    {
        string root=Destination(destination),zip=Path.Combine(Base,"payload.zip");
        if(Hash(zip)!=File.ReadAllText(Path.Combine(Base,"payload.sha256")).Trim())throw new InvalidDataException("Installer payload checksum mismatch");
        if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidOperationException("Choose an empty dedicated folder. Existing installations must be uninstalled first; user settings are retained.");
        Directory.CreateDirectory(root);
        using(var archive=ZipFile.OpenRead(zip))
        {
            foreach(var entry in archive.Entries)
            {
                if(String.IsNullOrEmpty(entry.Name))continue;
                string path=Inside(root,entry.FullName);Directory.CreateDirectory(Path.GetDirectoryName(path));entry.ExtractToFile(path,false);
            }
        }
        if(!File.Exists(Inside(root,Marker))||!File.Exists(Inside(root,"ScreenTranslator.exe")))throw new InvalidDataException("Payload does not contain the application");
        foreach(var pair in Files(root))if(Hash(Inside(root,pair.Key))!=pair.Value)throw new InvalidDataException("Installed file checksum mismatch: "+pair.Key);
    }
    static string Shortcut(){return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Screen Translator.lnk");}
    static void Register(string root)
    {
        using(var key=Registry.CurrentUser.CreateSubKey(Key))
        {
            key.SetValue("DisplayName","Screen Translator");key.SetValue("DisplayVersion","0.1.0");key.SetValue("InstallLocation",root);key.SetValue("DisplayIcon",Path.Combine(root,"ScreenTranslator.exe"));key.SetValue("UninstallString",Quote(Path.Combine(root,"Uninstall.exe")));key.SetValue("NoModify",1);key.SetValue("NoRepair",1);
        }
        Type type=Type.GetTypeFromProgID("WScript.Shell");object shell=Activator.CreateInstance(type),link=type.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{Shortcut()});Type lt=link.GetType();
        lt.InvokeMember("TargetPath",BindingFlags.SetProperty,null,link,new object[]{Path.Combine(root,"ScreenTranslator.exe")});lt.InvokeMember("WorkingDirectory",BindingFlags.SetProperty,null,link,new object[]{root});lt.InvokeMember("IconLocation",BindingFlags.SetProperty,null,link,new object[]{Path.Combine(root,"ScreenTranslator.exe")});lt.InvokeMember("Save",BindingFlags.InvokeMethod,null,link,new object[0]);Marshal.FinalReleaseComObject(link);Marshal.FinalReleaseComObject(shell);
        using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))key.SetValue("LocalScreenTranslator",Quote(Path.Combine(root,"ScreenTranslator.exe"))+" --tray");
    }
    static int Install(string destination,bool register)
    {
        string root=Destination(destination);
        if(!Admin()&&root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)+"\\",StringComparison.OrdinalIgnoreCase))
        {
            var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--extract "+Quote(root)){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            using(var child=Process.Start(start)){child.WaitForExit();if(child.ExitCode!=0)return child.ExitCode;}
        }
        else Extract(root);
        if(register)Register(root);return 0;
    }
    static void Unregister(string root)
    {
        using(var key=Registry.CurrentUser.OpenSubKey(Key))if(!String.Equals(key==null?null:key.GetValue("InstallLocation") as string,root,StringComparison.OrdinalIgnoreCase))return;
        Registry.CurrentUser.DeleteSubKeyTree(Key,false);
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true))if(key!=null&&((key.GetValue("LocalScreenTranslator") as string)??"").StartsWith(Quote(Path.Combine(root,"ScreenTranslator.exe")),StringComparison.OrdinalIgnoreCase))key.DeleteValue("LocalScreenTranslator",false);
        string shortcut=Shortcut();if(File.Exists(shortcut))File.Delete(shortcut);
    }
    static void Remove(string destination)
    {
        string root=Destination(destination);if(!File.Exists(Inside(root,Marker)))throw new InvalidOperationException("This is not a Screen Translator installation");
        var files=Files(root);if(!files.ContainsKey("ScreenTranslator.exe")||!files.ContainsKey("Uninstall.exe"))throw new InvalidDataException("Incomplete installation ownership manifest");
        foreach(string relative in files.Keys){string file=Inside(root,relative);if(File.Exists(file))File.Delete(file);}
        File.Delete(Inside(root,Owned));
        // Only empty owned directories are removed. Unknown files and all user data are preserved.
        foreach(string dir in files.Keys.Select(p=>Path.GetDirectoryName(Inside(root,p))).Distinct().OrderByDescending(p=>p.Length))
        {var current=new DirectoryInfo(dir);while(current!=null&&current.FullName.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase)&&current.Exists&&!current.EnumerateFileSystemInfos().Any()){var parent=current.Parent;current.Delete();current=parent;}}
        if(Directory.Exists(root)&&!Directory.EnumerateFileSystemEntries(root).Any())Directory.Delete(root);
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling",false);AppContext.SetSwitch("Switch.System.IO.BlockLongPaths",false);
            Application.EnableVisualStyles();
#if UNINSTALL
            if(args.Length>=2&&args[0]=="--remove"){int parent; if(args.Length>2&&Int32.TryParse(args[2],out parent))try{Process.GetProcessById(parent).WaitForExit(10000);}catch(ArgumentException){}Remove(args[1]);return 0;}
            string root=Destination(Base);bool quiet=args.Contains("--quiet");
            if(!quiet&&MessageBox.Show("Uninstall Screen Translator? Your settings, logs and custom picture will be kept.","Screen Translator",MessageBoxButtons.OKCancel)!=DialogResult.OK)return 0;
            StopApp(root);
            string helper=Path.Combine(Path.GetTempPath(),"ScreenTranslator-Uninstall-"+Guid.NewGuid().ToString("N")+".exe");File.Copy(Assembly.GetExecutingAssembly().Location,helper,false);
            var start=new ProcessStartInfo(helper,"--remove "+Quote(root)+" "+Process.GetCurrentProcess().Id){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden};if(!Admin()&&root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)+"\\",StringComparison.OrdinalIgnoreCase))start.Verb="runas";
            Process.Start(start);if(!args.Contains("--no-register"))Unregister(root);return 0;
#else
            int at=Array.IndexOf(args,"--destination");if(args.Length==2&&args[0]=="--extract"){Extract(args[1]);return 0;}
            if(args.Contains("--quiet")){if(at<0||at+1>=args.Length)throw new ArgumentException("Supply --destination with --quiet");return Install(args[at+1],!args.Contains("--no-register"));}
            using(var form=new Form{Text="Screen Translator Setup",ClientSize=new Size(620,285),StartPosition=FormStartPosition.CenterScreen,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false})
            {
                var title=new Label{Text="Install Screen Translator",Font=new Font("Segoe UI",18),AutoSize=true,Location=new Point(24,22)};
                var info=new Label{Text="Offline models and runtimes are included. Choose a dedicated folder.\nProgram Files requires administrator approval. Settings stay in your profile.",Location=new Point(26,70),Size=new Size(565,50)};
                var path=new TextBox{Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Screen Translator"),Location=new Point(26,130),Width=450};
                var browse=new Button{Text="Browse",Location=new Point(485,128),Size=new Size(105,27)};
                var status=new Label{Text="",Location=new Point(26,176),Size=new Size(565,48)};
                var install=new Button{Text="Install",Location=new Point(375,235),Size=new Size(105,30)};var close=new Button{Text="Close",Location=new Point(485,235),Size=new Size(105,30)};
                browse.Click+=delegate{using(var picker=new FolderBrowserDialog{Description="Choose an empty dedicated application folder",SelectedPath=path.Text})if(picker.ShowDialog(form)==DialogResult.OK)path.Text=picker.SelectedPath;};close.Click+=delegate{form.Close();};
                install.Click+=async delegate
                {
                    string selected=path.Text;install.Enabled=browse.Enabled=path.Enabled=close.Enabled=false;status.Text="Verifying and extracting the offline runtime and models...";
                    try{int code=await Task.Run(()=>Install(selected,false));if(code!=0)throw new IOException("Installation did not finish ("+code+")");Register(Destination(selected));status.Text="Installed. Start menu and sign-in startup are ready.";close.Text="Finish";}
                    catch(Exception error){status.Text=error.Message;install.Enabled=browse.Enabled=path.Enabled=true;}finally{close.Enabled=true;}
                };
                form.Controls.AddRange(new Control[]{title,info,path,browse,status,install,close});Application.Run(form);
            }
            return 0;
#endif
        }
        catch(Exception error){try{File.AppendAllText(Path.Combine(Base,"setup-errors.log"),DateTime.UtcNow.ToString("O")+" "+error+Environment.NewLine);}catch(IOException){}catch(UnauthorizedAccessException){}if(args.Contains("--quiet")||args.Contains("--extract")||args.Contains("--remove"))Console.Error.WriteLine(error);else MessageBox.Show(error.Message,"Screen Translator setup");return 1;}
    }
}
