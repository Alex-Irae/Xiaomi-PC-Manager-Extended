// Purpose: tray lifecycle, local appearance, one-key shortcut and change-gated refresh.
// Dependencies: existing Windows Desktop/.NET 8 and Windows power APIs. Outputs: per-user settings only.
// Command: ./dev.ps1 [SDK options], or ScreenTranslator.exe [--tray].
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace LocalScreenTranslator;

internal static class DesktopOptions
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,int message,IntPtr a,IntPtr b);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string? type,string title);
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [StructLayout(LayoutKind.Sequential)] struct PowerStatus {internal byte ac,flags,percent,saver;internal uint life,fullLife;}
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);
    internal static readonly int WakeMessage=RegisterWindowMessage("LocalScreenTranslator.Show" + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? ".Suite." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : ""));
    internal static readonly int QuitMessage=RegisterWindowMessage("LocalScreenTranslator.Quit" + (XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? ".Suite." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : ""));
    internal static void WakeExisting()=>PostMessage(new IntPtr(0xffff),WakeMessage,IntPtr.Zero,IntPtr.Zero);
    internal static void QuitExisting()=>PostMessage(new IntPtr(0xffff),QuitMessage,IntPtr.Zero,IntPtr.Zero);
    internal static Icon AppIcon(){string path=Path.Combine(Program.Root,"frontend","logo.ico");return File.Exists(path)?new Icon(path):SystemIcons.Application;}
    internal static void Validate(JsonObject value)
    {
        if(!new[]{"system","light","dark"}.Contains(value["theme"]?.GetValue<string>()))throw new ArgumentException("Invalid theme");
        if(!System.Text.RegularExpressions.Regex.IsMatch(value["accent"]?.GetValue<string>()??"","^#[0-9a-fA-F]{6}$"))throw new ArgumentException("Choose an RGB accent colour");
        if(!new[]{"Auto","Performance","Eco"}.Contains(value["profile"]?.GetValue<string>()))throw new ArgumentException("Invalid power profile");
        value["shortcut"]=XiaomiRevamp.Suite.SuiteEnvironment.Enabled?XiaomiRevamp.Suite.SuiteChord.Parse(value["shortcut"]?.GetValue<string>()??"").Text:ShortcutBinding.Parse(value["shortcut"]?.GetValue<string>()??"").ToString();
        if(!FontNames.Contains(value["font_family"]?.GetValue<string>(),StringComparer.OrdinalIgnoreCase))throw new ArgumentException("Choose an installed font");
        if(!new[]{"underline","plain","contrast"}.Contains(value["display_style"]?.GetValue<string>()))throw new ArgumentException("Choose a translation display style");
        _=value["font_fit"]!.GetValue<bool>();
        if(string.IsNullOrWhiteSpace(value["screenshot_folder"]?.GetValue<string>())||!Path.IsPathFullyQualified(value["screenshot_folder"]!.GetValue<string>()))throw new ArgumentException("Choose an absolute screenshot folder path");
        string picture=value["picture"]?.GetValue<string>()??"";
        if(picture.Length>0&&picture!="picture.png")throw new ArgumentException("Invalid local picture");
        _=value["autostart"]!.GetValue<bool>();
    }
    internal static readonly string[] FontNames=InstalledFonts();
    static string[] InstalledFonts()
    {
        using var fonts=new System.Drawing.Text.InstalledFontCollection();
        var names=new List<string>();foreach(var font in fonts.Families){using(font)if(font.IsStyleAvailable(FontStyle.Regular))names.Add(font.Name);}
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static string SystemProfile()
    {
        try
        {
            if(GetSystemPowerStatus(out var status)&&status.saver==1)return "Eco";
            if(PowerGetEffectiveOverlayScheme(out var scheme)==0)
            {
                if(scheme==new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"))return "Eco";
                if(scheme==new Guid("ded574b5-45a0-4f42-8737-46345c09c238")||scheme==new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f"))return "Performance";
                if(scheme==Guid.Empty||scheme==new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"))return "Balanced";
            }
        }
        catch(EntryPointNotFoundException){}catch(DllNotFoundException){}
        return SystemInformation.PowerStatus.PowerLineStatus==PowerLineStatus.Offline?"Eco":"Balanced";
    }
    internal static void Startup(bool enabled)
    {
        if(!Program.Packaged||Program.CheckUi||Program.NoStartup||XiaomiRevamp.Suite.SuiteEnvironment.Portable)return;
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(enabled)key.SetValue(XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? "XiaomiRevampSuite.Translator." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "LocalScreenTranslator","\""+Path.Combine(Program.Root,"ScreenTranslator.exe")+"\" --tray");
        else key.DeleteValue(XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? "XiaomiRevampSuite.Translator." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "LocalScreenTranslator",false);
    }
}

internal sealed partial class MainWindow
{
    byte[]? pageStamp;
    long pageVersion,pendingPageVersion,sentPageVersion=-1;
    bool stale=true;
    long lastProbe;
    long dirtySince,lastChange,lastFrame;
    bool partialLayer;
    readonly HashSet<Rectangle> pendingChanges=new();
    IntPtr pageHook;
    PageProbe.WinEventCallback? pageCallback;
    GlobalShortcut? translationShortcut;
    bool shortcutRecording,savingScreenshot;
    void InitializeDesktop()
    {
        Directory.CreateDirectory(Path.Combine(Program.Data,"appearance"));
        DesktopOptions.Startup(config["autostart"]!.GetValue<bool>());
        ApplyAppearance(config);
        // Only window title changes are observed; no keys or screen contents are logged.
        pageCallback=(hook,ev,window,obj,child,thread,time)=>
        {
            if(obj==0&&window!=Handle&&window!=toolbar.Handle&&window!=overlay.Handle&&window==Native.GetForegroundWindow())Post(()=>{if(target is not null)InvalidatePage();});
        };
        if(!Program.CheckUi)pageHook=PageProbe.SetWinEventHook(0x800C,0x800C,IntPtr.Zero,pageCallback,0,0,2);
        FormClosed+=(_,_)=>{if(pageHook!=IntPtr.Zero)PageProbe.UnhookWinEvent(pageHook);};
    }
    string EffectiveProfile()=>config["profile"]!.GetValue<string>()=="Auto"?DesktopOptions.SystemProfile():config["profile"]!.GetValue<string>();
    int RefreshInterval()=>EffectiveProfile()=="Eco"?Math.Max(1500,config["interval_ms"]!.GetValue<int>()):config["interval_ms"]!.GetValue<int>();
    void RegisterShortcut()
    {
        try{ReplaceShortcut(config["shortcut"]!.GetValue<string>());}
        catch(ArgumentException error){hotkeyErrors.Add(config["shortcut"]!.GetValue<string>()+": "+error.Message);}
        catch(System.ComponentModel.Win32Exception error){hotkeyErrors.Add(error.Message);}
    }
    void ReplaceShortcut(string value)
    {
        var binding=ShortcutBinding.Parse(value);
        if(translationShortcut?.Binding==binding)return;
        // Temporarily release our own legacy shortcut if the user assigns it to filter toggle.
        int collision=binding.Modifiers==3?"FTOD".IndexOf((char)binding.Key)+1:0;
        bool released=collision>0&&hotkeys.Remove(collision);if(released)Native.UnregisterHotKey(Handle,collision);
        GlobalShortcut replacement;
        try{replacement=new GlobalShortcut(Handle,binding,()=>Post(()=>{if(!shortcutRecording)_=Hotkey(6);}));}
        catch{if(released&&Native.RegisterHotKey(Handle,collision,0x4003,binding.Key))hotkeys.Add(collision);throw;}
        translationShortcut?.Dispose();translationShortcut=replacement;
        hotkeyErrors.Clear();
        RegisterLegacyKeys();
    }
    void RegisterLegacyKeys()
    {
        if(Program.CheckUi)return;
        int id=0;foreach(char key in "FTOD")
        {
            id++;if(hotkeys.Contains(id)||translationShortcut?.Binding==new ShortcutBinding(key,3))continue;
            if(Native.RegisterHotKey(Handle,id,0x4003,key))hotkeys.Add(id);else hotkeyErrors.Add("Ctrl+Alt+"+key);
        }
    }
    void RecordShortcut()
    {
        shortcutRecording=true;suite?.Suspend(true);
        try{using var dialog=new XiaomiRevamp.Suite.SuiteShortcutDialog(config["shortcut"]!.GetValue<string>());
            if(dialog.ShowDialog(this)==DialogResult.OK)web.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{kind="path",field="shortcut",value=dialog.Selected}));}
        finally{shortcutRecording=false;suite?.Suspend(false);}
    }
    void ApplyDesktopSettings(JsonObject next)
    {
        // A rejected replacement leaves the existing registration/listener alive.
        if(!JsonNode.DeepEquals(next["shortcut"],config["shortcut"])&&!Program.CheckUi)
        {
            if(XiaomiRevamp.Suite.SuiteEnvironment.Enabled) XiaomiRevamp.Suite.SuiteStore.SetBinding("screen-translator.toggle",next["shortcut"]!.GetValue<string>());
            else ReplaceShortcut(next["shortcut"]!.GetValue<string>());
        }
        DesktopOptions.Startup(next["autostart"]!.GetValue<bool>());
        ApplyAppearance(next);
    }
    void ApplyAppearance(JsonObject next)
    {
        bool dark=next["theme"]!.GetValue<string>()=="dark"||next["theme"]!.GetValue<string>()=="system"&&
            (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1)==0;
        toolbar.Appearance(ColorTranslator.FromHtml(next["accent"]!.GetValue<string>()),dark);
        overlay.Accent=ColorTranslator.FromHtml(next["accent"]!.GetValue<string>());redrawOverlay=true;
        overlay.FontFamilyName=next["font_family"]!.GetValue<string>();overlay.AutoFit=next["font_fit"]!.GetValue<bool>();overlay.DisplayStyle=next["display_style"]!.GetValue<string>();
    }
    void ChooseScreenshotFolder()
    {
        using var dialog=new FolderBrowserDialog{Description="Save translated screenshots here",SelectedPath=config["screenshot_folder"]!.GetValue<string>()};
        if(dialog.ShowDialog(this)==DialogResult.OK)web.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{kind="path",field="screenshot_folder",value=dialog.SelectedPath}));
    }
    async Task SaveTranslatedScreenshot()
    {
        if(savingScreenshot||original||ControlsOpen||target is not Rectangle bounds)return;
        savingScreenshot=true;SyncOverlayVisibility();long currentEpoch=epoch;string? savedFile=null;
        try
        {
            ObservePage(force:true);
            if(!overlay.HasVisibleText){Status("Screenshot waits for a current translation. Let the page settle first.");return;}
            bool restoreOverlay=overlay.Visible,restoreToolbar=toolbar.Visible;
            try
            {
                if(overlay.Visible&&!overlay.CaptureExcluded)overlay.Hide();
                if(toolbar.Visible&&!toolbar.CaptureExcluded)toolbar.Hide();
                if(restoreOverlay&&!overlay.Visible||restoreToolbar&&!toolbar.Visible)await Task.Delay(80);
                if(currentEpoch!=epoch||target!=bounds||original||exiting)return;
                // UI checks use their explicit public fixture, and can never capture a desktop.
                if(Program.CheckUi&&Program.Fixture.Length==0)throw new InvalidOperationException("Screenshot checks require generated content");
                using var image=Program.CheckUi?new Bitmap(Path.Combine(Program.Fixture,"input.png")):new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb);
                if(image.Size!=bounds.Size)throw new InvalidOperationException("Screenshot dimensions changed");
                if(!Program.CheckUi)using(var graphics=Graphics.FromImage(image))graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
                ObservePage(force:true);
                if(currentEpoch!=epoch||target!=bounds||!overlay.HasVisibleText||original)return;
                string folder=config["screenshot_folder"]!.GetValue<string>();Directory.CreateDirectory(folder);
                string file=Path.Combine(folder,$"ScreenTranslator-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
                overlay.SaveComposite(image,file);
                savedFile=file;Status("Translated screenshot saved: "+file);
            }
            finally{if(restoreToolbar&&target is not null)toolbar.Present(bounds,original,filter,overlay.Blocks,overlay.Clipped);SyncOverlayVisibility();}
        }
        catch(Exception error){Status("Screenshot could not be saved: "+error.Message);}
        finally{savingScreenshot=false;SyncOverlayVisibility();if(savedFile is not null)toolbar.ScreenshotSaved();}
    }
    void ChoosePicture()
    {
        using var dialog=new OpenFileDialog {Filter="Pictures|*.png;*.jpg;*.jpeg;*.bmp",Title="Choose a local picture"};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        if(new FileInfo(dialog.FileName).Length>20_000_000)throw new ArgumentException("Choose a picture smaller than 20 MB");
        using var image=Image.FromFile(dialog.FileName);
        using var scaled=new Bitmap(256,256);using(var graphics=Graphics.FromImage(scaled)){graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;double ratio=Math.Max(256.0/image.Width,256.0/image.Height);float width=(float)(image.Width*ratio),height=(float)(image.Height*ratio);graphics.DrawImage(image,(256-width)/2,(256-height)/2,width,height);}
        scaled.Save(Path.Combine(Program.Data,"appearance","picture.png"),ImageFormat.Png);config["picture"]="picture.png";SaveConfig();Publish();
    }
    void ResetPage(){pageStamp=null;pageVersion++;sentPageVersion=-1;stale=true;partialLayer=false;lastProbe=lastFrame=dirtySince=lastChange=0;pendingChanges.Clear();}
    void InvalidatePage(IReadOnlyList<Rectangle>? changes=null)
    {
        long now=Environment.TickCount64;if(!stale||dirtySince==0)dirtySince=now;lastChange=now;
        pageVersion++;stale=true;
        if(changes is null)
        {
            var all=new Rectangle(Point.Empty,target?.Size??Size.Empty);
            overlay.MaskChanged(new[]{all});partialLayer=false;overlay.Hide();if(busy)pendingChanges.Add(all);
        }
        else
        {
            overlay.MaskChanged(changes);partialLayer=overlay.HasVisibleText;
            // Bound memory while a slow inference overlaps a long scrolling gesture.
            if(busy){pendingChanges.UnionWith(changes);if(pendingChanges.Count>512){pendingChanges.Clear();pendingChanges.Add(new Rectangle(Point.Empty,target?.Size??Size.Empty));}}
        }
        toolbar.MarkStale();
    }
    static bool RefreshDue(long now,long firstChange,long latestChange,long previousFrame,int interval,int quiet)
        =>now-previousFrame>=interval&&(now-latestChange>=quiet||now-firstChange>=Math.Max(1000,interval));
    async Task RefreshChangedPage()
    {
        if(!filter||!stale||busy||!ready||original||ControlsOpen||target is null)return;
        int quiet=EffectiveProfile()=="Eco"?320:160;
        if(RefreshDue(Environment.TickCount64,dirtySince,lastChange,lastFrame,RefreshInterval(),quiet))await CaptureTarget();
    }
    void ObservePage(bool force=false)
    {
        if(Program.CheckUi||target is not Rectangle bounds||ControlsOpen)return;
        long now=Environment.TickCount64;int interval=EffectiveProfile()=="Eco"?400:100;
        if(!force&&now-lastProbe<interval)return;lastProbe=now;
        try
        {
            // Capture exclusion is mandatory for probing a visible replacement layer.
            if(overlay.Visible&&!overlay.CaptureExcluded||toolbar.Visible&&!toolbar.CaptureExcluded)return;
            byte[] stamp=PageProbe.Capture(bounds);
            if(pageStamp is null)InvalidatePage();
            else {var changes=PageProbe.Changes(pageStamp,stamp,bounds.Size);if(changes.Count>0)InvalidatePage(changes);}
            pageStamp=stamp;
            if(filter)filterTimer.Interval=RefreshInterval();
        }
        catch(System.ComponentModel.Win32Exception){InvalidatePage();}
    }
}

