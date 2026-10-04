// Purpose: native C# WebView2 search shell and separate AI Center window.
// Dependencies: .NET Desktop 8, existing WebView2 assemblies/runtime, Python backend.
// Outputs: UI, data/native.log; backend owns the index. No remote HTTP search API.
// Command: "native/bin/Release/net8.0-windows/AI Center.exe" --search (or --center).
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LocalAICenter;

internal static class Program
{
    internal const string Origin = "https://ai-center.local";
    internal static string Root = "";
    internal static string Data = "";
    internal static string Config = "";
    internal static bool Exiting;
    internal static bool InspectUi;
    internal static bool Development;
    internal static bool CheckLaunchRouting;
    internal static bool CheckReadySearch;
    internal static string InstanceTitle => "Local Search · AI Center" + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? " · " + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "");
    internal static void Log(string value) => File.AppendAllText(Path.Combine(Data,"native.log"), $"{DateTimeOffset.Now:O} {value}\n");

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            Root = FindRoot();
            if(XiaomiRevamp.Suite.SuiteEnvironment.Enabled && XiaomiRevamp.Suite.SuiteEnvironment.LegacyRunning("file-search")){MessageBox.Show("The original AI Center is running. Quit that copy before opening the connected edition, so both cannot handle the same shortcut.","AI Center");return;}
            InspectUi=args.Contains("--inspect-ui");
            CheckReadySearch=args.Contains("--check-ready-search");
            string Option(string name,string fallback){int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?Path.GetFullPath(args[index+1]):fallback;}
            bool packaged=File.Exists(Path.Combine(Root,"package-manifest.json"));
            Development=!packaged;
            string profile=XiaomiRevamp.Suite.SuiteEnvironment.Enabled?XiaomiRevamp.Suite.SuiteEnvironment.Data("file-search"):packaged?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalAICenter"):Root;
            Data = Option("--data",Path.Combine(profile,"data")); Config=Option("--config",Path.Combine(profile,"config.json"));Directory.CreateDirectory(Data);
            if(packaged&&!File.Exists(Config))
            {
                var defaults=JsonSerializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Root,"config.example.json")))!;
                defaults["model_path"]=Path.Combine(Root,"models","qwen3-embedding");
                defaults["excluded_folders"]=XiaomiRevamp.Suite.SuiteEnvironment.Enabled?new[]{XiaomiRevamp.Suite.SuiteEnvironment.Root,XiaomiRevamp.Suite.SuiteEnvironment.DataRoot,profile}:new[]{Root,profile};
                Directory.CreateDirectory(Path.GetDirectoryName(Config)!);File.WriteAllText(Config,JsonSerializer.Serialize(defaults,new JsonSerializerOptions {WriteIndented=true}));
            }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            SetCurrentProcessExplicitAppUserModelID("XiaomiRevamp.AICenter" + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? ".Suite." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : ""));
            if(args.Contains("--check-worker-activity"))
            {
                var checks=CenterContext.CheckWorkerActivity();
                File.WriteAllText(Path.Combine(Data,"worker-activity-check.json"),JsonSerializer.Serialize(new {passed=checks.Values.All(value=>value),checks},new JsonSerializerOptions {WriteIndented=true}));
                if(checks.Values.Any(value=>!value))Environment.ExitCode=1;
                return;
            }
            if(CheckReadySearch)
            {
                using var check=new CenterContext(false,false,true,true,true);
                async void RunReady(object? sender,EventArgs eventArgs)
                {
                    Application.Idle-=RunReady;
                    try{File.WriteAllText(Path.Combine(Data,"ready-search-check.json"),JsonSerializer.Serialize(await check.CheckReady(),new JsonSerializerOptions {WriteIndented=true}));}
                    catch(Exception error){Log(error.ToString());Environment.ExitCode=1;}
                    finally{check.Quit();}
                }
                Application.Idle+=RunReady;Application.Run(check);return;
            }
            if(args.Contains("--check-history")){File.WriteAllText(Path.Combine(Data,"history-check.json"),JsonSerializer.Serialize(SearchHistory.Check(Data),new JsonSerializerOptions {WriteIndented=true}));return;}
            if(args.Contains("--check-personalization"))
            {
                File.WriteAllText(Path.Combine(Data,"personalization-check.json"),JsonSerializer.Serialize(Personalization.Check(Data),new JsonSerializerOptions {WriteIndented=true}));return;
            }
            if(args.Contains("--check-frame"))
            {
                // Create native handles without showing windows, launching WebView or starting Python.
                try
                {
                    using var search=new SearchWindow(null!);_=search.Handle;
                    using var manager=new CenterWindow(null!);_=manager.Handle;
                    long style=NativeInput.GetWindowLongPtr(search.Handle,-20).ToInt64(),centerStyle=NativeInput.GetWindowLongPtr(manager.Handle,-16).ToInt64();
                    bool minimize=(centerStyle&0x20000)!=0,maximize=(centerStyle&0x10000)!=0,searchFrame=search.NativeFrameEnabled(),centerFrame=manager.NativeFrameEnabled();
                    int fullscreenBottomGap=Screen.FromControl(manager).Bounds.Bottom-manager.ExpandedBounds(true).Bottom;
                    int maximizedBottomGap=Screen.FromControl(manager).WorkingArea.Bottom-manager.ExpandedBounds(false).Bottom;
                    bool passed=(style&0x80)!=0&&(style&0x40000)==0&&!search.ShowInTaskbar&&search.Region is not null&&!search.Region.IsVisible(0,0)&&search.Region.IsVisible(search.Width/2,search.Height/2)&&manager.FormBorderStyle==FormBorderStyle.None&&manager.Region is not null&&!manager.Region.IsVisible(0,0)&&minimize&&maximize&&!searchFrame&&!centerFrame&&fullscreenBottomGap==2&&maximizedBottomGap==2;
                    File.WriteAllText(Path.Combine(Data,"frame-check.json"),JsonSerializer.Serialize(new {passed,fullscreenBottomGap,maximizedBottomGap,exStyle=$"0x{style:X}",centerStyle=$"0x{centerStyle:X}",minimize,maximize,searchNativeFrame=searchFrame,centerNativeFrame=centerFrame,search.ShowInTaskbar,searchCornerVisible=search.Region?.IsVisible(0,0),centerCornerVisible=manager.Region?.IsVisible(0,0),centerBorder=manager.FormBorderStyle.ToString(),windowsShown=false,backendStarted=false},new JsonSerializerOptions {WriteIndented=true}));
                    if(!passed)Environment.ExitCode=1;
                }
                catch(Exception error){Log(error.ToString());Environment.ExitCode=1;}
                return;
            }
            if(args.Contains("--check-index-lifetime"))
            {
                try{using var check=new CenterContext(false,false,true,true);File.WriteAllText(Path.Combine(Data,"index-lifetime-check.json"),JsonSerializer.Serialize(check.CheckIndexLifetime()));}
                catch(Exception error){Log(error.ToString());Environment.ExitCode=1;}return;
            }
            if(args.Contains("--check-lifecycle"))
            {
                try{using var check=new CenterContext(false,false,true,true);var report=check.CheckLifecycle();File.WriteAllText(Path.Combine(Data,"lifecycle-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));}
                catch(Exception error){Log(error.ToString());Environment.ExitCode=1;}
                return;
            }
            if(args.Contains("--check-resident")||args.Contains("--check-ux")||args.Contains("--check-search-ux"))
            {
                bool ux=args.Contains("--check-ux"),searchUX=args.Contains("--check-search-ux");
                using var check=new CenterContext(false,true,!ux,true);
                async void RunCheck(object? sender,EventArgs eventArgs)
                {
                    Application.Idle-=RunCheck;
                    try{var report=searchUX?await check.CheckSearchUX():ux?await check.CheckUX():await check.CheckResident();File.WriteAllText(Path.Combine(Data,searchUX?"search-ux-check.json":ux?"ux-check.json":"resident-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));}
                    catch(Exception error){Log(error.ToString());Environment.ExitCode=1;}
                    finally{check.Quit();}
                }
                Application.Idle+=RunCheck;Application.Run(check);return;
            }
            // Background activation must never request the settings window.
            CheckLaunchRouting=args.Contains("--check-launch-routing");
            bool center = args.Contains("--center") || (!args.Contains("--search") && !args.Contains("--tray"));
            using var mutex = new Mutex(true,"Local\\XiaomiSemanticSearchNative" + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? ".Suite." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : ""),out bool first);
            if (!first)
            {
                var handle = FindWindow(null,InstanceTitle);
                Log($"Existing instance command: search={!center}, quit={args.Contains("--quit")}, target={handle}");
                if (handle!=IntPtr.Zero && (center || args.Contains("--search") || args.Contains("--quit")))
                {
                    // A foreground launcher grants its resident instance activation rights.
                    NativeInput.GetWindowThreadProcessId(handle,out uint process);NativeInput.AllowSetForegroundWindow(process);
                    PostMessage(handle,0x8000+81,args.Contains("--quit")?new IntPtr(2):center?new IntPtr(1):IntPtr.Zero,IntPtr.Zero);
                }
                return;
            }
            if(args.Contains("--quit"))return;
            if(CheckLaunchRouting)
            {
                using var check=new CenterContext(false,true,true,headless:true,startHidden:true);
                async void RunRouting(object? sender,EventArgs e)
                {
                    Application.Idle-=RunRouting;
                    try{var report=await check.CheckLaunchCommands();File.WriteAllText(Path.Combine(Data,"launch-routing-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));}
                    catch(Exception error){Log(error.ToString());Environment.ExitCode=1;}
                    finally{check.Quit();}
                }
                Application.Idle+=RunRouting;Application.Run(check);mutex.ReleaseMutex();return;
            }
            using var app = new CenterContext(center,args.Contains("--paused"),args.Contains("--no-shortcut"),startHidden:args.Contains("--tray"));
            Application.Run(app);
            mutex.ReleaseMutex();
        }
        catch(Exception error)
        {
            if(Data.Length>0) Log(error.ToString());
            Environment.ExitCode=1;
            if(!args.Any(value=>value.StartsWith("--check-")))MessageBox.Show(error.Message,"AI Center could not start",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
    }

    static string FindRoot()
    {
        for(var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"config.example.json")) && Directory.Exists(Path.Combine(directory.FullName,"xiaomi_search"))) return directory.FullName;
        throw new DirectoryNotFoundException("Place the native host inside the xiaomi-semantic-search project.");
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string? cls,string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr first,IntPtr second);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

internal sealed partial class CenterContext : ApplicationContext
{
    internal readonly SearchWindow Search;
    Process? backend;
    readonly object writeLock=new();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer idle=new() {Interval=30000};
    readonly System.Windows.Forms.Timer maintenance=new() {Interval=60000};
    DateTime lastScheduled=DateTime.UtcNow;
    bool backgroundIndex;
    bool workerIndexing;
    readonly HashSet<string> indexingRequests=new();
    readonly HashSet<string> settingsRequests=new();
    bool IndexingActive=>backgroundIndex||workerIndexing||indexingRequests.Count>0;
    readonly HashSet<string> outstanding=new();
    readonly bool paused,noShortcut;
    readonly bool residentEnabled;
    JsonElement? readinessStatus;
    internal bool KeepSearchReady => residentEnabled && settings.GetProperty("model_standby").GetString()=="keep_loaded";
    CenterWindow? center;
    JsonElement settings;
    JsonElement? settingsHistory;
    Func<string,string,bool>? testFileLaunch;
    string? testBackupFolder;
    NativeInput? keyboard;
    string installedShortcut="";
    bool disposed;
    bool backendReady;
    int workerId;
    internal CenterContext(bool showCenter,bool paused,bool noShortcut,bool headless=false,bool startHidden=false)
    {
        this.paused=paused;this.noShortcut=noShortcut;
        residentEnabled=!headless||Program.CheckReadySearch;
        var defaults=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Program.Root,"config.example.json")))!;
        if(File.Exists(Program.Config))foreach(var entry in JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Program.Config))!)defaults[entry.Key]=entry.Value;
        settings=JsonSerializer.SerializeToElement(defaults);
        Search=new SearchWindow(this);_=Search.Handle;NativeInput.SetWindowText(Search.Handle,Program.InstanceTitle);
        if(!headless)ApplyStartup();
        tray=new NotifyIcon {Icon=Personalization.AppIcon,Text="AI Center",Visible=true,ContextMenuStrip=new ContextMenuStrip()};
        tray.ContextMenuStrip.Items.Add("AI Center",null,(_,_)=>ShowCenter());
        tray.ContextMenuStrip.Items.Add("File Search",null,(_,_)=>Search.ShowSearch());
        tray.ContextMenuStrip.Items.Add("Quit AI Center",null,(_,_)=>Quit());tray.DoubleClick+=(_,_)=>ShowCenter();
        idle.Tick+=(_,_)=>{if(!KeepSearchReady&&outstanding.Count==0&&!IndexingActive)StopBackend("30 seconds without a request");};
        string schedule=Path.Combine(Program.Data,"schedule.txt");
        if(File.Exists(schedule)&&DateTime.TryParse(File.ReadAllText(schedule),out var time))lastScheduled=time.ToUniversalTime();
        else if(!headless)File.WriteAllText(schedule,lastScheduled.ToString("O"));
        maintenance.Tick+=(_,_)=>ScheduleIndex();if(!headless){maintenance.Start();SystemEvents.SessionSwitch+=SessionChanged;SystemEvents.UserPreferenceChanged+=WindowsThemeChanged;}
        // Hidden startup must own shortcuts before the first command arrives.
        // Application.Idle can be delayed, leaving a tray-only app unresponsive.
        InstallShortcut();
        if(KeepSearchReady)Post(()=>{EnsureBackend();Search.Prepare();});
        if(!headless&&!startHidden){if(showCenter)ShowCenter();else Search.ShowSearch();}
    }
    void InstallShortcut()
    {
        if (XiaomiRevamp.Suite.SuiteEnvironment.Enabled && !noShortcut) { InstallSuite(); return; }
        string shortcut=noShortcut?"none":settings.TryGetProperty("shortcut",out var value)?value.GetString()??"double_ctrl":"double_ctrl";
        int interval=settings.TryGetProperty("double_ctrl_ms",out var milliseconds)?milliseconds.GetInt32():450;
        string signature=shortcut+":"+interval;
        if(keyboard is not null&&installedShortcut==signature)return;
        keyboard?.Dispose();keyboard=new NativeInput(shortcut,interval,()=>Post(()=>Search.ShowSearch()));
        installedShortcut=signature;
        Program.Log("Shortcut listener installed: "+shortcut);
    }
    void ApplyStartup(){if(!Program.Development)Personalization.Startup(!settings.TryGetProperty("run_at_startup",out var value)||value.GetBoolean());}
    void SessionChanged(object sender,SessionSwitchEventArgs eventArgs)
    {
        if(eventArgs.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)Post(()=>{installedShortcut="";InstallShortcut();});
    }
    bool WindowsDark=>Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1))==0;
    void WindowsThemeChanged(object sender,UserPreferenceChangedEventArgs args)=>Post(BroadcastTheme);
    void BroadcastTheme(){var message=JsonSerializer.SerializeToElement(new {kind="windows_theme",dark=WindowsDark});Search.Receive(message);center?.Receive(message);}
    void ReadScope()
    {
        var current=settings.Deserialize<Dictionary<string,JsonElement>>()!;
        foreach(var (key,name) in new[]{("roots","included-folders.txt"),("excluded_folders","excluded-folders.txt")})
        {
            string path=Path.Combine(Path.GetDirectoryName(Program.Config)!,name);
            if(!File.Exists(path))File.WriteAllLines(path,current[key].Deserialize<string[]>()!);
            string text=File.ReadAllText(path);
            if(text.StartsWith("#")&&text.Contains(@"\r\n")){text=text.Replace(@"\r\n","\n");File.WriteAllText(path,text);}
            var paths=text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Trim()).Where(line=>line.Length>0&&!line.StartsWith("#")).ToArray();
            if(paths.Any(line=>!Path.IsPathFullyQualified(line)))throw new ArgumentException("Folder lists must contain absolute paths, one per line.");
            current[key]=JsonSerializer.SerializeToElement(paths);
        }
        settings=JsonSerializer.SerializeToElement(current);
    }
    internal void CenterVisibility()=>tray.Visible=!Program.Exiting;
    bool ActiveWindow=>Search.Visible||(center?.Visible==true&&center.WindowState!=FormWindowState.Minimized);
    internal Color WindowColor
    {
        get{string theme=settings.TryGetProperty("theme",out var value)?value.GetString()??"system":"system";bool dark=theme=="dark"||(theme=="system"&&Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1))==0);return dark?Color.FromArgb(23,26,31):Color.White;}
    }
    void ScheduleIndex()
    {
        if(KeepSearchReady && (backend is null||backend.HasExited))EnsureBackend();
        if(backgroundIndex&&settings.GetProperty("indexing_mode").GetString()=="battery_saver"&&SystemInformation.PowerStatus.PowerLineStatus==PowerLineStatus.Offline){StopBackend("scheduled indexing paused on battery");return;}
        if(IndexingActive||(backend is not null&&!KeepSearchReady)||!settings.TryGetProperty("indexing_frequency",out var frequency))return;
        int minutes=frequency.GetString() switch {"realtime"=>5,"5_minutes"=>5,"15_minutes"=>15,"hourly"=>60,"daily"=>1440,_=>0};
        string mode=settings.GetProperty("indexing_mode").GetString()!;
        if(minutes==0||mode=="paused"||DateTime.UtcNow-lastScheduled<TimeSpan.FromMinutes(minutes)||(mode=="battery_saver"&&SystemInformation.PowerStatus.PowerLineStatus==PowerLineStatus.Offline))return;
        EnsureBackend();backgroundIndex=true;outstanding.Add("maintenance:987654321");
        Write(new {owner="maintenance",mode=0,data=new {id=987654321,persistent=false,request=new {method="local_index_now",@params=new {force=false}}}});
    }
    void Touch(){idle.Stop();idle.Start();}
    void EnsureBackend()
    {
        if(backend is not null&&!backend.HasExited)return;
        backend?.Dispose();backendReady=false;workerId=0;
        string python=Path.Combine(Program.Root,"runtime","python","python.exe");
        if(!File.Exists(python))python=Path.Combine(Program.Root,".venv","Scripts","python.exe");
        var start=new ProcessStartInfo(python)
        {WorkingDirectory=Program.Root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        start.ArgumentList.Add("-m");start.ArgumentList.Add("xiaomi_search.backend");
        start.ArgumentList.Add("--config");start.ArgumentList.Add(Program.Config);start.ArgumentList.Add("--data");start.ArgumentList.Add(Program.Data);
        if(paused)start.ArgumentList.Add("--paused");if(noShortcut)start.ArgumentList.Add("--no-shortcut");start.Environment["PYTHONUTF8"]="1";start.Environment["PYTHONDONTWRITEBYTECODE"]="1";
        var process=Process.Start(start)??throw new InvalidOperationException("Local backend failed to start.");backend=process;
        Program.Log($"Owned backend started: {process.Id}");process.EnableRaisingEvents=true;
        process.Exited+=(_,_)=>Post(()=>{if(ReferenceEquals(backend,process)&&!Program.Exiting){Program.Log("Search backend exited unexpectedly");backend=null;NotifyStopped();process.Dispose();}});
        _=Task.Run(async()=>
        {
            try{while(await process.StandardOutput.ReadLineAsync() is string line){try{using var document=JsonDocument.Parse(line);var message=document.RootElement.Clone();Post(()=>{if(ReferenceEquals(backend,process))Receive(message);});}catch(JsonException){Program.Log("Non-RPC backend output");}}}
            catch(Exception error) when(error is IOException or ObjectDisposedException){if(ReferenceEquals(backend,process))Program.Log(error.Message);}
        });
        _=Task.Run(async()=>{try{while(await process.StandardError.ReadLineAsync() is string line)Program.Log(line);}catch(Exception error) when(error is IOException or ObjectDisposedException){}});
    }
    void NotifyStopped()
    {
        outstanding.Clear();indexingRequests.Clear();settingsRequests.Clear();workerIndexing=false;var message=JsonSerializer.SerializeToElement(new {kind="backend_stopped"});Search.Receive(message);center?.Receive(message);
    }
    void StopBackend(string reason)
    {
        idle.Stop();backgroundIndex=false;var process=backend;backend=null;if(process is null)return;
        // Allow the protected snapshot to finish before releasing the owned worker.
        try{if(!process.HasExited){Program.Log($"Owned backend stopped: {process.Id}; {reason}");bool finished=false;if(outstanding.Count==0){process.StandardInput.WriteLine("{\"kind\":\"shutdown\"}");process.StandardInput.Flush();finished=process.WaitForExit(15000);}if(!finished){process.Kill(entireProcessTree:true);process.WaitForExit(1000);}}}
        catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception error){Program.Log(error.Message);}
        finally{process.Dispose();NotifyStopped();}
    }
    internal void CheckUnused(){if(!KeepSearchReady&&!ActiveWindow&&!IndexingActive&&settingsRequests.Count==0)StopBackend("all application windows dismissed");}
    internal void DismissSearch()=>CheckUnused();
    internal async Task<object> CheckReady()
    {
        async Task Wait(Func<bool> condition,int timeout=120000){var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<timeout)await Task.Delay(25);if(!condition())throw new TimeoutException("Ready-search check timed out");}
        async Task<JsonElement> Status()
        {
            readinessStatus=null;Write(new {owner="readiness",mode=0,data=new {id=901,request=new {method="local_status",@params=new {}}}});
            await Wait(()=>readinessStatus is not null);return readinessStatus!.Value;
        }
        await Wait(()=>backendReady&&Search.PageReady);
        JsonElement state=await Status();
        var warm=Stopwatch.StartNew();while(!state.GetProperty("model").GetProperty("loaded").GetBoolean()&&warm.ElapsedMilliseconds<90000){await Task.Delay(100);state=await Status();}
        if(!state.GetProperty("model").GetProperty("loaded").GetBoolean())throw new InvalidOperationException("Resident embedding model did not load");
        int pid=backend!.Id;var firstBrowser=Search.BrowserIdentity;
        var open=Stopwatch.StartNew();Search.ShowSearch();
        if(await Search.Evaluate("document.activeElement===document.getElementById('query')")!="true")throw new InvalidOperationException("Search input was not focused");
        double firstMs=open.Elapsed.TotalMilliseconds;
        Search.Hide();DismissSearch();idle.Interval=100;Touch();await Task.Delay(6200);
        if(backend?.Id!=pid||!Search.PageReady||!ReferenceEquals(firstBrowser,Search.BrowserIdentity))throw new InvalidOperationException("Dismissal released resident search resources");
        state=await Status();
        if(!state.GetProperty("model").GetProperty("loaded").GetBoolean()||state.GetProperty("indexer").GetProperty("mode").GetString()=="paused")throw new InvalidOperationException("Index/model paused or unloaded");
        open.Restart();Search.ShowSearch();
        if(await Search.Evaluate("document.activeElement===document.getElementById('query')")!="true")throw new InvalidOperationException("Repeated search did not focus immediately");
        double secondMs=open.Elapsed.TotalMilliseconds;
        return new {passed=true,backendPid=pid,backendRetained=true,searchBrowserRetained=true,embeddingModelRetained=true,mainCenterOpened=center?.Visible==true,firstOpenMilliseconds=firstMs,repeatedOpenMilliseconds=secondMs,status=state};
    }
    internal object CheckIndexLifetime()
    {
        void Pump(Func<bool> condition,int timeout=30000){var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<timeout){Application.DoEvents();Thread.Sleep(10);}if(!condition())throw new TimeoutException("Index lifetime check timed out");}
        EnsureBackend();Pump(()=>backendReady);
        Write(new {owner="test",mode=0,data=new {id=701,request=new {method="local_scan",@params=new {}}}});
        Pump(()=>workerIndexing);int parent=backend!.Id;
        DismissSearch();CheckUnused();idle.Interval=200;Touch();
        var wait=Stopwatch.StartNew();Pump(()=>wait.ElapsedMilliseconds>=700);
        if(backend is null||backend.Id!=parent)throw new InvalidOperationException("Hidden/idle UI interrupted queued indexing");
        Write(new {owner="test",mode=0,data=new {id=702,request=new {method="local_mode",@params=new {mode="normal"}}}});
        Pump(()=>backend is null);
        return new {passed=true,queuedIndexSurvivedDismissal=true,queuedIndexSurvivedIdle=true,workerReleasedAfterCompletion=true,parent};
    }
    internal object CheckLifecycle()
    {
        if(backend is not null)throw new InvalidOperationException("Blank launch started Python");
        Send(JsonSerializer.SerializeToElement(new {mode=0,data=new {id=1,request=new {method="local_config",@params=new {}}}}),"search");
        if(backend is not null)throw new InvalidOperationException("Appearance request started Python");
        void Pump(Func<bool> condition,int timeout=10000){var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<timeout){Application.DoEvents();Thread.Sleep(10);}if(!condition())throw new TimeoutException("Lifecycle check timed out");}
        bool Gone(int id){try{using var process=Process.GetProcessById(id);return process.HasExited;}catch(ArgumentException){return true;}}
        (int parent,int worker) Start(){EnsureBackend();Pump(()=>backendReady);return(backend!.Id,workerId);}
        var first=Start();DismissSearch();Pump(()=>Gone(first.parent)&&Gone(first.worker));
        Send(JsonSerializer.SerializeToElement(new {mode=0,data=new {id=2,request=new {method="window_resize",@params=new {width=700,height=78}}}}),"search");
        if(backend is not null)throw new InvalidOperationException("Hidden resize restarted Python");
        var second=Start();CheckUnused();Pump(()=>Gone(second.parent)&&Gone(second.worker));
        var third=Start();idle.Interval=200;Touch();Pump(()=>backend is null);Pump(()=>Gone(third.parent)&&Gone(third.worker));
        byte[] history=Encoding.UTF8.GetBytes("[\"fixture history\"]");var encrypted=AccountProtection.Crypt(history,false);if(encrypted.SequenceEqual(history)||!AccountProtection.Crypt(encrypted,true).SequenceEqual(history))throw new InvalidOperationException("History protection failed");
        var scheduledSettings=JsonSerializer.Deserialize<Dictionary<string,object>>(settings.GetRawText())!;scheduledSettings["indexing_frequency"]="realtime";scheduledSettings["indexing_mode"]="normal";settings=JsonSerializer.SerializeToElement(scheduledSettings);lastScheduled=DateTime.UtcNow-TimeSpan.FromMinutes(4);
        ScheduleIndex();if(backend is not null)throw new InvalidOperationException("Schedule ran before five minutes");lastScheduled=DateTime.UtcNow-TimeSpan.FromMinutes(6);
        ScheduleIndex();int scheduledParent=backend?.Id??throw new InvalidOperationException("Schedule did not start a worker");Pump(()=>backend is null,30000);int scheduledWorker=workerId;Pump(()=>Gone(scheduledParent)&&Gone(scheduledWorker));
        return new {passed=true,windowsShown=false,modelLoaded=false,blankLaunchStartedPython=false,appearanceStartedPython=false,hiddenResizeStartedPython=false,historyProtected=true,scheduledIndexStopped=true,scheduledIntervalMinutes=5,scheduledParent,scheduledWorker,firstParent=first.parent,firstWorker=first.worker,restartedParent=second.parent,restartedWorker=second.worker,idleParent=third.parent,idleWorker=third.worker,dismissalStopped=true,hiddenStopped=true,idleStopped=true,productionIdleMilliseconds=30000,testIdleMilliseconds=200};
    }
    internal async Task<object> CheckResident()
    {
        async Task Wait(Func<bool> condition,int timeout=15000){var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<timeout)await Task.Delay(50);if(!condition())throw new TimeoutException("Resident check timed out");}
        if(!tray.Visible||Search.BrowserLoaded)throw new InvalidOperationException("Hidden startup must have a tray icon without a WebView");
        ShowCenter();await Wait(()=>center!.PageReady);if(!tray.Visible)throw new InvalidOperationException("Center has no tray icon");
        if(!await center!.CheckAccent(new[]{new[]{".brand","backgroundColor"},new[]{".center-tabs .active","color"},new[]{".tool-icon","color"},new[]{".primary","backgroundColor"},new[]{".secondary","color"}}))throw new InvalidOperationException("Center accent did not propagate");
        center!.WindowState=FormWindowState.Minimized;await Task.Delay(100);if(!tray.Visible)throw new InvalidOperationException("Minimized center lost tray icon");
        center.WindowState=FormWindowState.Normal;
        center.Hide();await Wait(()=>!center.BrowserLoaded);if(!tray.Visible)throw new InvalidOperationException("Hidden center lost tray icon");
        Search.ShowSearch();await Wait(()=>Search.PageReady);if(!tray.Visible)throw new InvalidOperationException("Search lost tray icon");
        if(!await Search.CheckAccent(new[]{new[]{".submit","backgroundColor"},new[]{"nav .active","color"}}))throw new InvalidOperationException("Search accent did not propagate");
        Search.Hide();await Wait(()=>!Search.BrowserLoaded);
        Search.ShowSearch();await Wait(()=>Search.PageReady);if(!tray.Visible)throw new InvalidOperationException("Recreated search lost tray icon");
        Search.Hide();await Wait(()=>!Search.BrowserLoaded);
        return new {passed=true,hiddenStartupLoadedBrowser=false,centerTrayVisible=true,minimizedTrayVisible=true,hiddenCenterTrayVisible=true,searchTrayVisible=true,centerBrowserReleased=true,searchBrowserReleased=true,searchBrowserRecreated=true,centerAccentChanges=true,searchAccentChanges=true,releaseAfterMilliseconds=5000};
    }
    internal async Task<object> CheckUX()
    {
        async Task Wait(Func<Task<bool>> condition){var watch=Stopwatch.StartNew();while(!await condition()&&watch.ElapsedMilliseconds<20000)await Task.Delay(50);if(!await condition())throw new TimeoutException("UX check timed out");}
        async Task WaitPage(WebWindow window)=>await Wait(()=>Task.FromResult(window.PageReady));
        bool Saved(string key,string value){if(!File.Exists(Program.Config))return false;using var stream=new FileStream(Program.Config,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var document=JsonDocument.Parse(stream);return document.RootElement.GetProperty(key).GetString()==value;}
        ControlTap.Check();if(keyboard?.SeparateMessageLoop!=true)throw new InvalidOperationException("Shortcut hook shares the UI loop");
        ShowCenter();await WaitPage(center!);
        async Task Theme(string theme,bool dark)
        {
            Program.Log("UX: theme "+theme);
            await center!.Evaluate("(()=>{const input=document.getElementById('theme');input.value="+JsonSerializer.Serialize(theme)+";input.dispatchEvent(new Event('change'));})()");
            await Wait(()=>Task.FromResult(Saved("theme",theme)));
            await Wait(async()=>await center.Evaluate("document.documentElement.dataset.theme==="+JsonSerializer.Serialize(dark?"dark":"light"))=="true");
        }
        await Theme("light",false);await Theme("dark",true);
        await center!.Evaluate("document.getElementById('settings-undo').click()");await Wait(()=>Task.FromResult(Saved("theme","light")));
        await center.Evaluate("document.getElementById('settings-redo').click()");await Wait(()=>Task.FromResult(Saved("theme","dark")));
        await Theme("system",WindowsDark);
        await center.Evaluate("document.getElementById('scan-pc').click()");
        await Wait(()=>Task.FromResult(settings.GetProperty("roots").GetArrayLength()>0));
        if(IndexingActive)throw new InvalidOperationException("Whole-PC scope button started indexing");
        await center.Evaluate("document.getElementById('path-manage').click()");
        if(await center.Evaluate("document.getElementById('paths-dialog').open&&document.querySelectorAll('.path-row').length>0")!="true")throw new InvalidOperationException("Path editor failed");
        await center.Evaluate("document.querySelector('.path-row button').click();document.getElementById('paths-undo').click();document.getElementById('paths-cancel').click()");
        // Both edits are sent before the browser closes. Neither may be lost.
        await center.Evaluate("(()=>{for(const [id,value] of [['theme','light'],['accent_color','#a347dc']]){const input=document.getElementById(id);input.value=value;input.dispatchEvent(new Event('change'));}})()");
        center.Hide();await Wait(()=>Task.FromResult(Saved("theme","light")&&Saved("accent_color","#a347dc")&&settingsRequests.Count==0));
        Program.Log("UX: preferences persisted after immediate close");
        await Wait(()=>Task.FromResult(!center.BrowserLoaded));
        ShowCenter();await WaitPage(center);
        await Wait(async()=>await center.Evaluate("!document.getElementById('settings-undo').disabled")=="true");
        Program.Log("UX: settings history survived browser disposal");
        center.Hide();
        Search.ShowSearch();await WaitPage(Search);
        using var outside=new Form {Text="AI Center focus fixture",Size=new Size(220,100),ShowInTaskbar=false};
        outside.Show();outside.Activate();await Wait(()=>Task.FromResult(!Search.Visible));outside.Close();
        Program.Log("UX: search dismissed on foreign window activation");
        return new {passed=true,light=true,dark=true,windowsTheme=true,undoRedo=true,historySurvivesBrowserDisposal=true,scopeDoesNotStartIndex=true,pathsDialog=true,rapidEditsSurviveClose=true,focusLossDismisses=true,separateShortcutLoop=true,doubleCtrlGesture=true};
    }
    internal async Task<object> CheckSearchUX()
    {
        async Task Wait(Func<Task<bool>> condition){var watch=Stopwatch.StartNew();while(!await condition()&&watch.ElapsedMilliseconds<20000)await Task.Delay(50);if(!await condition())throw new TimeoutException("Search UX check timed out");}
        async Task Page(WebWindow window)=>await Wait(()=>Task.FromResult(window.PageReady));
        var launches=new List<(string action,string path)>();testFileLaunch=(action,path)=>{launches.Add((action,path));return true;};
        ShowCenter();await Page(center!);
        await center!.Evaluate("document.querySelector('[data-page=preferences]').click()");
        await Wait(async()=>await center!.Evaluate("document.querySelectorAll('#paths-summary .scope-path').length===4&&!!document.querySelector('.paths-more')")=="true");
        await center.Evaluate("document.getElementById('paths-summary').scrollIntoView({block:'center'})");await center.SavePreview("included-folders.png");
        await center!.Evaluate("document.querySelector('.paths-more').click()");
        if(await center.Evaluate("document.querySelectorAll('#paths-summary .scope-path').length===5")!="true")throw new Exception("Included paths did not expand");
        await center.Evaluate("document.getElementById('paths-exclude').click()");
        if(await center.Evaluate("document.querySelectorAll('#paths-summary .scope-path').length===4&&!!document.querySelector('.paths-more')")!="true")throw new Exception("Excluded rows failed");
        await center.SavePreview("excluded-folders.png");
        await center.Evaluate("localBridge.call('local_index_now',{force:true,wait:false}).then(()=>window.fixtureIndexReady=true)");
        await Wait(async()=>await center.Evaluate("window.fixtureIndexReady===true")=="true");
        await Wait(()=>Task.FromResult(!IndexingActive));
        testBackupFolder=Path.GetFullPath(Path.Combine(Program.Data,"..","..","Backups"));Directory.CreateDirectory(testBackupFolder);
        if(await center.Evaluate("document.getElementById('index-location').textContent.endsWith('index.sqlite3.dpapi')")!="true")throw new Exception("Index path was not displayed");
        await center.Evaluate("document.getElementById('backup-index').click()");
        await Wait(async()=>await center.Evaluate("document.getElementById('index-backup-status').textContent.startsWith('Backup complete')&&!document.getElementById('backup-index').disabled")=="true");
        if(Directory.GetFiles(testBackupFolder,"manifest.json",SearchOption.AllDirectories).Length!=1)throw new Exception("Backup manifest was not published");
        await center.Evaluate("document.getElementById('index-backup-title').scrollIntoView({block:'start'})");await center.SavePreview("index-backup.png");
        testBackupFolder=null;
        Search.ShowSearch();await Page(Search);
        async Task Query()=>await Search.Evaluate("(()=>{const q=document.getElementById('query');q.value='efs';q.dispatchEvent(new Event('input'));})()");
        await Query();await Wait(async()=>await Search.Evaluate("document.querySelector('.result .name')?.textContent==='efs.txt'")=="true");
        await Search.Evaluate("(()=>{const row=[...document.querySelectorAll('.result')].find(r=>r.querySelector('.name').textContent==='chosen.md');row.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:120,clientY:120}));})()");
        if(await Search.Evaluate("!document.getElementById('result-menu').hidden&&!document.getElementById('menu-open-with').disabled")!="true")throw new Exception("Result menu failed");
        await Search.SavePreview("result-menu.png");
        await Search.Evaluate("document.getElementById('menu-open').click()");
        await Wait(()=>Task.FromResult(launches.Any(item=>item.action=="open"&&Path.GetFileName(item.path)=="chosen.md")&&new SearchHistory(Program.Data).Preferred("efs").Length==1));
        Search.Hide();await Wait(()=>Task.FromResult(!Search.BrowserLoaded));
        Search.ShowSearch();await Page(Search);await Query();
        await Wait(async()=>await Search.Evaluate("document.querySelector('.result .name')?.textContent==='chosen.md'&&!document.getElementById('history').hidden")=="true");
        var launchesBeforeEnter=launches.Count;
        await Query();await Search.Evaluate("document.getElementById('query').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}))");
        await Wait(async()=>await Search.Evaluate("document.querySelector('.result .name')?.textContent==='chosen.md'")=="true");
        await Task.Delay(300);
        if(launches.Count!=launchesBeforeEnter)throw new Exception("Enter opened a document instead of searching");
        await Search.Evaluate("document.querySelector('.result').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:120,clientY:120}));document.getElementById('menu-open-with').click()");
        await Wait(()=>Task.FromResult(launches.Any(item=>item.action=="open_with")));
        await Search.Evaluate("document.querySelector('.result').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:120,clientY:120}));document.getElementById('menu-reveal').click()");
        await Wait(()=>Task.FromResult(launches.Any(item=>item.action=="reveal")));
        await Search.Evaluate("document.querySelector('.history-remove').click()");
        await Wait(async()=>await Search.Evaluate("document.querySelector('.result .name')?.textContent==='efs.txt'&&document.getElementById('history').hidden")=="true");
        await Task.Delay(1000);
        if(new SearchHistory(Program.Data).Read().Queries.Length!=0)throw new Exception("Removed history was resaved");
        await Search.Evaluate("(()=>{const q=document.getElementById('query');q.value='efs md';q.dispatchEvent(new Event('input'));})()");
        await Wait(async()=>await Search.Evaluate("document.getElementById('file-type').value==='md'&&document.querySelectorAll('.result').length>0&&[...document.querySelectorAll('.result .name')].every(n=>n.textContent.endsWith('.md'))")=="true");
        await Search.SavePreview("extension-filter.png");
        return new {passed=true,includedRows=true,excludedRows=true,expandAboveFour=true,contextMenu=true,open=true,openWith=true,showInExplorer=true,choiceSurvivesBrowserDisposal=true,exactHistoryVisible=true,repeatQueryFavoriteFirst=true,enterSearchDoesNotOpenFile=true,exactExtensionFilter=true,removeForgetsChoice=true,removedQueryStaysRemoved=true,externalAppsLaunched=false,openWithDialogVisuallyTested=false};
    }
    internal void Post(Action action){if(!Search.IsDisposed&&Search.IsHandleCreated)Search.BeginInvoke(action);}
    internal void Send(JsonElement message,string owner)
    {
        if(!message.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Object)return;
        JsonElement request=default;
        string method=data.TryGetProperty("request",out request)?request.GetProperty("method").GetString()??"":"";
        void Reply(object? result,int code=0,string text="")
        {
            if(!data.TryGetProperty("id",out var id))return;
            var response=JsonSerializer.SerializeToElement(new {id,response=new {code,message=text,data=result}});
            if(owner=="manager")center?.Receive(response);else Search.Receive(response);
        }
        // Window mechanics never need Python and cannot restart it after dismissal.
        if(method=="local_suite_manager"){try{OpenSuiteManager();Reply(new {opened=true});}catch(Exception error){Reply(null,1,error.Message);}return;}
        if(method=="local_save_config"){try{SaveSuiteShortcut(request.GetProperty("params"));}catch(Exception error){Reply(null,1,error.Message);return;}}
        if(method=="local_config"){try{ReadScope();BroadcastTheme();Reply(new {settings,path=Program.Config});}catch(Exception error){Reply(null,1,error.Message);}return;}
        if(method is "local_index_info" or "local_index_folder")
        {
            if(owner!="manager"){Reply(null,1,"Index backups belong to AI Center search settings");return;}
            try
            {
                string path=Path.Combine(Program.Data,"index.sqlite3"+(settings.GetProperty("index_protection").GetString()=="windows"?".dpapi":""));
                var file=new FileInfo(path);
                if(method=="local_index_folder")Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,Arguments="\""+Program.Data+"\""});
                Reply(new {path,bytes=file.Exists?file.Length:0,saved=file.Exists?file.LastWriteTimeUtc.ToString("O"):null,model_path=settings.GetProperty("model_path").GetString()});
            }
            catch(Exception error){Reply(null,1,error.Message);}return;
        }
        if(method=="local_backup_index"&&owner!="manager"){Reply(null,1,"Open index backup in AI Center search settings");return;}
        if(method is "local_exclusions_get" or "local_exclusions_edit")
        {
            try
            {
                string path=Path.Combine(Path.GetDirectoryName(Program.Config)!,"excluded-folders.txt");
                if(!File.Exists(path))File.WriteAllText(path,"# Excluded folders: one absolute path per line. Save to apply.\n"+string.Join("\n",settings.GetProperty("excluded_folders").Deserialize<string[]>()!)+"\n");
                if(method=="local_exclusions_edit")
                {
                    if(owner!="manager")throw new ArgumentException("Open exclusion settings in AI Center");
                    var editor=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","notepad.exe")){UseShellExecute=true};editor.ArgumentList.Add(path);Process.Start(editor);
                }
                Reply(new {path,count=File.ReadAllLines(path).Count(line=>!string.IsNullOrWhiteSpace(line)&&!line.TrimStart().StartsWith("#"))});
            }
            catch(Exception error){Reply(null,1,error.Message);}return;
        }
        if(method=="local_drives"){Reply(new {roots=DriveInfo.GetDrives().Where(drive=>drive.DriveType==DriveType.Fixed&&drive.IsReady).Select(drive=>drive.RootDirectory.FullName).ToArray()});return;}
        if(method is "local_profile_get" or "local_profile_choose" or "local_profile_reset")
        {
            if(owner!="manager"){Reply(null,1,"Profile settings are available in AI Center");return;}
            try
            {
                if(method=="local_profile_choose")
                {
                    using var dialog=new OpenFileDialog {Title="Choose a profile picture",Filter="Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",CheckFileExists=true,Multiselect=false};
                    if(dialog.ShowDialog(center)==DialogResult.OK)Personalization.SavePicture(dialog.FileName,Program.Data);
                }
                if(method=="local_profile_reset")
                {
                    string picture=Path.Combine(Program.Data,"profile.png");
                    if(File.Exists(picture))File.Move(picture,Path.Combine(Program.Data,"profile.previous.png"),true);
                }
                Reply(new {image=Personalization.Picture(Program.Data)});
            }
            catch(Exception error){Reply(null,1,error.Message);}
            return;
        }
        if(method is "local_settings_history_get" or "local_settings_history_save")
        {
            if(owner!="manager"){Reply(null,1,"Settings history belongs to AI Center");return;}
            if(method=="local_settings_history_get"){Reply(settingsHistory);return;}
            var history=request.GetProperty("params");
            if(history.GetRawText().Length>262144||!history.TryGetProperty("snapshots",out var snapshots)||snapshots.ValueKind!=JsonValueKind.Array||snapshots.GetArrayLength() is <1 or >30||snapshots.EnumerateArray().Any(value=>value.ValueKind!=JsonValueKind.Object)||!history.TryGetProperty("position",out var position)||!position.TryGetInt32(out int cursor)||cursor<0||cursor>=snapshots.GetArrayLength()){Reply(null,1,"Invalid settings history");return;}
            settingsHistory=history.Clone();Reply(new {saved=true});return;
        }
        if(method is "local_history_get" or "local_history_save" or "local_history_remember" or "local_history_remove" or "local_history_clear")
        {
            try
            {
                var history=new SearchHistory(Program.Data);var parameters=request.GetProperty("params");
                var state=method switch {"local_history_get"=>history.Read(),"local_history_save"=>history.Save(parameters.GetProperty("queries").Deserialize<string[]>()??Array.Empty<string>()),"local_history_remember"=>history.Remember(parameters.GetProperty("query").GetString()!),"local_history_remove"=>history.Remove(parameters.GetProperty("query").GetString()!),_=>history.Clear()};
                Reply(new {queries=state.Queries,saved=true});
                if(method=="local_history_clear"){var changed=JsonSerializer.SerializeToElement(new {kind="history_changed",queries=state.Queries});Search.Receive(changed);center?.Receive(changed);}
            }
            catch(Exception error){Reply(null,1,"Protected history is unavailable: "+error.Message);}
            return;
        }
        if(method=="window_resize")
        {
            var parameters=request.GetProperty("params");Search.ResizeLogical(Math.Clamp(parameters.GetProperty("width").GetInt32(),320,1400),Math.Clamp(parameters.GetProperty("height").GetInt32(),78,1000));Reply(null);return;
        }
        if(method=="local_hide"){Search.Hide();Reply(null);return;}
        if(method=="local_quit"){Quit();return;}
        // WebView messages can arrive one dispatch after the center is closed.
        // Accept its already-issued edits/index actions while that page exists;
        // hidden search requests and subscriptions must still stay canceled.
        bool pendingEdit=owner=="manager"&&center?.BrowserLoaded==true&&method is "local_save_config" or "local_index_now" or "local_reset_index" or "local_mode" or "local_backup_index";
        bool warmRead=KeepSearchReady&&owner=="search"&&(method.StartsWith("register_",StringComparison.Ordinal)||method=="local_status");
        if(!ActiveWindow&&!pendingEdit&&!warmRead){Reply(null,1,"Search is dismissed");return;}
        EnsureBackend();Touch();
        if(method is "local_index_now" or "local_reset_index")indexingRequests.Add(owner+":"+data.GetProperty("id").GetRawText());
        if(method=="local_save_config")settingsRequests.Add(owner+":"+data.GetProperty("id").GetRawText());
        if(message.TryGetProperty("mode",out var mode)&&mode.GetInt32()==0&&data.TryGetProperty("id",out var identifier)&&(!data.TryGetProperty("persistent",out var persistent)||!persistent.GetBoolean()))outstanding.Add(owner+":"+identifier.GetRawText());
        var envelope=JsonNode.Parse(message.GetRawText())!;envelope["owner"]=owner;
        if(method=="local_search")
        {
            try{var parameters=envelope["data"]!["request"]!["params"]!;parameters["preferred_paths"]=JsonSerializer.SerializeToNode(new SearchHistory(Program.Data).Preferred(parameters["history_query"]?.GetValue<string>()??parameters["text"]?.GetValue<string>()??""));}
            catch(Exception error){Program.Log("Remembered choices unavailable: "+error.Message);}
        }
        Write(envelope);
    }
    void Write(object value)
    {
        lock(writeLock){if(backend is not null&&!backend.HasExited){backend.StandardInput.WriteLine(JsonSerializer.Serialize(value));backend.StandardInput.Flush();}}
    }
    void Receive(JsonElement message)
    {
        if(message.TryGetProperty("kind",out var kind))
        {
            if(kind.GetString()=="ready")
            {
                backendReady=true;workerId=message.GetProperty("pid").GetInt32();
                settings=message.GetProperty("settings").Clone();InstallShortcut();Search.Receive(message);center?.Receive(message);
                workerIndexing=!paused&&settings.GetProperty("indexing_frequency").GetString()=="realtime"&&settings.GetProperty("indexing_mode").GetString()!="paused"&&settings.GetProperty("roots").GetArrayLength()>0;
            }
            else if(kind.GetString()=="index_activity")
            {
                var activity=message.GetProperty("indexer");
                workerIndexing=WorkerActive(activity);
                if(!workerIndexing&&!IndexingActive&&!ActiveWindow&&outstanding.Count==0)CheckUnused();
            }
            else if(kind.GetString()=="action")
            {
                object? value=null;string? error=null;try{value=Action(message.GetProperty("action").GetString()!,message.GetProperty("params"));}catch(Exception exception){error=exception.Message;Program.Log(exception.ToString());}
                Write(new {kind="action_result",token=message.GetProperty("token").GetInt32(),value,error});
            }
            return;
        }
        string owner=message.TryGetProperty("owner",out var property)?property.GetString()??"search":"search";
        // A short indexing ACK must pin the worker even if the UI closes before
        // the next coalesced progress event arrives.
        if(message.TryGetProperty("response",out var reply)&&reply.TryGetProperty("data",out var payload)&&payload.ValueKind==JsonValueKind.Object&&payload.TryGetProperty("indexer",out var state))
            workerIndexing=WorkerActive(state);
        if(message.TryGetProperty("id",out var id)&&outstanding.Remove(owner+":"+id.GetRawText()))Touch();
        if(message.TryGetProperty("id",out var completed))indexingRequests.Remove(owner+":"+completed.GetRawText());
        if(message.TryGetProperty("id",out var saved))settingsRequests.Remove(owner+":"+saved.GetRawText());
        if(owner=="maintenance")
        {
            backgroundIndex=false;lastScheduled=DateTime.UtcNow;File.WriteAllText(Path.Combine(Program.Data,"schedule.txt"),lastScheduled.ToString("O"));
            if(!KeepSearchReady&&!ActiveWindow&&settingsRequests.Count==0)StopBackend("scheduled reconciliation finished");return;
        }
        if(owner=="readiness"){readinessStatus=message.GetProperty("response").GetProperty("data").Clone();return;}
        if(owner=="manager")center?.Receive(message);else Search.Receive(message);
        if(!ActiveWindow&&!IndexingActive&&outstanding.Count==0)CheckUnused();
    }

    static bool WorkerActive(JsonElement state)=>state.GetProperty("busy").GetBoolean()||
        (state.GetProperty("mode").GetString()!="paused"&&(state.GetProperty("queued_files").GetInt32()>0||
         (state.GetProperty("queued_embedding_files").GetInt32()>0&&state.GetProperty("semantic_error").ValueKind==JsonValueKind.Null)));

    internal static Dictionary<string,bool> CheckWorkerActivity()
    {
        bool Active(string mode,bool busy,int files,int vectors,string? error=null)=>WorkerActive(JsonSerializer.SerializeToElement(new {mode,busy,queued_files=files,queued_embedding_files=vectors,semantic_error=error}));
        return new() {
            ["pausedFileQueueDoesNotPinWorker"]=!Active("paused",false,5,0),
            ["pausedVectorQueueDoesNotPinWorker"]=!Active("paused",false,0,5),
            ["activeFileQueuePinsWorker"]=Active("normal",false,5,0),
            ["activeVectorQueuePinsWorker"]=Active("normal",false,0,5),
            ["inFlightWorkStillPinsPausedWorker"]=Active("paused",true,0,0),
            ["failedEmbeddingDoesNotPinWorker"]=!Active("normal",false,0,5,"model unavailable")
        };
    }
    object? Action(string action,JsonElement args)
    {
        switch(action)
        {
            case "open":
            case "open_with":
                string file=args.GetProperty("path").GetString()!;
                bool opened=true;if(testFileLaunch is not null)opened=testFileLaunch(action,file);else if(action=="open_with")opened=FileActions.OpenWith(file);else Process.Start(new ProcessStartInfo(file){UseShellExecute=true});
                if(opened&&args.TryGetProperty("query",out var query))try{new SearchHistory(Program.Data).RecordOpen(query.GetString()??"",file);}catch(Exception error){Program.Log("Remembering open failed: "+error.Message);}
                return new {opened};
            case "reveal":if(testFileLaunch is not null){testFileLaunch(action,args.GetProperty("path").GetString()!);break;}var explorer=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};explorer.ArgumentList.Add("/select,");explorer.ArgumentList.Add(args.GetProperty("path").GetString()!);Process.Start(explorer);break;
            case "copy":Clipboard.SetText(args.GetProperty("path").GetString()!);break;
            case "choose_folder":if(testBackupFolder is not null)return testBackupFolder;using(var dialog=new FolderBrowserDialog {Description="Choose a backup or folder location"})return dialog.ShowDialog(center)==DialogResult.OK?dialog.SelectedPath:null;
            case "resize":Search.ResizeLogical(args.GetProperty("width").GetInt32(),args.GetProperty("height").GetInt32());break;
            case "appearance":settings=args.Clone();InstallShortcut();ApplyStartup();if(KeepSearchReady){EnsureBackend();Search.Prepare();}Search.Script("window.dispatchEvent(new CustomEvent('settings-changed',{detail:"+args.GetRawText()+"}))");BroadcastTheme();break;
            case "hide":Search.Hide();break;case "show_search":Search.ShowSearch();break;case "manage":ShowCenter();break;case "quit":Post(Quit);break;
            default:throw new ArgumentException("Unknown native action "+action);
        }
        return null;
    }
    internal void ShowCenter()
    {
        Search.Hide();if(center is null||center.IsDisposed)center=new CenterWindow(this);
        center.Show();center.WindowState=FormWindowState.Normal;center.Activate();center.Script("window.dispatchEvent(new Event('local-center-focus'))");
    }
    internal void HideCenterForSearch()=>center?.Hide();
    internal void LaunchPlayground()
    {
        const string path=@"C:\Program Files\AI Playground\AI Playground.exe";
        if(!File.Exists(path))throw new FileNotFoundException("Intel AI Playground is not installed at "+path);
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    internal void Quit()
    {
        if(Program.Exiting)return;Program.Exiting=true;suite?.Dispose();keyboard?.Dispose();tray.Visible=false;StopBackend("quit");center?.Close();Search.Close();ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing&&!disposed){disposed=true;SystemEvents.SessionSwitch-=SessionChanged;SystemEvents.UserPreferenceChanged-=WindowsThemeChanged;StopBackend("native host disposed");maintenance.Dispose();idle.Dispose();suite?.Dispose();keyboard?.Dispose();tray.Dispose();}
        base.Dispose(disposing);
    }
}

