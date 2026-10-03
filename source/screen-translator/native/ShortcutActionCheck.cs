// Purpose: test actual shortcut messages/IPC, immediate toolbox, model load and English preservation.
// Dependencies: existing native app, Python/models and an isolated portable shortcut profile.
// Outputs: generated fixtures, toolbox previews and shortcut-action-check.json; never desktop pixels.
// Run: checks/run_translation_shortcuts.ps1 -Native BUILD_FOLDER -Source APP_SOURCE -App INSTALLED_APP -CacheProfile CACHE_PROFILE -RunDirectory NEW_RUN.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using XiaomiRevamp.Suite;

namespace LocalScreenTranslator;

internal sealed partial class MainWindow
{
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr handle,int message,IntPtr first,IntPtr second);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);

    async Task CheckShortcutActions()
    {
        var checks=new Dictionary<string,bool>();var timings=new Dictionary<string,double>();
        void Require(bool value,string label){checks[label]=value;if(!value)throw new InvalidOperationException(label);Program.Log("PASS: "+label);}
        async Task Wait(Func<bool> condition,string label,int seconds=20)
        {
            var watch=Stopwatch.StartNew();
            while(!condition()){if(lastError.Length>0)throw new InvalidOperationException(lastError);if(watch.Elapsed.TotalSeconds>seconds)throw new TimeoutException(label);await Task.Delay(20);}
        }
        void Key(string action)
        {
            // Deliver WM_HOTKEY to the real registered shortcut window, using its current IDs.
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var keyboard=(SuiteKeyboard?)typeof(SuiteClient).GetField("keyboard",flags)!.GetValue(suite);
            if(keyboard is null)throw new InvalidOperationException("Shortcut window did not register");
            var ids=(Dictionary<int,string>)typeof(SuiteKeyboard).GetField("hotkeys",flags)!.GetValue(keyboard)!;
            int id=ids.Single(item=>item.Value==action).Key;
            if(!PostMessage(keyboard.Handle,0x312,new IntPtr(id),IntPtr.Zero))throw new InvalidOperationException("WM_HOTKEY delivery failed");
        }
        void UpperRight(string label)
        {
            var area=SelectedScreen().WorkingArea;
            Require(toolbar.Visible&&IsWindowVisible(toolbar.Handle)&&toolbar.Left==area.Right-toolbar.Width-16&&toolbar.Top==area.Top+16,label);
        }
        void PreserveEnglish(string name)
        {
            using var input=new Bitmap(Path.Combine(Program.Fixture,"input.png"));
            string path=Path.Combine(Program.Data,name+"-translated.png");overlay.SaveComposite(input,path);
            using var translated=new Bitmap(path);bool equal=true;
            for(int y=75;y<Math.Min(900,input.Height)&&equal;y++)for(int x=0;x<input.Width/2;x++)
                if(input.GetPixel(x,y)!=translated.GetPixel(x,y)){equal=false;break;}
            Require(equal,name+"EnglishPixelsUnchanged");Require(overlay.Blocks>=2,name+"ChineseWasTranslated");
        }
        string? failure=null;
        try
        {
            Require(SuiteEnvironment.Enabled&&SuiteEnvironment.Portable&&SuiteEnvironment.Root.StartsWith(Program.Data,StringComparison.OrdinalIgnoreCase),"isolatedShortcutProfile");
            Require(suite is not null&&suite.Error is null,"standaloneKeysRegistered");
            Hide();Require(!ready&&backend is null&&web.CoreWebView2 is null,"hiddenStartupWithoutModelsOrBrowser");
            Directory.CreateDirectory(Program.Fixture);
            using(var image=new Bitmap(SelectedScreen().Bounds.Width,SelectedScreen().Bounds.Height))
            using(var graphics=Graphics.FromImage(image))
            using(var font=new Font("Segoe UI",28,GraphicsUnit.Pixel))
            using(var chinese=new Font("Microsoft YaHei",32,GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                string[] labels={"PC Manager","Screen Translator","AI Center","Microsoft Edge","Google Chrome","Recycle Bin","Documents","Downloads","Visual Studio Code","Steam","Firefox","Discord"};
                for(int i=0;i<labels.Length;i++)graphics.DrawString(labels[i],font,Brushes.Black,60,90+i*60);
                string[] texts={"设置","打开文件","此功能完全在本机运行。"};
                for(int i=0;i<texts.Length;i++)graphics.DrawString(texts[i],chinese,Brushes.Black,image.Width/2+80,230+i*110);
                image.Save(Path.Combine(Program.Fixture,"input.png"));
            }
            config["toolbar_focus_only"]=false;config["cache"]=false;config["interval_ms"]=2000;
            var clock=Stopwatch.StartNew();Key("screen-translator.screen");
            await Wait(()=>busy&&pendingAction==1,"cold screen action",5);
            timings["coldToolboxSeconds"]=clock.Elapsed.TotalSeconds;Require(clock.Elapsed.TotalSeconds<1,"coldToolboxWithinOneSecond");
            UpperRight("coldToolboxVisibleUpperRight");Require(!ControlsOpen&&backend is not null&&!ready,"coldScreenLoadsModelsWithoutSettings");
            toolbar.SavePreview(Path.Combine(Program.Data,"cold-toolbox.png"));
            await Wait(()=>lastTiming is not null&&!busy&&ready,"cold action completes",180);timings["coldLoadAndTranslationSeconds"]=clock.Elapsed.TotalSeconds;
            Require(!ControlsOpen,"coldScreenKeepsSettingsHidden");PreserveEnglish("cold");
            lastTiming=null;clock.Restart();SuiteStore.Send("screen-translator.screen");
            await Wait(()=>busy&&target is not null,"warm IPC screen action",5);
            timings["warmToolboxSeconds"]=clock.Elapsed.TotalSeconds;Require(clock.Elapsed.TotalSeconds<1,"warmToolboxWithinOneSecond");
            UpperRight("warmToolboxVisibleBeforeOcrCompletes");Require(!ControlsOpen,"warmScreenKeepsSettingsHidden");
            toolbar.SavePreview(Path.Combine(Program.Data,"warm-toolbox.png"));
            await Wait(()=>lastTiming is not null&&!busy,"warm action completes");PreserveEnglish("warm");
            Key("screen-translator.original");await Wait(()=>original,"original key");Require(!overlay.Visible&&toolbar.Visible,"originalHidesOnlyReplacement");
            Key("screen-translator.original");await Wait(()=>!original&&!busy,"translated key");Require(overlay.Visible,"originalShortcutRestoresTranslation");
            Key("screen-translator.filter");await Wait(()=>filter&&filterTimer.Enabled,"filter enabled");
            Key("screen-translator.filter");await Wait(()=>!filter&&!filterTimer.Enabled&&!busy,"filter disabled");checks["filterKeyTogglesOnAndOff"]=true;
            Dismiss();lastTiming=null;Key("screen-translator.toggle");await Wait(()=>filter&&lastTiming is not null&&!busy,"toggle starts translation");
            Key("screen-translator.toggle");await Wait(()=>target is null&&!toolbar.Visible,"toggle dismisses translation");checks["toggleKeyStartsAndDismisses"]=true;
            Key("screen-translator.region");await Wait(()=>selecting&&Application.OpenForms.OfType<RegionSelector>().Any(),"region key opens selector");
            Application.OpenForms.OfType<RegionSelector>().Single().DialogResult=DialogResult.Cancel;
            await Wait(()=>!selecting,"cancel region");Require(target is null&&pendingCapture is null,"regionCancellationCapturesNothing");
            await Wait(()=>backend is null&&!ready,"idle model release",16);checks["idleInferenceReleased"]=true;
        }
        catch(Exception error){failure=error.ToString();Program.Log(failure);Environment.ExitCode=1;}
        finally
        {
            File.WriteAllText(Path.Combine(Program.Data,"shortcut-action-check.json"),JsonSerializer.Serialize(new {passed=failure is null,checks,timings,error=failure,desktopCaptured=false,physicalKeyboardDelivered=false,windowsHotkeyMessagesDelivered=true,realSharedQueueUsed=true},new JsonSerializerOptions{WriteIndented=true}));
            Quit();
        }
    }
}
