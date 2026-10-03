// Purpose: C# WebView2 frontend, capture/hotkeys/tray, resident Python bridge.
// Dependencies: .NET Desktop 8+, existing WebView2 SDK/runtime, Python requirements.txt.
// Outputs: user data/config, timing/native logs, WebView profile; PNG screenshots only on request.
// Command: ScreenTranslator.exe [--tray], or ./dev.ps1 -Sdk <dotnet> -WebViewReferenceDir <SDK assemblies>.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LocalScreenTranslator;

internal static class Program
{
    internal static string Root="",Data="",CacheData="",Python="python",Fixture="";
    internal static bool NoLoad,CheckUi,CheckSuiteUi,CheckLifecycle,TrayStart,Packaged,NoStartup;
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            string Option(string name,string fallback){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
            if(XiaomiRevamp.Suite.SuiteEnvironment.Enabled && XiaomiRevamp.Suite.SuiteEnvironment.LegacyRunning("screen-translator")){MessageBox.Show("The original Screen Translator is running. Exit that copy before opening the connected edition.","Screen Translator");return;}
            Packaged=File.Exists(Path.Combine(AppContext.BaseDirectory,"packaged.json"));
            Root=Path.GetFullPath(Option("--root",Packaged?AppContext.BaseDirectory:Environment.CurrentDirectory));
            if(!File.Exists(Path.Combine(Root,"screen_translator","backend.py")))throw new DirectoryNotFoundException("Pass --root with the screen-translator source folder.");
            Data=Path.GetFullPath(Option("--data-dir",XiaomiRevamp.Suite.SuiteEnvironment.Enabled?XiaomiRevamp.Suite.SuiteEnvironment.Data("screen-translator"):Packaged?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenTranslator"):Path.Combine(Root,"data")));Directory.CreateDirectory(Data);
            Python=Option("--python",Packaged?Path.Combine(Root,"runtime","python","python.exe"):"python");NoLoad=args.Contains("--no-load");CheckSuiteUi=args.Contains("--check-suite-ui");CheckUi=args.Contains("--check-ui")||CheckSuiteUi;TrayStart=args.Contains("--tray")||args.Contains("--toggle")||args.Contains("--region");NoStartup=args.Contains("--no-startup")||CheckSuiteUi;
            CheckLifecycle=args.Contains("--check-model-lifecycle");CheckUi|=CheckLifecycle;NoStartup|=CheckLifecycle;
            CacheData=Path.GetFullPath(Option("--cache-data-dir",Data));
            Fixture=Option("--fixture-directory","");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException+=(_,e)=>{Log(e.Exception.ToString());MessageBox.Show(e.Exception.Message,"Screen Translator error");};
            if(args.Contains("--quit")){DesktopOptions.QuitExisting();return;}
            using var mutex=new Mutex(true,CheckUi?"Local\\ScreenTranslator.CheckUi."+Environment.ProcessId:"Local\\ScreenTranslator" + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? ".Suite." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : ""),out bool first);
            if(!first){if(args.Contains("--toggle") || args.Contains("--region")) { XiaomiRevamp.Suite.SuiteStore.Send(args.Contains("--region") ? "screen-translator.region" : "screen-translator.toggle"); } else if(!TrayStart) DesktopOptions.WakeExisting();return;}
            if(XiaomiRevamp.Suite.SuiteEnvironment.Enabled && (args.Contains("--toggle") || args.Contains("--region"))) XiaomiRevamp.Suite.SuiteStore.Send(args.Contains("--region") ? "screen-translator.region" : "screen-translator.toggle");
            using var window=new MainWindow();
            if(TrayStart){window.StartHidden();Application.Run();}else Application.Run(window);
            mutex.ReleaseMutex();
        }
        catch(Exception error){if(Data.Length>0)Log(error.ToString());Console.Error.WriteLine(error);Environment.ExitCode=1;MessageBox.Show(error.Message,"Screen Translator could not start");}
    }
    static readonly object logLock=new();
    internal static void Log(string message)
    {
        lock(logLock)
        {
            string path=Path.Combine(Data,"native.log");
            if(File.Exists(path)&&new FileInfo(path).Length>5_000_000)File.Move(path,Path.Combine(Data,$"native-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}.log"));
            File.AppendAllText(path,$"{DateTimeOffset.Now:O} {message}\n");
        }
        Console.Error.WriteLine(message);
    }
}

internal sealed partial class MainWindow : Form
{
    const string Origin="https://screen-translator.local";
    readonly WebView2 web=new() {Dock=DockStyle.Fill,DefaultBackgroundColor=Color.White};
    readonly ReplacementOverlay overlay=new();
    readonly TranslationToolbar toolbar;
    readonly System.Windows.Forms.Timer filterTimer=new();
    readonly System.Windows.Forms.Timer visibilityTimer=new() {Interval=100};
    readonly System.Windows.Forms.Timer modelIdle=new() {Interval=10000};
    int pendingAction;
    Rectangle? resumeBounds;
    readonly NotifyIcon tray;
    readonly List<int> hotkeys=new();
    readonly List<string> hotkeyErrors=new();
    JsonObject config;
    Process? backend;
    JsonNode? models,lastTiming;
    string status="Development frontend ready",lastError="",activeId="";
    bool ready,busy,original,filter,exiting,navigated,selecting,frameSent,redrawOverlay,initializing;
    long serial,epoch;
    int monitor;
    Rectangle? target;
    Bitmap? pendingCapture;
    double captureMs;
    readonly Stopwatch requestTime=new();
    readonly object pipeLock=new();
    Task pipeWrites=Task.CompletedTask;