internal class WebWindow : Form
{
    protected readonly CenterContext App;
    protected WebView2 Web=new() {Dock=DockStyle.Fill};
    readonly System.Windows.Forms.Timer browserIdle=new() {Interval=5000};
    readonly string owner,page;
    bool loading,navigated;
    Point dragOrigin;
    protected bool Ready;
    internal WebWindow(CenterContext app,string owner,string page)
    {
        App=app;this.owner=owner;this.page=page;
        AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new SizeF(96,96);Icon=Personalization.AppIcon;
        BackColor=app?.WindowColor??Color.White;Web.DefaultBackgroundColor=BackColor;Controls.Add(Web);
        AttachWeb(Web);
        browserIdle.Tick+=(_,_)=>{browserIdle.Stop();if((!Visible||WindowState==FormWindowState.Minimized)&&!(owner=="search"&&App?.KeepSearchReady==true))ReleaseBrowser();};
    }
    protected virtual void AttachWeb(WebView2 web){}
    internal bool BrowserLoaded=>loading||Web.CoreWebView2 is not null;
    internal object? BrowserIdentity=>Web.CoreWebView2;
    internal bool PageReady=>navigated;
    internal Task<string> Evaluate(string script)=>Web.ExecuteScriptAsync(script);
    internal async Task SavePreview(string filename){await Task.Delay(100);using var stream=new FileStream(Path.Combine(Program.Data,filename),FileMode.CreateNew);await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);}
    internal async Task<bool> CheckAccent(string[][] rules)
    {
        // Check computed styles for two distinct colors, including the search body.
        string script="(()=>{const root=document.documentElement,old=root.style.getPropertyValue('--accent'),rules="+JsonSerializer.Serialize(rules)+";const read=()=>rules.map(([selector,property])=>{const element=document.querySelector(selector);if(!element)throw Error(selector);return getComputedStyle(element)[property];});try{root.style.setProperty('--accent','#b547d8');const first=read();root.style.setProperty('--accent','#20aa73');const second=read();return first.every((value,index)=>value!==second[index]);}finally{root.style.setProperty('--accent',old);}})()";
        return await Web.ExecuteScriptAsync(script)=="true";
    }
    void ReleaseBrowser()
    {
        Ready=false;loading=false;navigated=false;var old=Web;Controls.Remove(old);old.Dispose();
        Web=new WebView2 {Dock=DockStyle.Fill,DefaultBackgroundColor=BackColor};AttachWeb(Web);Controls.Add(Web);
        Program.Log("WebView released: "+owner);
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);if(Visible&&WindowState!=FormWindowState.Minimized){browserIdle.Stop();_=Initialize();}else browserIdle.Start();
    }
    internal void Prepare(){_=Initialize(true);}
    async Task Initialize(bool prepare=false)
    {
        if(Program.CheckLaunchRouting||loading || Ready||(!prepare&&!Visible)||WindowState==FormWindowState.Minimized)return;loading=true;var web=Web;
        try
        {
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Program.Data,"native-webview"));
            if(web!=Web||web.IsDisposed)return;
            await web.EnsureCoreWebView2Async(environment);
            if(web!=Web||web.IsDisposed)return;
            var core=web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("ai-center.local",Path.Combine(Program.Root,"frontend"),CoreWebView2HostResourceAccessKind.Deny);
            core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=false;core.Settings.IsStatusBarEnabled=false;core.Settings.IsZoomControlEnabled=false;
            core.NavigationStarting+=(_,eventArgs)=>{ if(!eventArgs.Uri.StartsWith(Program.Origin+"/",StringComparison.Ordinal))eventArgs.Cancel=true; };
            core.NewWindowRequested+=(_,eventArgs)=>eventArgs.Handled=true;
            core.PermissionRequested+=(_,eventArgs)=>eventArgs.State=CoreWebView2PermissionState.Deny;
            core.WebMessageReceived+=(_,eventArgs)=>
            {
                if(!eventArgs.Source.StartsWith(Program.Origin+"/",StringComparison.Ordinal))return;
                try
                {
                    using var document=JsonDocument.Parse(eventArgs.WebMessageAsJson);
                    var message=document.RootElement;
                    if(message.TryGetProperty("host",out var host))HostAction(host.GetString()!,message);else App.Send(message,owner);
                }
                catch(Exception exc){Program.Log(exc.ToString());}
            };
            core.ProcessFailed+=(_,eventArgs)=>{Program.Log("WebView process failed: "+eventArgs.ProcessFailedKind);};
            core.NavigationCompleted+=(_,eventArgs)=>{if(eventArgs.IsSuccess&&web==Web){navigated=true;NavigationReady();}};
            Ready=true;core.Navigate(Program.Origin+"/"+page);
        }
        catch(Exception exc){if(web==Web&&!web.IsDisposed){Program.Log(exc.ToString());if(Visible)MessageBox.Show(this,exc.Message,"Local UI unavailable");}}
        finally{if(web==Web)loading=false;}
    }
    protected virtual void NavigationReady(){}
    protected virtual void HostAction(string action,JsonElement message)
    {
        switch(action)
        {
            case "search":App.Search.ShowSearch();break;
            case "playground":App.LaunchPlayground();break;
            case "hide":Hide();break;
            case "minimize":WindowState=FormWindowState.Minimized;break;
            case "close":Hide();break;
            case "quit":App.Quit();break;
            case "theme":BackColor=message.GetProperty("dark").GetBoolean()?Color.FromArgb(23,26,31):Color.White;Web.DefaultBackgroundColor=BackColor;break;
            case "drag":NativeInput.ReleaseCapture();NativeInput.SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);break;
            case "drag_start":dragOrigin=Location;break;
            case "drag_move":
                float scale=DeviceDpi/96f;
                Location=new Point(dragOrigin.X+(int)Math.Round(message.GetProperty("dx").GetDouble()*scale),dragOrigin.Y+(int)Math.Round(message.GetProperty("dy").GetDouble()*scale));break;
            default:throw new ArgumentException("Unknown host action");
        }
    }
    internal void Receive(JsonElement message){if(Ready)Web.CoreWebView2.PostWebMessageAsJson(message.GetRawText());}
    internal async void Script(string code){if(Ready)try{await Web.ExecuteScriptAsync(code);}catch(Exception exc){Program.Log(exc.Message);}}
    protected virtual int CornerRadius => 18;
    protected override void WndProc(ref Message message)
    {
        // The HTML and rounded client region own the edge. With DWM frame
        // rendering disabled, DefWindowProc would paint a white classic frame.
        if(message.Msg==0x85){message.Result=IntPtr.Zero;return;} // WM_NCPAINT
        if(message.Msg==0x86){message.Result=new IntPtr(1);return;} // WM_NCACTIVATE
        base.WndProc(ref message);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int disabled=1;DwmSetWindowAttribute(Handle,2,ref disabled,sizeof(int));
        DwmSetWindowAttribute(Handle,33,ref disabled,sizeof(int));
        RoundFrame();
    }
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);RoundFrame();if(WindowState==FormWindowState.Minimized)browserIdle.Start();else if(Visible){browserIdle.Stop();_=Initialize();}}
    protected override void Dispose(bool disposing){if(disposing)browserIdle.Dispose();base.Dispose(disposing);}
    void RoundFrame()
    {
        if(Width<=0 || Height<=0)return;
        if(WindowState==FormWindowState.Maximized||CornerRadius<=0){var old=Region;Region=null;old?.Dispose();return;}
        int diameter=Math.Min(Math.Min(Width,Height),CornerRadius*2*DeviceDpi/96);
        using var path=new GraphicsPath();
        path.AddArc(0,0,diameter,diameter,180,90);
        path.AddArc(Width-diameter,0,diameter,diameter,270,90);
        path.AddArc(Width-diameter,Height-diameter,diameter,diameter,0,90);
        path.AddArc(0,Height-diameter,diameter,diameter,90,90);path.CloseFigure();
        var previous=Region;Region=new Region(path);previous?.Dispose();
    }
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    internal bool NativeFrameEnabled(){if(DwmGetWindowAttribute(Handle,1,out int value,sizeof(int))!=0)throw new InvalidOperationException("Cannot read DWM frame state");return value!=0;}
}