internal static class PageProbe
{
    internal delegate void WinEventCallback(IntPtr hook,uint ev,IntPtr window,int obj,int child,uint thread,uint time);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEventCallback callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll",SetLastError=true)] static extern bool StretchBlt(IntPtr dest,int x,int y,int width,int height,IntPtr source,int sx,int sy,int sw,int sh,uint operation);
    internal static byte[] Capture(Rectangle bounds)
    {
        using var small=new Bitmap(320,200,PixelFormat.Format24bppRgb);using var graphics=Graphics.FromImage(small);
        var source=GetDC(IntPtr.Zero);var dest=graphics.GetHdc();
        try{if(!StretchBlt(dest,0,0,320,200,source,bounds.X,bounds.Y,bounds.Width,bounds.Height,0x00CC0020))throw new System.ComponentModel.Win32Exception();}
        finally{graphics.ReleaseHdc(dest);ReleaseDC(IntPtr.Zero,source);}
        var locked=small.LockBits(new Rectangle(0,0,320,200),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
        try{byte[] bytes=new byte[locked.Stride*200];Marshal.Copy(locked.Scan0,bytes,0,bytes.Length);return bytes;}
        finally{small.UnlockBits(locked);}
    }
    internal static bool Changed(byte[] before,byte[] after)
        =>Changes(before,after,new Size(320,200)).Count>0;
    internal static List<Rectangle> Changes(byte[] before,byte[] after,Size target)
    {
        if(before.Length!=after.Length||before.Length!=192000)return new(){new Rectangle(Point.Empty,target)};
        int changed=0;var cells=new bool[160];
        // A 16x10 grid localizes changed pixels; expand one thumbnail pixel for sampling uncertainty.
        for(int i=0;i<before.Length;i+=3)
            if(Math.Abs(before[i]-after[i])+Math.Abs(before[i+1]-after[i+1])+Math.Abs(before[i+2]-after[i+2])>48)
            {changed++;int pixel=i/3;cells[pixel/320/20*16+pixel%320/20]=true;}
        if(changed<=24)return new();
        return Enumerable.Range(0,160).Where(i=>cells[i]).Select(i=>Rectangle.Intersect(new Rectangle(Point.Empty,target),Rectangle.FromLTRB(
            (int)Math.Floor((i%16*20-1)*target.Width/320.0),(int)Math.Floor((i/16*20-1)*target.Height/200.0),
            (int)Math.Ceiling((i%16*20+21)*target.Width/320.0),(int)Math.Ceiling((i/16*20+21)*target.Height/200.0)))).ToList();
    }
}