    internal MainWindow()
    {
        toolbar=new TranslationToolbar(async ()=>await ToggleOriginal(),async ()=>await SetFilter(!filter),Dismiss,SaveTranslatedScreenshot);
        modelIdle.Tick+=(_,_)=>{modelIdle.Stop();if(ModelIdle)UnloadModels("Idle · inference memory released; compiled cache retained");};
        // Poll settled foreground state; activation callbacks fire during window transitions.
        visibilityTimer.Tick+=async (_,_)=>{if(target is not null){ObservePage();SyncOverlayVisibility();await RefreshChangedPage();}};
        Text="Screen Translator";AutoScaleMode=AutoScaleMode.None;
        StartPosition=FormStartPosition.CenterScreen;FormBorderStyle=FormBorderStyle.None;
        // WebView CSS uses logical pixels; initialize the host size at its actual Windows DPI.
        _=Handle;double scale=DeviceDpi/96.0;var area=Screen.FromHandle(Handle).WorkingArea;
        ClientSize=new Size(Math.Min(area.Width-40,(int)Math.Round(640*scale)),Math.Min(area.Height-40,(int)Math.Round(760*scale)));
        MinimumSize=new Size((int)Math.Round(520*scale),(int)Math.Round(560*scale));
        config=Defaults();string path=Path.Combine(Program.Data,"config.json");
        if(File.Exists(path))
        {
            if(JsonNode.Parse(File.ReadAllText(path)) is not JsonObject saved)throw new InvalidDataException("data/config.json must be a JSON object");
            foreach(var pair in saved)if(config.ContainsKey(pair.Key))config[pair.Key]=pair.Value?.DeepClone();
            // This release changes the default to continuous use. Later explicit focus choices persist.
            if(saved["refresh_version"]?.GetValue<int>()!=2){config["toolbar_focus_only"]=false;config["refresh_version"]=2;}
            if(saved["shortcut_version"]?.GetValue<int>()!=2&&config["shortcut"]!.GetValue<string>()=="F8")config["shortcut"]="Copilot";
        }
        config["shortcut_version"]=2;
        if(!DesktopOptions.FontNames.Contains(config["font_family"]!.GetValue<string>(),StringComparer.OrdinalIgnoreCase))config["font_family"]="Segoe UI";
        config["mode"]="Managed";config["batch_size"]=8;
        ValidateConfig(config);SaveConfig();Controls.Add(web);
        Icon=DesktopOptions.AppIcon();
        tray=new NotifyIcon {Icon=Icon,Text="Screen Translator · offline",Visible=true,ContextMenuStrip=new ContextMenuStrip()};
        tray.ContextMenuStrip.Items.Add("Show controls",null,(_,_)=>ShowControls());
        tray.ContextMenuStrip.Items.Add("Translate screen",null,async (_,_)=>await Hotkey(1));
        tray.ContextMenuStrip.Items.Add("Translate region",null,async (_,_)=>await Hotkey(2));
        tray.ContextMenuStrip.Items.Add("Filter on / off",null,async (_,_)=>await Hotkey(4));
        tray.ContextMenuStrip.Items.Add("Original / translated",null,async (_,_)=>await Hotkey(3));
        tray.ContextMenuStrip.Items.Add("Exit",null,(_,_)=>Quit());tray.DoubleClick+=(_,_)=>ShowControls();
        filterTimer.Tick+=async (_,_)=>{if(!busy&&ready&&!original&&!ControlsOpen){if(stale)await RefreshChangedPage();else if(redrawOverlay||!config["cache"]!.GetValue<bool>())await CaptureTarget();}};
        Shown+=async (_,_)=>await Initialize();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        InitializeDesktop();
    }
    static JsonObject Defaults()=>new()
    {
        ["models"]=Path.Combine(Program.Root,"models","zh-en"),["mode"]="Managed",
        ["devices"]=new JsonArray("NPU","GPU","GPU"),["benchmark"]="",["batch_size"]=8,
        ["cache"]=true,["incremental"]=true,["interval_ms"]=600,["font_scale"]=1.0,["debug"]=false,["toolbar_focus_only"]=false,["refresh_version"]=2,
        ["follow_suite_appearance"]=true,["theme"]="system",["accent"]="#3482ff",["picture"]="",["shortcut"]="Copilot",["shortcut_version"]=2,["profile"]="Auto",["autostart"]=!XiaomiRevamp.Suite.SuiteEnvironment.Portable,
        ["font_family"]="Segoe UI",["font_fit"]=true,["display_style"]="underline",
        ["screenshot_folder"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"Screen Translator")
    };
    static void ValidateConfig(JsonObject value)
    {
        if(string.IsNullOrWhiteSpace(value["models"]?.GetValue<string>()))throw new ArgumentException("Choose a local model directory");
        if(value["devices"] is not JsonArray devices||devices.Count!=3||devices.Any(d=>string.IsNullOrWhiteSpace(d?.GetValue<string>())))throw new ArgumentException("Supply three device IDs");
        if(value["mode"]?.GetValue<string>()!="Managed")throw new ArgumentException("Hardware is selected automatically");
        if(value["batch_size"]!.GetValue<int>() is <1 or >64)throw new ArgumentException("Batch size must be 1 to 64");
        if(value["interval_ms"]!.GetValue<int>() is <200 or >10000)throw new ArgumentException("Filter interval must be 200 to 10000 ms");
        double font=value["font_scale"]!.GetValue<double>();if(!double.IsFinite(font)||font is <.5 or >2.5)throw new ArgumentException("Text size must be 50% to 250%");
        foreach(string key in new[]{"cache","incremental","debug","toolbar_focus_only"})_=value[key]!.GetValue<bool>();
        _=value["benchmark"]!.GetValue<string>();
        DesktopOptions.Validate(value);
    }
    async Task Initialize()
    {
        if(initializing)return;initializing=true;
        try
        {
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Program.Data,"webview"));
            await web.EnsureCoreWebView2Async(environment);
            var core=web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("screen-translator.local",Path.Combine(Program.Root,"frontend"),CoreWebView2HostResourceAccessKind.Deny);
            core.SetVirtualHostNameToFolderMapping("screen-translator-images.local",Path.Combine(Program.Data,"appearance"),CoreWebView2HostResourceAccessKind.Deny);
            core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.IsStatusBarEnabled=false;
            core.Settings.AreDevToolsEnabled=true;core.Settings.IsZoomControlEnabled=false;
            core.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith(Origin+"/",StringComparison.Ordinal))e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.WebResourceRequested+=(_,e)=>
            {
                if(!e.Request.Uri.StartsWith(Origin+"/",StringComparison.Ordinal)&&!e.Request.Uri.StartsWith("https://screen-translator-images.local/",StringComparison.Ordinal))e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(),403,"Offline UI","");
            };
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebMessageReceived+=async (_,e)=>
            {
                if(!e.Source.StartsWith(Origin+"/",StringComparison.Ordinal))return;
                try{using var document=JsonDocument.Parse(e.WebMessageAsJson);await Action(document.RootElement);}
                catch(Exception error){Fail(error.Message,false);}
            };
            core.NavigationCompleted+=async (_,e)=>
            {
                if(!e.IsSuccess){Fail("Local frontend navigation failed",true);return;}
                navigated=true;Publish();
                if(Program.TrayStart)Hide();
                if(Program.CheckLifecycle){await CheckModelLifecycle();return;}
                if(Program.CheckSuiteUi){await CheckSuiteUi();return;}
                if(Program.CheckUi){await CheckUi();return;}
                // Inference starts on an explicit action, so a tray/settings launch stays lightweight.
            };
            if(!Program.CheckUi)
            {
                if(XiaomiRevamp.Suite.SuiteEnvironment.Enabled)StartSuite();else {RegisterShortcut();RegisterLegacyKeys();}
            }
            Round();core.Navigate(Origin+"/index.html");
        }
        catch(Exception error){Fail(error.Message,true);}
    }
    protected override CreateParams CreateParams
    {
        // Preserve native caption/minimize styles for DWM transitions; draw our caption in WebView.
        get{var value=base.CreateParams;value.Style|=0xC00000|0x80000|0x20000|0x10000;return value;}
    }
    void Round(){int round=2;Native.DwmSetWindowAttribute(Handle,33,ref round,sizeof(int));}
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Preserve the same WebView and canvas across minimize/restore. DWM handles system animation.
        if(WindowState==FormWindowState.Minimized&&target is not null){toolbar.Present(target.Value,original,filter,overlay.Blocks,overlay.Clipped);SyncOverlayVisibility();}
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x83&&message.WParam!=IntPtr.Zero){message.Result=IntPtr.Zero;return;}
        if(message.Msg==DesktopOptions.WakeMessage){ShowControls();return;}
        if(message.Msg==DesktopOptions.QuitMessage){Quit();return;}
        if(message.Msg==0x0312)
        {
            int id=message.WParam.ToInt32();
            if(translationShortcut is not null&&translationShortcut.Id==id){if(!shortcutRecording)_=Hotkey(6);}
            else _=Hotkey(id);return;
        }
        base.WndProc(ref message);
    }
    async Task Hotkey(int id)
    {
        if(id==6&&target is not null){Dismiss();return;}
        if((id is 1 or 2 or 4 or 6) && !ready)
        {
            if(id==6&&pendingAction==6){Stop();return;}
            if(!busy)LoadModels(id,id==4?target:null);else pendingAction=id;
            Program.Log("Queued translation action "+id+"; controls hidden while loading");
            Hide();toolbar.Loading(SelectedScreen().Bounds);return;
        }
        if(id==6){Hide();await SetFilter(true);return;}
        try{if(id==1)await Translate(false);if(id==2)await Translate(true);if(id==3)await ToggleOriginal();if(id==4)await SetFilter(!filter);if(id==5)Dismiss();}
        catch(Exception error){Fail(error.Message,false);}
    }
    bool ControlsOpen=>Visible&&WindowState!=FormWindowState.Minimized;
    void ShowControls(){overlay.Hide();toolbar.Hide();WindowState=FormWindowState.Normal;Show();Activate();ArmModelIdle();Publish();}
    void HideControls()
    {
        Hide();if(filter&&!original&&!ready){_=Hotkey(4);return;}if(target is Rectangle bounds){toolbar.Present(bounds,original,filter,overlay.Blocks,overlay.Clipped);ObservePage(force:true);SyncOverlayVisibility();}ArmModelIdle();
    }
    void SyncOverlayVisibility()
    {
        toolbar.ScreenshotAvailable=target is not null&&!ControlsOpen&&!original&&overlay.HasVisibleText&&!savingScreenshot;
        if(stale&&!partialLayer||target is null||original||ControlsOpen||config["toolbar_focus_only"]!.GetValue<bool>()&&!toolbar.IsActive)overlay.Hide();
        else if(!overlay.Visible)overlay.Reveal();
    }
    void ArmEscape()
    {
        visibilityTimer.Start();
        if(hotkeys.Contains(5))return;
        if(Native.RegisterHotKey(Handle,5,0x4000,0x1B))hotkeys.Add(5);
        else throw new InvalidOperationException("Escape could not be registered. Another application owns that shortcut.");
    }
    void Dismiss()
    {
        if(!ready&&busy){Stop();return;}
        bool waiting=frameSent&&busy;StopVisual();busy=waiting;
        Status(waiting?"Translation dismissed · finishing pending inference":"Translation dismissed · releasing models after 10 seconds idle");
        ArmModelIdle();
    }
    Screen SelectedScreen()=>Screen.AllScreens[Math.Clamp(monitor,0,Screen.AllScreens.Length-1)];
    void Publish()
    {
        if(!navigated||exiting)return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {kind="state",status,lastError,ready,busy,original,filter,
            config,models,timing=lastTiming,monitor,suiteOwner=XiaomiRevamp.Suite.SuiteEnvironment.Enabled?SuiteClientHubOwner():"",suiteBindings=XiaomiRevamp.Suite.SuiteEnvironment.Enabled?XiaomiRevamp.Suite.SuiteStore.Read().Bindings:null,hotkeyErrors=suite?.Error is string suiteError?hotkeyErrors.Concat(new[]{suiteError}).ToArray():hotkeyErrors.ToArray(),hasTranslation=overlay.Blocks>0,effectiveProfile=EffectiveProfile(),packaged=Program.Packaged,fonts=DesktopOptions.FontNames,
            pictureVersion=File.GetLastWriteTimeUtc(Path.Combine(Program.Data,"appearance","picture.png")).Ticks,
            monitors=Screen.AllScreens.Select((s,i)=>new {id=i,name=s.DeviceName,width=s.Bounds.Width,height=s.Bounds.Height,primary=s.Primary})}));
    }
    void Status(string text){status=text;Program.Log(text);Publish();}
    void StartBackend()
    {
        if(backend is {HasExited:false})return;
        backend?.Dispose();backend=null;
        var process=new Process {StartInfo=new ProcessStartInfo(Program.Python)
        {WorkingDirectory=Program.Root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
         StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8},EnableRaisingEvents=true};
        process.StartInfo.ArgumentList.Add("-u");process.StartInfo.ArgumentList.Add("-m");process.StartInfo.ArgumentList.Add("screen_translator.backend");
        process.StartInfo.Environment["PYTHONIOENCODING"]="utf-8";
        process.StartInfo.Environment["HF_HUB_OFFLINE"]="1";process.StartInfo.Environment["TRANSFORMERS_OFFLINE"]="1";
        process.StartInfo.Environment["SCREEN_TRANSLATOR_DATA"]=Program.CacheData;
        process.StartInfo.Environment["HF_HOME"]=Path.Combine(Program.Data,"cache","huggingface");
        process.StartInfo.Environment["TORCH_HOME"]=Path.Combine(Program.Data,"cache","torch");
        if(Program.Packaged){process.StartInfo.Environment["PYTHONNOUSERSITE"]="1";process.StartInfo.Environment["PYTHONDONTWRITEBYTECODE"]="1";process.StartInfo.Environment.Remove("PYTHONPATH");process.StartInfo.Environment.Remove("PYTHONHOME");}
        process.OutputDataReceived+=(_,e)=>
        {
            if(e.Data is null)return;
            try{var value=JsonNode.Parse(e.Data) as JsonObject??throw new InvalidDataException("Invalid backend response");Post(()=>{if(backend==process)Receive(value);});}
            catch(Exception error){Post(()=>{if(backend==process)Fail("Backend protocol error: "+error.Message,true);});}
        };
        process.ErrorDataReceived+=(_,e)=>{if(e.Data is not null)Program.Log("backend: "+e.Data);};
        process.Exited+=(_,_)=>Post(()=>{if(!exiting&&backend==process)Fail($"Python backend stopped (0x{process.ExitCode:X8}). Reload models to restart.",true);});
        if(!process.Start())throw new InvalidOperationException("Could not start the selected Python interpreter");
        backend=process;process.BeginOutputReadLine();process.BeginErrorReadLine();
    }
    void Post(Action action){if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(action);}catch(InvalidOperationException){}}
    string Send(string command,object payload)
    {
        StartBackend();string id=(++serial).ToString();
        string message=JsonSerializer.Serialize(new {id,command,payload});
        var process=backend!;
        // The serial pipe is written away from the UI thread; a large capture cannot freeze controls.
        pipeWrites=pipeWrites.ContinueWith(_=>
        {
            try{lock(pipeLock){if(!process.HasExited){process.StandardInput.WriteLine(message);process.StandardInput.Flush();}}}
            catch(Exception error){Post(()=>{if(backend==process)Fail(error.Message,true);});}
        },TaskScheduler.Default);
        return id;
    }
    void LoadModels(int action=0,Rectangle? bounds=null)
    {
        StopVisual();pendingAction=action;resumeBounds=bounds;ready=false;busy=true;models=null;lastError="";
        activeId=Send("load",config);Status("Loading and warming local models");
    }
    void StopVisual(){epoch++;filter=false;original=false;filterTimer.Stop();visibilityTimer.Stop();modelIdle.Stop();ResetPage();overlay.Clear();toolbar.Hide();target=null;pendingCapture?.Dispose();pendingCapture=null;if(hotkeys.Remove(5))Native.UnregisterHotKey(Handle,5);}
    void Stop()
    {
        StopVisual();UnloadModels("Inference stopped · memory released");
    }
    internal void StartHidden(){_=Handle;_=Initialize();}
    bool ModelIdle=>ready&&!busy&&!selecting&&pendingAction==0&&(!filter||original||ControlsOpen);
    void ArmModelIdle(){modelIdle.Stop();if(ModelIdle&&backend is not null)modelIdle.Start();}
    void UnloadModels(string message)
    {
        modelIdle.Stop();pendingAction=0;resumeBounds=null;activeId="";ready=busy=frameSent=false;
        var process=backend;backend=null;pipeWrites=Task.CompletedTask;
        if(process is not null){try{if(!process.HasExited)process.Kill(entireProcessTree:true);}finally{process.Dispose();}}
        Program.Log("Models released; compiled cache retained on disk");Status(message);
    }
    void Receive(JsonObject message)
    {
        string? id=message["id"]?.GetValue<string>();string kind=message["event"]!.GetValue<string>();
        if(kind=="stopped")return;
        if(id is not null&&id!=activeId)return;
        var value=message["value"];
        if(kind=="progress"){Status(value!.GetValue<string>());return;}
        if(kind=="ready")
        {
            models=value?.DeepClone();ready=true;busy=false;Status("Models ready · fully local");
            Program.Log("Execution devices: "+new JsonObject {
                ["detector"]=models?["ocr"]?["detector"]?["execution_devices"]?.DeepClone(),
                ["recognizer"]=models?["ocr"]?["recognizer"]?["execution_devices"]?.DeepClone(),
                ["translator"]=models?["translator"]?["execution_devices"]?.DeepClone()}.ToJsonString());
            int requested=pendingAction;var bounds=resumeBounds;long generation=epoch;pendingAction=0;resumeBounds=null;
            if(requested!=0){toolbar.Hide();Post(()=>{if(ready&&epoch==generation){if(requested==4)target=bounds;_=Hotkey(requested);}});}else ArmModelIdle();
            return;
        }
        if(kind=="failed")
        {Fail(value?["message"]?.GetValue<string>()??"Inference failed",value?["engine_stopped"]?.GetValue<bool>()??true);return;}
        if(kind=="result")
        {
            if(pendingCapture is null||target is null){busy=false;frameSent=false;ArmModelIdle();Publish();return;}
            ObservePage(force:true);
            busy=false;frameSent=false;
            bool superseded=pendingPageVersion!=pageVersion;
            try
            {
                using var capture=pendingCapture;pendingCapture=null;
                using var document=JsonDocument.Parse(value!.ToJsonString());var result=document.RootElement;
                var render=Stopwatch.StartNew();
                if(redrawOverlay||overlay.Masked||!(result.GetProperty("timing").GetProperty("frame_cache_hit").GetBoolean()&&overlay.Blocks>0))
                    overlay.Replace(capture,result.GetProperty("regions"),target.Value,(float)config["font_scale"]!.GetValue<double>(),config["debug"]!.GetValue<bool>(),present:!superseded&&!ControlsOpen&&!original&&(!config["toolbar_focus_only"]!.GetValue<bool>()||toolbar.IsActive));
                if(superseded)overlay.MaskChanged(pendingChanges.ToArray());
                pendingChanges.Clear();partialLayer=superseded&&overlay.HasVisibleText;
                redrawOverlay=false;
                stale=superseded;if(!stale)dirtySince=0;
                toolbar.Present(target.Value,original,filter,overlay.Blocks,overlay.Clipped);
                SyncOverlayVisibility();
                render.Stop();lastTiming=value["timing"]!.DeepClone();lastTiming["capture_ms"]=captureMs;lastTiming["layout_render_ms"]=render.Elapsed.TotalMilliseconds;
                lastTiming["total_hotkey_ms"]=requestTime.Elapsed.TotalMilliseconds;lastTiming["capture_size"]=new JsonArray(capture.Width,capture.Height);
                lastTiming["clipped_regions"]=overlay.Clipped;lastTiming["capture_excluded"]=overlay.CaptureExcluded;
                File.AppendAllText(Path.Combine(Program.Data,"timings.jsonl"),lastTiming.ToJsonString()+"\n");
                Status(original?"Original view · paused":overlay.Blocks==0?"No Chinese text recognized in this capture":$"Translated · {overlay.Blocks} replacements · {overlay.Clipped} do not fit");
            if(filter&&!overlay.CaptureExcluded&&overlay.Blocks>0){filter=false;filterTimer.Stop();Status("Filter paused: Windows capture exclusion is unavailable. One-shot translation remains available.");}
            ArmModelIdle();
            }
            catch(Exception error){Fail(error.Message,false);}return;
        }
        if(kind=="downloaded"){config["models"]=value!["models"]!.GetValue<string>();SaveConfig();LoadModels();return;}
        if(kind=="benchmarked")
        {
            string report=value!["report"]!.GetValue<string>();var summary=JsonNode.Parse(File.ReadAllText(report))!;
            config["mode"]="Automatic";config["benchmark"]=report;config["devices"]=summary["fastest_measured_devices"]!.DeepClone();SaveConfig();LoadModels();return;
        }
        if(kind=="cleared"){busy=false;Status("Resident caches cleared");}
    }
    void Fail(string error,bool stopped)
    {
        busy=false;frameSent=false;StopVisual();if(stopped)UnloadModels("Failed engine released");
        lastError=error;Status("Action failed: "+error);ShowControls();
    }
    async Task Translate(bool region)
    {
        if(!ready||busy||selecting)return;
        StopVisual();if(region)Hide();
        if(region)
        {
            selecting=true;
            using var selector=new RegionSelector(SelectedScreen().Bounds);
            DialogResult selected;
            try{selected=selector.ShowDialog();}finally{selecting=false;}
            if(selected!=DialogResult.OK){ShowControls();return;}
            var selection=selector.Selection;selection.Offset(SelectedScreen().Bounds.Location);target=selection;
        }
        else target=SelectedScreen().Bounds;
        ArmEscape();
        if(config["toolbar_focus_only"]!.GetValue<bool>()) {toolbar.Present(target.Value,original,filter,overlay.Blocks,overlay.Clipped);toolbar.FocusForAction();await Task.Delay(60);}
        Hide();
        await CaptureTarget();
    }
    async Task CaptureTarget()
    {
        if(!CanCapture()||target is not Rectangle bounds)return;
        ObservePage();
        if(config["cache"]!.GetValue<bool>()&&!stale&&!redrawOverlay&&sentPageVersion==pageVersion)return;
        if(!Screen.AllScreens.Any(s=>s.Bounds.Contains(target.Value))){StopVisual();Status("Display geometry changed. Select a monitor or region again.");return;}
        busy=true;frameSent=false;long currentEpoch=epoch;requestTime.Restart();Publish();
        try
        {
            if(overlay.Visible&&!overlay.CaptureExcluded)overlay.Hide();
            if(toolbar.Visible&&!toolbar.CaptureExcluded)toolbar.Hide();
            bool controlsVisible=ControlsOpen;Hide();if(controlsVisible)await Task.Delay(80);
            if(currentEpoch!=epoch||exiting){busy=false;Publish();return;}
            ObservePage(force:true);
            // Bind validity before capturing; changes during capture/encoding belong to pendingChanges.
            pendingPageVersion=pageVersion;sentPageVersion=pageVersion;pendingChanges.Clear();lastFrame=Environment.TickCount64;
            var watch=Stopwatch.StartNew();using var image=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb);
            using(var graphics=Graphics.FromImage(image))graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
            watch.Stop();captureMs=watch.Elapsed.TotalMilliseconds;
            ObservePage(force:true);
            pendingCapture?.Dispose();pendingCapture=(Bitmap)image.Clone();
            // PNG transport remains in RAM. This copy/encode cost is included in total hotkey time.
            string png=await Task.Run(()=>{using var stream=new MemoryStream();image.Save(stream,ImageFormat.Png);return Convert.ToBase64String(stream.ToArray());});
            if(currentEpoch!=epoch||exiting){busy=false;Publish();return;}
            frameSent=true;
            activeId=Send("frame",new {png,key=new object[]{SelectedScreen().DeviceName,bounds.X,bounds.Y,bounds.Width,bounds.Height},cache=config["cache"]!.GetValue<bool>()});
            Status("Translating locally");
        }
        catch(Exception error){if(currentEpoch==epoch)Fail(error.Message,false);}
    }
    bool CanCapture()=>ready&&!busy&&!original&&target is not null;
    async Task ToggleOriginal()
    {
        if(target is null)return;
        if(original&&!ready&&(filter||stale)){await Hotkey(filter?4:1);return;}
        Hide();
        original=!original;
        SyncOverlayVisibility();
        toolbar.Present(target.Value,original,filter,overlay.Blocks,overlay.Clipped);Publish();
        if(original){ArmModelIdle();Status("Original view · translation paused");return;}
        if((stale||overlay.Blocks==0)&&!busy)await CaptureTarget();else Status("Translated view · cached replacement");
    }
    async Task SetFilter(bool enabled)
    {
        if(enabled&&!ready)return;
        filter=enabled;
        if(enabled)
        {
            original=false;if(target is null)target=SelectedScreen().Bounds;ArmEscape();
            toolbar.Present(target.Value,original,filter,overlay.Blocks,overlay.Clipped);
            if(config["toolbar_focus_only"]!.GetValue<bool>())toolbar.FocusForAction();SyncOverlayVisibility();
            filterTimer.Interval=RefreshInterval();filterTimer.Start();await CaptureTarget();
        }
        else filterTimer.Stop();if(target is Rectangle bounds)toolbar.Present(bounds,original,filter,overlay.Blocks,overlay.Clipped);ArmModelIdle();Publish();
    }
    void SaveConfig()=>File.WriteAllText(Path.Combine(Program.Data,"config.json"),config.ToJsonString(new JsonSerializerOptions {WriteIndented=true}));
    async Task Action(JsonElement message)
    {
        string action=message.GetProperty("action").GetString()??"";
        switch(action)
        {
            case "state":Publish();break;
            case "suite-manager":OpenSuiteManager();break;
            case "screen":await Hotkey(1);break;
            case "region":await Hotkey(2);break;
            case "original":await ToggleOriginal();break;
            case "filter":await Hotkey(4);break;
            case "stop":Stop();break;
            case "reload":if(!busy)LoadModels();break;
            case "monitor":if(busy)return;monitor=Math.Clamp(message.GetProperty("value").GetInt32(),0,Screen.AllScreens.Length-1);StopVisual();Publish();break;
            case "save":
                var next=JsonNode.Parse(message.GetProperty("config").GetRawText()) as JsonObject??throw new ArgumentException("Invalid settings");
                next["mode"]="Managed";next["batch_size"]=8;ValidateConfig(next);
                bool reload= new[]{"models","cache","incremental"}.Any(key=>!JsonNode.DeepEquals(config[key],next[key]));
                redrawOverlay|=new[]{"font_scale","font_family","font_fit","display_style","debug"}.Any(key=>!JsonNode.DeepEquals(config[key],next[key]));
                if(reload)Stop();ApplyDesktopSettings(next);config=next;SaveConfig();
                if(reload)LoadModels();else {filterTimer.Interval=RefreshInterval();SyncOverlayVisibility();Status("Settings saved");}break;
            case "browse-picture":ChoosePicture();break;
            case "record-shortcut":RecordShortcut();break;
            case "browse-screenshots":ChooseScreenshotFolder();break;
            case "screenshot":await SaveTranslatedScreenshot();break;
            case "reset-picture":config["picture"]="";SaveConfig();Publish();break;
            case "browse-models":
                using(var dialog=new FolderBrowserDialog {Description="Local model bundle",SelectedPath=config["models"]!.GetValue<string>()})
                    if(dialog.ShowDialog(this)==DialogResult.OK)web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {kind="path",field="models",value=dialog.SelectedPath}));break;
            case "browse-report":
                using(var dialog=new OpenFileDialog {Filter="Benchmark summary|*.json"})
                    if(dialog.ShowDialog(this)==DialogResult.OK)web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {kind="path",field="benchmark",value=dialog.FileName}));break;
            case "clear":if(ready&&!busy){busy=true;activeId=Send("clear",new {});Publish();}break;
            case "download":
                if(busy)return;
                if(MessageBox.Show(this,"Verify the fixed PP-OCRv4 and OPUS Chinese-to-English bundle? Matching local files are reused. Only missing files are downloaded from Hugging Face (about 687 MB if empty). This is not a marketplace or model upgrade. Changed/corrupt files require a new writable bundle folder.","Verify / restore bundled models",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
                StopVisual();ready=false;busy=true;activeId=Send("download",new {models=config["models"]!.GetValue<string>()});Status("Verifying local bundle; restoring missing files if needed");break;
            case "benchmark":await Benchmark();break;
            case "diagnostics":
                using(var dialog=new SaveFileDialog {Filter="JSON|*.json",FileName="screen-translator-diagnostics.json"})
                    if(dialog.ShowDialog(this)==DialogResult.OK)File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(new {config,models,timing=lastTiming,lastError},new JsonSerializerOptions {WriteIndented=true}));break;
            case "drag":Native.ReleaseCapture();Native.SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);break;
            case "minimize":WindowState=FormWindowState.Minimized;break;
            case "maximize":WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;break;
            case "close":HideControls();break;
            case "exit":Quit();break;
            default:throw new ArgumentException("Unknown frontend action");
        }
    }
    async Task Benchmark()
    {
        if(busy)return;
        if(MessageBox.Show(this,"Capture the selected monitor in RAM and compare CPU, GPU and NPU model profiles? This can take several minutes. Timings are saved; screen content is not.","Benchmark hardware",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        StopVisual();ready=false;busy=true;Hide();long currentEpoch=epoch;await Task.Delay(80);
        if(currentEpoch!=epoch)return;
        try
        {
            var bounds=SelectedScreen().Bounds;using var image=new Bitmap(bounds.Width,bounds.Height);
            using(var graphics=Graphics.FromImage(image))graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);
            string png=await Task.Run(()=>{using var stream=new MemoryStream();image.Save(stream,ImageFormat.Png);return Convert.ToBase64String(stream.ToArray());});
            if(currentEpoch!=epoch)return;
            activeId=Send("benchmark",new {png,key=new object[]{SelectedScreen().DeviceName,bounds.X,bounds.Y,bounds.Width,bounds.Height},cache=false,
                models=config["models"]!.GetValue<string>(),batch_size=config["batch_size"]!.GetValue<int>(),results=Path.Combine(Program.Data,"results","calibration")});
            ShowControls();Status("Benchmarking actual hardware profiles");
        }
        catch(Exception error){Fail(error.Message,true);}
    }
    void DisplayChanged(object? sender,EventArgs e)=>Post(()=>{StopVisual();if(busy)Stop();Status("Displays changed. Select a monitor or region again.");});
    async Task CheckSuiteUi()
    {
        try
        {
            await Task.Delay(200);
            await web.CoreWebView2.ExecuteScriptAsync("page('settings');document.getElementById('models').focus();");
            void Update(string shortcut) => web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {kind="suite-settings",shortcut,theme="dark",accent="#9352d7"}));
            Update("Ctrl+Alt+F20");await Task.Delay(150);
            string applied=await web.CoreWebView2.ExecuteScriptAsync("Boolean(state&&state.config.shortcut==='Ctrl+Alt+F20'&&document.getElementById('shortcut').value==='Ctrl+Alt+F20'&&state.config.theme==='dark'&&document.getElementById('accent').value==='#9352d7')");
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('shortcut').focus();setShortcut('Ctrl+Alt+Z');");
            Update("Ctrl+Shift+F20");await Task.Delay(150);
            string preserved=await web.CoreWebView2.ExecuteScriptAsync("Boolean(state.config.shortcut==='Ctrl+Shift+F20'&&document.getElementById('shortcut').value==='Ctrl+Alt+Z')");
            string reset=await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('shortcut').blur();document.getElementById('reset-settings').click();document.getElementById('shortcut').value==='Ctrl+Shift+F20'");
            string directory=Path.Combine(Program.Data,"results","suite-ui",DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ"));Directory.CreateDirectory(directory);
            string controls=await web.CoreWebView2.ExecuteScriptAsync("window.checkDevelopmentUi()");
            bool controlsPassed=JsonNode.Parse(controls)?["ok"]?.GetValue<bool>()==true;
            var report=new {passed=applied=="true"&&preserved=="true"&&reset=="true"&&controlsPassed,sharedSettingsApplied=applied=="true",activeEditPreserved=preserved=="true",resetUsesSyncedValue=reset=="true",coldControlsEnabled=controlsPassed,backendStarted=false,desktopCaptured=false};
            File.WriteAllText(Path.Combine(directory,"config.json"),JsonSerializer.Serialize(new {seed=0,mode="suite-webview-shortcut-sync",root=Program.Root,protocol="Native WebView messages, editable shortcut and reset button",shortcut="Ctrl+Alt+F20",updatedShortcut="Ctrl+Shift+F20"}));
            File.WriteAllText(Path.Combine(directory,"summary.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            if(!report.passed)Environment.ExitCode=1;
            Program.Log("Suite UI check saved: "+directory);
        }
        catch(Exception error){Program.Log(error.ToString());Environment.ExitCode=1;}
        finally{Quit();}
    }
    async Task CheckUi()
    {
        try
        {
            bool continuousDefault=!Defaults()["toolbar_focus_only"]!.GetValue<bool>()&&Defaults()["refresh_version"]!.GetValue<int>()==2;
            config["toolbar_focus_only"]=false;
            stale=false;
            var appearance=config.DeepClone().AsObject();appearance["theme"]="dark";appearance["accent"]="#9352d7";ValidateConfig(appearance);ApplyAppearance(appearance);
            bool appearanceApplied=toolbar.BackColor==Color.FromArgb(23,26,31)&&overlay.Accent==ColorTranslator.FromHtml("#9352d7");ApplyAppearance(config);
            await Task.Delay(200);
            string json=await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.checkDevelopmentUi())");
            string report=JsonSerializer.Deserialize<string>(json)!;var result=JsonNode.Parse(report)!;
            result["nativeAppearanceApplied"]=appearanceApplied;
            result["continuousUseDefault"]=continuousDefault;
            bool bindings=ShortcutBinding.Parse("Ctrl+Shift+T")==new ShortcutBinding((uint)Keys.T,6)&&
                ShortcutBinding.Parse("A")==new ShortcutBinding((uint)Keys.A,0)&&ShortcutBinding.Parse("9").ToString()=="9"&&
                ShortcutBinding.Parse("Copilot").IsCopilot&&ShortcutBinding.Parse("Win+C").IsCopilot&&ShortcutBinding.Parse("F8").ToString()=="F8";
            foreach(string invalid in new[]{"", "Ctrl", "Escape", "Win+L", "Ctrl+Alt+Delete", "A+B", "Ctrl+Ctrl+A"})
            {try{ShortcutBinding.Parse(invalid);bindings=false;}catch(ArgumentException){}}
            result["customShortcutParsing"]=bindings;
            int presses=0;bool manual=false;
            using(var listener=new KeyboardHook((key,mods)=>manual&&key==(uint)Keys.F23&&mods==12,(_,_)=>presses++))
            {
                manual=true;
                foreach(uint key in new uint[]{0x10,0x11,0x12,0x5b,0x5c,0xa0,0xa1,0xa2,0xa3,0xa4,0xa5})listener.Process(key,false,false);
                bool unrelated=!listener.Process((uint)Keys.A,true,false)&&!listener.Process((uint)Keys.A,false,false);
                listener.Process(0x5b,true,false);listener.Process(0xa0,true,false);
                bool down=listener.Process((uint)Keys.F23,true,false),repeat=listener.Process((uint)Keys.F23,true,false);
                listener.Process(0xa0,false,false);listener.Process(0x5b,false,false);
                bool up=listener.Process((uint)Keys.F23,false,false);manual=false;
                result["copilotChordRepeatAndRelease"]=unrelated&&down&&repeat&&up&&presses==1&&listener.Modifiers==0;
            }
            using(var registered=new GlobalShortcut(Handle,ShortcutBinding.Parse("Ctrl+Alt+F24"),()=>{}))
            {
                bool collision=false;try{using var duplicate=new GlobalShortcut(Handle,registered.Binding,()=>{});}catch(ArgumentException){collision=true;}
                result["shortcutConflictPreservesExisting"]=collision&&registered.Id>0;
            }
            using(var iconBitmap=Icon!.ToBitmap())
            {
                bool monochrome=true;
                for(int y=0;y<iconBitmap.Height;y++)for(int x=0;x<iconBitmap.Width;x++)
                {var pixel=iconBitmap.GetPixel(x,y);monochrome&=pixel.R==pixel.G&&pixel.G==pixel.B;}
                result["monochromeAppIcon"]=monochrome;
            }
            // Render only generated content; inspect native geometry/affinity without desktop capture.
            using(var image=new Bitmap(220,80))
            {
                using(var graphics=Graphics.FromImage(image))graphics.Clear(Color.White);
                using var document=JsonDocument.Parse("[{\"polygon\":[[10,10],[120,10],[120,36],[10,36]],\"source\":\"设置\",\"translated\":\"Settings\"}]");
                var bounds=new Rectangle(100,100,220,80);overlay.Replace(image,document.RootElement,bounds,1,false);
                result["overlayGeometry"]=overlay.Bounds==bounds&&overlay.Blocks==1&&overlay.Region?.IsVisible(20,20)==true&&overlay.Region?.IsVisible(200,60)==false;
                result["overlayBounds"]=JsonSerializer.SerializeToNode(new {overlay.Bounds.X,overlay.Bounds.Y,overlay.Bounds.Width,overlay.Bounds.Height});
                result["hostDpi"]=DeviceDpi;
                result["overlayCaptureExcluded"]=overlay.CaptureExcluded;result["clippedSyntheticRegions"]=overlay.Clipped;
                result["paintDpiChecks"]=JsonSerializer.SerializeToNode(overlay.CheckPaintDpi());
                result["paintDpiAligned"]=result["paintDpiChecks"]!.AsArray().All(check=>check!["ok"]!.GetValue<bool>());
                using(var layout=Graphics.FromImage(image))
                {
                    float before=overlay.Region!.GetBounds(layout).Height;
                    overlay.AutoFit=false;overlay.Replace(image,document.RootElement,bounds,2,false);
                    result["largerTextUsesBlankSpace"]=overlay.Region!.GetBounds(layout).Height>before+8&&overlay.Clipped==0;
                    overlay.DisplayStyle="plain";overlay.Replace(image,document.RootElement,bounds,1,false);
                    overlay.FontFamilyName=DesktopOptions.FontNames.Contains("Arial")?"Arial":"Segoe UI";
                    overlay.DisplayStyle="contrast";overlay.Replace(image,document.RootElement,bounds,1.5f,false);
                    result["customFontAndContrastPaintDpi"]=JsonSerializer.SerializeToNode(overlay.CheckPaintDpi())!.AsArray().All(check=>check!["ok"]!.GetValue<bool>());
                    overlay.FontFamilyName="Segoe UI";overlay.AutoFit=true;overlay.DisplayStyle="underline";
                    overlay.Replace(image,document.RootElement,bounds,1,false);
                }
                int visibilityChanges=0;EventHandler changed=(_,_)=>visibilityChanges++;overlay.VisibleChanged+=changed;
                overlay.Replace(image,document.RootElement,bounds,1,false);overlay.VisibleChanged-=changed;
                result["refreshDoesNotHideOverlay"]=visibilityChanges==0&&overlay.Visible;
                ready=true;target=bounds;ArmEscape();
                await ToggleOriginal();bool hidden=original&&!overlay.Visible;
                await ToggleOriginal();result["originalSwitchReusesCanvas"]=hidden&&!original&&overlay.Visible&&!busy;
                Show();Activate();await Task.Delay(60);toolbar.Present(bounds,false,false,1,0);await Task.Delay(60);
                result["toolbarDoesNotStealFocus"]=ContainsFocus;
                config["toolbar_focus_only"]=true;
                Program.Log("Focus check: activating toolbar");toolbar.FocusForAction();await Task.Delay(100);
                Program.Log($"Focus before hide: {Native.GetForegroundWindow()} / {Native.GetActiveWindow()} / {toolbar.Handle}");
                Program.Log("Focus check: hiding controls");Hide();
                Program.Log("Focus check: waiting");await Task.Delay(100);SyncOverlayVisibility();
                Program.Log($"Focus after hide: {Native.GetForegroundWindow()} / {Native.GetActiveWindow()} / {toolbar.Handle}");
                bool focusedVisible=toolbar.IsActive&&overlay.Visible;
                result["focusedToolbarProbe"]=JsonSerializer.SerializeToNode(new {active=toolbar.IsActive,toolbarVisible=toolbar.Visible,toolbarOwner=toolbar.Owner?.Handle.ToInt64(),toolbarNativeOwner=Native.GetWindow(toolbar.Handle,4).ToInt64(),overlayVisible=overlay.Visible,controlsVisible=Visible,original,targetNull=target is null,blocks=overlay.Blocks,foreground=Native.GetForegroundWindow().ToInt64(),toolbarHandle=toolbar.Handle.ToInt64(),overlayHandle=overlay.Handle.ToInt64(),controlsHandle=Handle.ToInt64()});
                Show();Activate();await Task.Delay(60);SyncOverlayVisibility();
                result["toolbarFocusSetting"]=focusedVisible&&!overlay.Visible;
                config["toolbar_focus_only"]=false;Hide();SyncOverlayVisibility();
                result["translationCanRemainVisibleWithoutToolbarFocus"]=overlay.Visible;
                config["toolbar_focus_only"]=true;
                Show();Activate();await Task.Delay(60);
                result["refreshDoesNotRequireToolbarFocus"]=!toolbar.IsActive&&CanCapture();
                config["toolbar_focus_only"]=false;Hide();
                using(var changedImage=new Bitmap(320,200))
                using(var changedRegions=JsonDocument.Parse("[{\"polygon\":[[10,10],[100,10],[100,36],[10,36]],\"source\":\"设置\",\"translated\":\"Settings\"},{\"polygon\":[[220,100],[300,100],[300,126],[220,126]],\"source\":\"取消\",\"translated\":\"Cancel\"}]"))
                {
                    using(var graphics=Graphics.FromImage(changedImage))graphics.Clear(Color.White);
                    target=new Rectangle(100,100,320,200);
                    overlay.Replace(changedImage,changedRegions.RootElement,target.Value,1,false);
                    busy=true;pendingCapture=(Bitmap)changedImage.Clone();pendingPageVersion=pageVersion;activeId="synthetic-scroll";
                    InvalidatePage(new[]{new Rectangle(0,0,140,60)});SyncOverlayVisibility();
                    for(int step=0;step<20;step++)InvalidatePage(new[]{new Rectangle(0,0,140,60)});
                    result["scrollChangesDeduplicated"]=pendingChanges.Count==1;
                    result["localChangePreservesOtherText"]=overlay.Visible&&overlay.Masked&&overlay.Region!.IsVisible(230,110)&&!overlay.Region.IsVisible(20,20);
                    // A changed corner must not starve unrelated results, or restore the old corner.
                    var timing=new JsonObject{["frame_cache_hit"]=false};requestTime.Restart();
                    Receive(new JsonObject{["id"]=activeId,["event"]="result",["value"]=new JsonObject{["regions"]=JsonNode.Parse(changedRegions.RootElement.GetRawText()),["timing"]=timing}});
                    result["pendingChangeCannotRestoreStaleWords"]=stale&&partialLayer&&overlay.Visible&&overlay.Region!.IsVisible(230,110)&&!overlay.Region.IsVisible(20,20)&&!busy;
                    overlay.MaskChanged(new[]{new Rectangle(0,0,320,200)});
                    result["fullyChangedCanvasCannotReappear"]=!overlay.HasVisibleText&&!overlay.Reveal();
                }
                target=bounds;overlay.Replace(image,document.RootElement,bounds,1,false);stale=false;partialLayer=false;
                InvalidatePage();SyncOverlayVisibility();
                result["pageChangeClearsOldLayer"]=stale&&!overlay.Visible&&target is not null;
                InvalidatePage(new[]{new Rectangle(190,60,20,10)});SyncOverlayVisibility();
                result["pageChangeCannotRestoreOldCanvas"]=!overlay.Visible&&!overlay.HasVisibleText;
                stale=false;overlay.Replace(image,document.RootElement,bounds,1,false);SyncOverlayVisibility();
                byte[] a=new byte[192000],b=new byte[192000];b[0]=255;
                bool ignoresCaret=!PageProbe.Changed(a,b);for(int i=0;i<90;i++)b[i]=255;
                result["pageProbeRejectsChangedContent"]=ignoresCaret&&PageProbe.Changed(a,b);
                var localChanges=PageProbe.Changes(a,b,new Size(3200,2000));
                result["probeLocalizesChanges"]=localChanges.Count==2&&localChanges.All(r=>r.Bottom<=210)&&localChanges.All(r=>r.Right<=410);
                Array.Fill(b,(byte)255);var probeTime=Stopwatch.StartNew();
                for(int step=0;step<100;step++)PageProbe.Changes(a,b,new Size(3120,2080));
                probeTime.Stop();result["fullChangeComparisonMs"]=probeTime.Elapsed.TotalMilliseconds/100;
                result["scrollRefreshDebouncedAndBounded"]=!RefreshDue(800,700,750,500,600,160)&&!RefreshDue(1000,700,950,0,600,160)&&RefreshDue(1200,700,1000,0,600,160)&&RefreshDue(1750,700,1720,0,600,160);
                var savedBounds=Bounds;
                using(var minimize=JsonDocument.Parse("{\"action\":\"minimize\"}"))await Action(minimize.RootElement);
                await Task.Delay(120);bool minimized=WindowState==FormWindowState.Minimized&&!ControlsOpen;
                ShowControls();await Task.Delay(120);
                result["nativeMinimizeRestore"]=minimized&&WindowState==FormWindowState.Normal&&ControlsOpen&&Bounds==savedBounds&&Region is null&&(CreateParams.Style&0xC20000)==0xC20000;
                Hide();
                config["profile"]="Eco";result["ecoReducesRefresh"]=RefreshInterval()>=1500;config["profile"]="Auto";
                // Busy keeps the timer from capturing while exercising both real filter transitions.
                busy=true;await SetFilter(true);bool started=filter&&filterTimer.Enabled;
                await SetFilter(false);result["filterCanDisengage"]=started&&!filter&&!filterTimer.Enabled;
                busy=true;frameSent=true;await Hotkey(5);
                result["escapeDismissesPendingTranslation"]=target is null&&!overlay.Visible&&!toolbar.Visible&&!filter&&ready&&busy&&!hotkeys.Contains(5);
                Receive(new JsonObject {["id"]=activeId,["event"]="result",["value"]=new JsonObject()});
                result["dismissedResultCannotReappear"]=!busy&&!overlay.Visible;
                result["ok"]=result["ok"]!.GetValue<bool>()&&result["overlayGeometry"]!.GetValue<bool>()&&result["overlayCaptureExcluded"]!.GetValue<bool>()&&result["paintDpiAligned"]!.GetValue<bool>()&&result["originalSwitchReusesCanvas"]!.GetValue<bool>()&&result["filterCanDisengage"]!.GetValue<bool>()&&result["escapeDismissesPendingTranslation"]!.GetValue<bool>()&&result["dismissedResultCannotReappear"]!.GetValue<bool>();
                foreach(string check in new[]{"refreshDoesNotHideOverlay","toolbarDoesNotStealFocus","toolbarFocusSetting","translationCanRemainVisibleWithoutToolbarFocus","pageChangeClearsOldLayer","pageChangeCannotRestoreOldCanvas","pageProbeRejectsChangedContent","ecoReducesRefresh","nativeAppearanceApplied","refreshDoesNotRequireToolbarFocus","localChangePreservesOtherText","pendingChangeCannotRestoreStaleWords","fullyChangedCanvasCannotReappear","probeLocalizesChanges","scrollRefreshDebouncedAndBounded","scrollChangesDeduplicated","nativeMinimizeRestore","continuousUseDefault","monochromeAppIcon","customShortcutParsing","copilotChordRepeatAndRelease","shortcutConflictPreservesExisting","largerTextUsesBlankSpace","customFontAndContrastPaintDpi"})result["ok"]=result["ok"]!.GetValue<bool>()&&result[check]!.GetValue<bool>();
                busy=ready=frameSent=false;Publish();
                overlay.Clear();
            }
            string directory=Path.Combine(Program.Packaged?Program.Data:Program.Root,"results","frontend");Directory.CreateDirectory(directory);
            int index=Directory.GetDirectories(directory).Select(Path.GetFileName).Select(n=>int.TryParse(n!.Split('_')[0],out int i)?i:0).DefaultIfEmpty().Max()+1;
            directory=Path.Combine(directory,$"{index:000}_{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}_seed0");Directory.CreateDirectory(directory);
            toolbar.Present(new Rectangle(0,0,3120,2080),false,true,66,0);toolbar.SavePreview(Path.Combine(directory,"toolbar.png"));toolbar.Hide();
            if(Program.Fixture.Length>0)
            {
                // Only the explicit generated-fixture directory is read; never capture the desktop.
                using var fixture=new Bitmap(Path.Combine(Program.Fixture,"input.png"));
                using var regions=JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.Fixture,"regions.json")));
                var renderTime=Stopwatch.StartNew();overlay.Replace(fixture,regions.RootElement,new Rectangle(0,0,fixture.Width,fixture.Height),1,false);renderTime.Stop();
                overlay.SaveComposite(fixture,Path.Combine(directory,"translated-fixture.png"));
                bool latinUntouched=true;using var geometry=Graphics.FromImage(fixture);
                foreach(var region in regions.RootElement.EnumerateArray())
                {
                    if(region.GetProperty("source").GetString()!=region.GetProperty("translated").GetString())continue;
                    var points=region.GetProperty("polygon").EnumerateArray().Select(p=>new PointF(p[0].GetSingle(),p[1].GetSingle())).ToArray();
                    using var intersection=overlay.Region!.Clone();intersection.Intersect(RectangleF.FromLTRB(points.Min(p=>p.X),points.Min(p=>p.Y),points.Max(p=>p.X),points.Max(p=>p.Y)));
                    latinUntouched&=intersection.IsEmpty(geometry);
                }
                result["fixtureRender"]=JsonSerializer.SerializeToNode(new {source=Program.Fixture,blocks=overlay.Blocks,clipped=overlay.Clipped,latinAreaUnmodified=latinUntouched,layoutRenderMs=renderTime.Elapsed.TotalMilliseconds});
                result["ok"]=result["ok"]!.GetValue<bool>()&&latinUntouched&&overlay.Clipped==0;
                Hide();target=new Rectangle(Point.Empty,fixture.Size);original=false;stale=false;partialLayer=false;
                config["screenshot_folder"]=Path.Combine(directory,"screenshots");toolbar.Present(target.Value,false,false,overlay.Blocks,overlay.Clipped);SyncOverlayVisibility();
                bool available=toolbar.ScreenshotAvailable;await SaveTranslatedScreenshot();
                string[] screenshots=Directory.Exists(config["screenshot_folder"]!.GetValue<string>())?Directory.GetFiles(config["screenshot_folder"]!.GetValue<string>(),"*.png"):Array.Empty<string>();
                bool exportMatches=false;
                if(screenshots.Length==1)
                {
                    using var actual=new Bitmap(screenshots[0]);using var expected=new Bitmap(Path.Combine(directory,"translated-fixture.png"));
                    exportMatches=actual.Size==fixture.Size;
                    for(int y=0;y<actual.Height&&exportMatches;y++)for(int x=0;x<actual.Width;x++)
                        if(actual.GetPixel(x,y)!=expected.GetPixel(x,y)){exportMatches=false;break;}
                }
                result["translatedScreenshotMatchesNativeComposite"]=available&&exportMatches&&toolbar.ScreenshotAvailable;
                await ToggleOriginal();result["screenshotDisabledForOriginal"]=!toolbar.ScreenshotAvailable;await ToggleOriginal();
                overlay.MaskChanged(new[]{new Rectangle(Point.Empty,fixture.Size)});SyncOverlayVisibility();
                await SaveTranslatedScreenshot();result["screenshotCannotSaveObsoleteLayer"]=Directory.GetFiles(config["screenshot_folder"]!.GetValue<string>(),"*.png").Length==1&&!toolbar.ScreenshotAvailable;
                foreach(string check in new[]{"translatedScreenshotMatchesNativeComposite","screenshotDisabledForOriginal","screenshotCannotSaveObsoleteLayer"})result["ok"]=result["ok"]!.GetValue<bool>()&&result[check]!.GetValue<bool>();
                target=null;toolbar.Hide();
                var styleReports=new List<object>();
                foreach(string style in new[]{"underline","plain","contrast"})
                {
                    overlay.FontFamilyName=DesktopOptions.FontNames.Contains("Arial")?"Arial":"Segoe UI";overlay.DisplayStyle=style;
                    overlay.Replace(fixture,regions.RootElement,new Rectangle(Point.Empty,fixture.Size),1.5f,false,present:false);
                    bool untouched=true;
                    foreach(var region in regions.RootElement.EnumerateArray().Where(r=>r.GetProperty("source").GetString()==r.GetProperty("translated").GetString()))
                    {
                        var points=region.GetProperty("polygon").EnumerateArray().Select(p=>new PointF(p[0].GetSingle(),p[1].GetSingle())).ToArray();
                        using var intersection=overlay.Region!.Clone();intersection.Intersect(RectangleF.FromLTRB(points.Min(p=>p.X),points.Min(p=>p.Y),points.Max(p=>p.X),points.Max(p=>p.Y)));
                        untouched&=intersection.IsEmpty(geometry);
                    }
                    overlay.SaveComposite(fixture,Path.Combine(directory,$"appearance-{style}.png"));
                    styleReports.Add(new{style,font=overlay.FontFamilyName,fontScale=1.5,fit=overlay.AutoFit,clipped=overlay.Clipped,latinAreaUnmodified=untouched});
                    result["ok"]=result["ok"]!.GetValue<bool>()&&untouched&&overlay.Blocks==66;
                }
                result["appearanceVariants"]=JsonSerializer.SerializeToNode(styleReports);ApplyAppearance(config);
                overlay.Clear();
            }
            File.WriteAllText(Path.Combine(directory,"config.json"),JsonSerializer.Serialize(new {seed=0,mode="native-webview-ui",backendStarted=false,screenCaptured=false,root=Program.Root,settings=config,protocol="DOM settings navigation and responsive control-state checks"},new JsonSerializerOptions {WriteIndented=true}));
            File.WriteAllText(Path.Combine(directory,"summary.json"),result.ToJsonString(new JsonSerializerOptions {WriteIndented=true}));
            // CapturePreview exports this app's own HTML surface, never the desktop or other windows.
            using(var preview=File.Create(Path.Combine(directory,"frontend.png")))
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
            await web.CoreWebView2.ExecuteScriptAsync("page('settings');document.getElementById('font_family').scrollIntoView({block:'start'});");
            await Task.Delay(180);
            using(var preview=File.Create(Path.Combine(directory,"text-settings.png")))
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
            if(!result["ok"]!.GetValue<bool>())Environment.ExitCode=1;
            Program.Log("UI check saved: "+directory);
        }
        catch(Exception error){Program.Log(error.ToString());Environment.ExitCode=1;}
        finally{Quit();}
    }
    void Quit()
    {
        if(exiting)return;exiting=true;StopVisual();
        suite?.Dispose();translationShortcut?.Dispose();translationShortcut=null;
        foreach(int id in hotkeys)Native.UnregisterHotKey(Handle,id);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=DisplayChanged;
        if(backend is not null)
        {
            if(!backend.HasExited)backend.Kill(entireProcessTree:true);
            backend.Dispose();backend=null;
        }
        modelIdle.Dispose();visibilityTimer.Dispose();overlay.Dispose();toolbar.Dispose();tray.Visible=false;tray.Dispose();filterTimer.Dispose();Close();Application.ExitThread();
    }
    protected override void OnFormClosing(FormClosingEventArgs e){if(!exiting&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;HideControls();}base.OnFormClosing(e);}
}