internal sealed class CenterWindow : WebWindow
{
    bool fullScreen;
    Rectangle restoreBounds;
    FormWindowState restoreState;
    protected override int CornerRadius=>fullScreen?0:18;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters=base.CreateParams;
            // Keep native taskbar minimize/maximize behavior while drawing the caption in HTML.
            parameters.Style|=0x00C00000|0x00040000|0x00080000|0x00020000|0x00010000;
            parameters.ClassStyle&=~0x00020000;return parameters;
        }
    }
    internal CenterWindow(CenterContext app):base(app,"manager","center.html")
    {
        Text="AI Center";FormBorderStyle=FormBorderStyle.None;ClientSize=new Size(560,650);StartPosition=FormStartPosition.CenterScreen;
        MinimizeBox=true;MaximizeBox=true;Padding=new Padding(3);
        FormClosing+=(_,eventArgs)=>{if(!Program.Exiting){eventArgs.Cancel=true;Hide();}};
        VisibleChanged+=(_,_)=>{App?.CenterVisibility();if(!Visible)App?.CheckUnused();};
        Shown+=(_,_)=>
        {
            float scale=DeviceDpi/96f;var area=Screen.FromControl(this).WorkingArea;
            ClientSize=new Size(Math.Min((int)Math.Round(560*scale),area.Width-48),Math.Min((int)Math.Round(650*scale),area.Height-100));
            MinimumSize=new Size((int)Math.Round(480*scale),(int)Math.Round(480*scale));
            Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
        };
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);Padding=WindowState==FormWindowState.Maximized||fullScreen?Padding.Empty:new Padding(Math.Max(3,3*DeviceDpi/96));
        if(WindowState==FormWindowState.Minimized)App?.CheckUnused();
        Script("window.dispatchEvent(new CustomEvent('window-state-changed',{detail:"+JsonSerializer.Serialize(new {maximized=WindowState==FormWindowState.Maximized,fullScreen})+"}))");
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x24&&!fullScreen)
        {
            base.WndProc(ref message);
            var limits=Marshal.PtrToStructure<MinMaxInfo>(message.LParam);var screen=Screen.FromControl(this);var area=ExpandedBounds(false);
            limits.MaxPosition=new Point(area.Left-screen.Bounds.Left,area.Top-screen.Bounds.Top);limits.MaxSize=new Point(area.Width,area.Height);Marshal.StructureToPtr(limits,message.LParam,false);return;
        }
        if(message.Msg==0x83&&message.WParam!=IntPtr.Zero){message.Result=IntPtr.Zero;return;}
        if(message.Msg==0x84&&WindowState==FormWindowState.Normal&&!fullScreen)
        {
            base.WndProc(ref message);
            if(message.Result==new IntPtr(1))
            {
                int packed=unchecked((int)message.LParam.ToInt64());var point=PointToClient(new Point(unchecked((short)packed),unchecked((short)(packed>>16))));
                int grip=Padding.Left+2;bool left=point.X<grip,right=point.X>=Width-grip,top=point.Y<grip,bottom=point.Y>=Height-grip;
                message.Result=new IntPtr(top?left?13:right?14:12:bottom?left?16:right?17:15:left?10:right?11:1);
            }
            return;
        }
        base.WndProc(ref message);
    }
    protected override void HostAction(string action,JsonElement message)
    {
        if(action=="maximize"){if(fullScreen){HostAction("fullscreen",message);return;}MaximizedBounds=ExpandedBounds(false);WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;return;}
        if(action=="fullscreen")
        {
            if(!fullScreen){restoreBounds=Bounds;restoreState=WindowState;fullScreen=true;WindowState=FormWindowState.Normal;Bounds=ExpandedBounds(true);}
            else{fullScreen=false;Bounds=restoreBounds;WindowState=restoreState;}
            OnSizeChanged(EventArgs.Empty);return;
        }
        base.HostAction(action,message);
    }
    internal Rectangle ExpandedBounds(bool full)
    {
        var screen=Screen.FromControl(this);var area=full?screen.Bounds:screen.WorkingArea;
        // PC Manager preserves a physical edge so an auto-hide taskbar can activate.
        return Rectangle.FromLTRB(area.Left,area.Top,area.Right,area.Bottom-2);
    }
    [StructLayout(LayoutKind.Sequential)]struct MinMaxInfo{public Point Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;}
}

