// Purpose: offline setup with current-user, all-users and custom paths.
// Dependencies: Windows .NET Framework 4.x; embedded ZIP/script/checksum.
// Outputs: installed files, shortcuts and uninstall entry.
// Build: packaging/build-release.ps1. Run: AI-Center-Setup.exe [--install
// --scope User|AllUsers --target PATH --no-launch --no-integrations] or --extract PATH.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
internal static class Setup {
    static string Quote(string value) {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){if(c=='\\'){slashes++;continue;}result.Append('\\',c=='"'?slashes*2+1:slashes);slashes=0;result.Append(c);}
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    static bool Admin {get{return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}}
    static string Extract(string root) {
        Directory.CreateDirectory(root);
        foreach(string name in new[]{"payload.zip","Install.ps1","payload.sha256"}) {
            string path=Path.Combine(root,name);if(File.Exists(path))throw new IOException("Extraction destination already contains "+name);
            using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using(var output=File.Create(path)){if(input==null)throw new IOException("Missing setup resource: "+name);input.CopyTo(output);}
        }
        return root;
    }
    static int Install(string scope,string target,bool integrate) {
        if(scope=="AllUsers"&&!Admin) {
            var elevated=new ProcessStartInfo(Application.ExecutablePath,"--install --scope AllUsers --target "+Quote(target)+" --no-launch"+(integrate?"":" --no-integrations"));
            elevated.UseShellExecute=true;elevated.Verb="runas";
            using(var process=Process.Start(elevated)){process.WaitForExit();return process.ExitCode;}
        }
        string cache=Extract(Path.Combine(Path.GetTempPath(),"AI-Center-Setup",DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+"-"+Guid.NewGuid().ToString("N")));
        string log=Path.Combine(cache,"install.log");
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"),
            "-NoProfile -ExecutionPolicy Bypass -File "+Quote(Path.Combine(cache,"Install.ps1"))+" -Package "+Quote(Path.Combine(cache,"payload.zip"))+
            " -ExpectedHash "+Quote(File.ReadAllText(Path.Combine(cache,"payload.sha256")).Trim())+" -Scope "+scope+" -Target "+Quote(target)+(integrate?"":" -NoIntegrations"));
        start.UseShellExecute=false;start.CreateNoWindow=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
        using(var stream=new StreamWriter(log))
        using(var process=new Process()){
            process.StartInfo=start;
            process.OutputDataReceived+=(_,e)=>{if(e.Data!=null)lock(stream)stream.WriteLine(e.Data);};
            process.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)lock(stream)stream.WriteLine(e.Data);};
            process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();process.WaitForExit();
            if(process.ExitCode!=0)throw new IOException("Installation failed. Details: "+log);
        }
        return 0;
    }
    [STAThread] static int Main(string[] args) {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {
            if(args.Length==2&&args[0]=="--extract"){Extract(Path.GetFullPath(args[1]));return 0;}
            if(Array.IndexOf(args,"--install")>=0) {
                Func<string,string,string> option=(key,fallback)=>{int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback;};
                string scope=option("--scope","AllUsers");if(scope!="User"&&scope!="AllUsers")throw new ArgumentException("Scope must be User or AllUsers.");
                string target=Path.GetFullPath(option("--target",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Xiaomi Revamp","AI Center")));
                int code=Install(scope,target,Array.IndexOf(args,"--no-integrations")<0);
                if(code==0&&Array.IndexOf(args,"--no-launch")<0)Process.Start(Path.Combine(target,"AI Center.exe"),"--center");
                return code;
            }
            if(args.Length!=0)throw new ArgumentException("Unknown setup arguments.");
            var form=new Form{Text="AI Center Setup",ClientSize=new Size(520,450),StartPosition=FormStartPosition.CenterScreen,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,BackColor=Color.White,Font=new Font("Segoe UI",10)};
            var title=new Label{Text="Install AI Center",Location=new Point(28,24),AutoSize=true,Font=new Font("Segoe UI",19)};
            var description=new Label{Text="Local file search, meaning search and AI launchers.\nOffline setup. Each user keeps a private index.",Location=new Point(30,70),Size=new Size(460,52),ForeColor=Color.DimGray};
            var user=new RadioButton{Text="Only for me",Location=new Point(30,139),AutoSize=true};
            var all=new RadioButton{Text="For all users · Program Files",Location=new Point(30,176),AutoSize=true};
            var custom=new RadioButton{Text="Custom installation folder",Location=new Point(30,213),AutoSize=true};
            var path=new TextBox{Location=new Point(30,252),Size=new Size(366,28)};
            var browse=new Button{Text="Browse…",Location=new Point(405,250),Size=new Size(85,31)};
            var machine=new CheckBox{Text="Install custom location for all users",Location=new Point(30,291),AutoSize=true,Visible=false};
            var launch=new CheckBox{Text="Open AI Center after installation",Location=new Point(30,326),AutoSize=true,Checked=true};
            var status=new Label{Text="All-users setup requires administrator approval.",Location=new Point(30,365),Size=new Size(345,55),ForeColor=Color.DimGray};
            var button=new Button{Text="Install",Location=new Point(392,367),Size=new Size(98,40),BackColor=Color.FromArgb(52,130,255),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
            Action selection=()=>{
                machine.Visible=custom.Checked;path.ReadOnly=!custom.Checked;browse.Enabled=custom.Checked;
                if(user.Checked)path.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Local AI Center");
                if(all.Checked)path.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Xiaomi Revamp","AI Center");
            };
            user.CheckedChanged+=(_,e)=>selection();all.CheckedChanged+=(_,e)=>selection();custom.CheckedChanged+=(_,e)=>selection();
            browse.Click+=(_,e)=>{using(var dialog=new FolderBrowserDialog{Description="Choose a dedicated AI Center folder",SelectedPath=path.Text})if(dialog.ShowDialog(form)==DialogResult.OK)path.Text=dialog.SelectedPath;};
            button.Click+=async(_,e)=>{
                string target;
                try{target=Path.GetFullPath(path.Text);}catch(Exception error){MessageBox.Show(form,error.Message);return;}
                string scope=all.Checked||(custom.Checked&&machine.Checked)?"AllUsers":"User";
                button.Enabled=false;user.Enabled=all.Enabled=custom.Enabled=path.Enabled=browse.Enabled=machine.Enabled=false;status.Text="Verifying and installing local files…";
                try {
                    int result=await Task.Run(()=>Install(scope,target,true));
                    if(result!=0)throw new IOException("Administrator approval was declined or setup failed.");
                    if(launch.Checked)Process.Start(Path.Combine(target,"AI Center.exe"),"--center");form.Close();
                }catch(Exception error){
                    MessageBox.Show(form,error.Message,"AI Center Setup",MessageBoxButtons.OK,MessageBoxIcon.Error);
                    status.Text="Installation did not finish.";button.Enabled=true;user.Enabled=all.Enabled=custom.Enabled=path.Enabled=browse.Enabled=machine.Enabled=true;selection();
                }
            };
            form.Controls.AddRange(new Control[]{title,description,user,all,custom,path,browse,machine,launch,status,button});all.Checked=true;Application.Run(form);return 0;
        }catch(Exception error){
            string log=Path.Combine(Path.GetTempPath(),"AI-Center-Setup-error-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+".txt");
            File.WriteAllText(log,error.ToString());
            if(Array.IndexOf(args,"--install")<0)MessageBox.Show(error.Message+"\n"+log,"AI Center Setup");else Console.Error.WriteLine(error);
            return 1;
        }
    }
}