internal static class AccountProtection
{
    [StructLayout(LayoutKind.Sequential)]struct Blob{public int Size;public IntPtr Data;}
    [DllImport("crypt32.dll",SetLastError=true)]static extern bool CryptProtectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Crypt(byte[] bytes,bool decrypt)
    {
        var input=new Blob {Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;
        try{Marshal.Copy(bytes,0,input.Data,bytes.Length);bool success=decrypt?CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptProtectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);if(!success)throw new System.ComponentModel.Win32Exception();var result=new byte[output.Size];Marshal.Copy(output.Data,result,0,result.Length);return result;}
        finally{Marshal.FreeHGlobal(input.Data);if(output.Data!=IntPtr.Zero)LocalFree(output.Data);}
    }
}

internal sealed class SearchWindow : WebWindow
{
    protected override int CornerRadius => 22;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters=base.CreateParams;
            if(!Program.InspectUi)parameters.ExStyle=(parameters.ExStyle|0x80)&~0x40000; // Tool window: no Alt+Tab or taskbar entry.
            parameters.ClassStyle&=~0x00020000; // No rectangular legacy shadow outside the rounded region.
            return parameters;
        }
    }
    bool placed,moved;
    NativeInput? mouse;
    internal SearchWindow(CenterContext app):base(app,"search","search.html")
    {
        Text="Local Search · AI Center";ClientSize=new Size(700,78);FormBorderStyle=FormBorderStyle.None;
        // Optional test visibility lets accessibility tools discover this floating form.
        ShowInTaskbar=Program.InspectUi;TopMost=true;StartPosition=FormStartPosition.Manual;
        VisibleChanged+=(_,_)=>{mouse?.Dispose();mouse=null;if(Visible)mouse=NativeInput.OutsideClicks(point=>Bounds.Contains(point)&&(Region?.IsVisible(PointToClient(point))??true),()=>BeginInvoke((Action)Hide));else{Script("window.dispatchEvent(new Event('local-search-dismiss'))");App?.DismissSearch();}};
        FormClosing+=(_,eventArgs)=>{if(!Program.Exiting){eventArgs.Cancel=true;Hide();}};
    }
    protected override void AttachWeb(WebView2 web){web.GotFocus+=(_,_)=>{if(Visible)Script("document.getElementById('query')?.focus({preventScroll:true})");};}
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Activation changes can briefly pass through WebView's child windows.
        // Check the final foreground root on the next UI dispatch, without a timer.
        if(Visible)BeginInvoke((Action)(()=>{if(Visible&&!NativeInput.IsForeground(this))Hide();}));
    }
    protected override void NavigationReady(){if(Visible&&NativeInput.IsForeground(this))FocusInput(false);}
    void FocusInput(bool fresh)
    {
        if(!Visible)return;
        bool foreground=NativeInput.FocusWindow(this,()=>Web.Focus());
        // Activating the form alone does not focus the embedded browser's input.
        Script(fresh?"window.dispatchEvent(new Event('local-search-open'))":"document.getElementById('query')?.focus({preventScroll:true})");
        Program.Log($"Search focus requested: foreground={foreground}, browser={Web.Focused}, ready={Ready}");
    }
    internal void ShowSearch()
    {
        App.HideCenterForSearch();
        Program.Log($"Show search: handle={Handle}, dpi={DeviceDpi}");
        if(!placed)
        {
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;
            Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(int)(area.Height*.20));placed=true;
        }
        Show();FocusInput(true);
        Program.Log($"Search window: exStyle=0x{NativeInput.GetWindowLongPtr(Handle,-20).ToInt64():X}, taskbar={ShowInTaskbar}, cornerVisible={Region?.IsVisible(0,0)}, bounds={Bounds}");
    }
    internal void ResizeLogical(int width,int height)
    {
        float scale=DeviceDpi/96f;
        var area=Screen.FromControl(this).WorkingArea;
        ClientSize=new Size(Math.Min((int)Math.Round(width*scale),area.Width-24),Math.Min((int)Math.Round(height*scale),area.Height-24));
        if(placed){Location=!moved?new Point(area.Left+(area.Width-Width)/2,area.Top+(int)(area.Height*.20)):new Point(Math.Clamp(Left,area.Left,Math.Max(area.Left,area.Right-Width)),Math.Clamp(Top,area.Top,Math.Max(area.Top,area.Bottom-Height)));}
    }
    protected override void HostAction(string action,JsonElement message)
    {
        if(action=="drag_start")moved=true;
        base.HostAction(action,message);
    }
    internal void BackendFailed(){Script("localBridge.feedback('Search backend stopped. Quit AI Center and relaunch.',true)");}
    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x8000+81){if(message.WParam==new IntPtr(2))App.Quit();else if(message.WParam!=IntPtr.Zero)App.ShowCenter();else ShowSearch();return;}
        base.WndProc(ref message);
    }
    protected override void Dispose(bool disposing){mouse?.Dispose();base.Dispose(disposing);}
}

internal sealed class NativeInput : IDisposable
{
    readonly Hook callback;
    IntPtr handle;
    readonly HashSet<uint> pressed=new();
    Thread? listener;
    ApplicationContext? loop;
    uint listenerId;
    internal bool SeparateMessageLoop=>listener?.IsAlive==true&&listenerId!=GetCurrentThreadId();
    NativeInput(int kind,Hook callback){this.callback=callback;handle=SetWindowsHookEx(kind,callback,GetModuleHandle(null),0);if(handle==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();}
    internal NativeInput(string shortcut,int interval,Action show)
    {
        var gesture=new ControlTap(interval);
        callback=(code,message,data)=>
        {
            if(code>=0)
            {
                uint key=(uint)Marshal.ReadInt32(data);long now=Environment.TickCount64;
                bool keyDown=message.ToInt64() is 0x100 or 0x104;
                bool keyUp=message.ToInt64() is 0x101 or 0x105;
                if(Program.InspectUi&&key is 0x11 or 0xA2 or 0xA3)Program.Log($"Control gesture: down={keyDown}, up={keyUp}, tick={now}");
                if(shortcut=="double_ctrl"&&(keyDown||keyUp)&&gesture.Key(key,keyDown,now))show();
                if(keyDown && pressed.Add(key))
                {
                    if(shortcut=="alt_space" && key==0x20 && pressed.Any(value=>value is 0x12 or 0xA4 or 0xA5))show();
                }
                if(keyUp)pressed.Remove(key);
            }
            return CallNextHookEx(IntPtr.Zero,code,message,data);
        };
        if(shortcut!="none")
        {
            // Low-level hooks time out if their message loop blocks. Keep this
            // loop independent of WebView startup, dialogs and index shutdown.
            using var started=new ManualResetEventSlim();Exception? failure=null;
            listener=new Thread(()=>{try{listenerId=GetCurrentThreadId();loop=new ApplicationContext();handle=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);if(handle==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();started.Set();Application.Run(loop);}catch(Exception error){failure=error;started.Set();}finally{if(handle!=IntPtr.Zero){UnhookWindowsHookEx(handle);handle=IntPtr.Zero;}}}){IsBackground=true,Name="Search shortcut"};
            listener.SetApartmentState(ApartmentState.STA);listener.Start();started.Wait();if(failure is not null)throw failure;
        }
    }
    internal static NativeInput OutsideClicks(Func<Point,bool> contains,Action hide)=>new(14,(code,message,data)=>
    {
        if(code>=0 && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B)
        {var point=Marshal.PtrToStructure<Point>(data);if(!contains(point))hide();}
        return CallNextHookEx(IntPtr.Zero,code,message,data);
    });
    public void Dispose(){if(listener is not null){PostThreadMessage(listenerId,0x12,IntPtr.Zero,IntPtr.Zero);listener.Join(1000);loop?.Dispose();}else if(handle!=IntPtr.Zero){UnhookWindowsHookEx(handle);handle=IntPtr.Zero;}GC.KeepAlive(callback);}
    [DllImport("user32.dll")]static extern bool PostThreadMessage(uint thread,uint message,IntPtr first,IntPtr second);
    delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")]static extern IntPtr SetWindowsHookEx(int kind,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")]internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")]internal static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr first,IntPtr second);
    [DllImport("user32.dll")]internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr window,uint flags);
    internal static bool IsForeground(Form window)=>GetAncestor(GetForegroundWindow(),2)==window.Handle;
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]static extern bool AttachThreadInput(uint first,uint second,bool attach);
    internal static bool FocusWindow(Form window,Action focus)
    {
        // An explicit shortcut must focus the input across foreground app threads.
        uint current=GetCurrentThreadId(),foreground=GetWindowThreadProcessId(GetForegroundWindow(),out _);
        bool attached=foreground!=0&&foreground!=current&&AttachThreadInput(current,foreground,true);
        try{window.BringToFront();window.Activate();bool activated=SetForegroundWindow(window.Handle);focus();return activated||GetForegroundWindow()==window.Handle;}
        finally{if(attached)AttachThreadInput(current,foreground,false);}
    }
    [DllImport("user32.dll")]internal static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")]internal static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool SetWindowText(IntPtr window,string text);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]internal static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
}
