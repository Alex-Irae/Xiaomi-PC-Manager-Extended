// Purpose: focused cold-action and real idle-release check without capturing the desktop.
// Dependencies: existing private Python, local models and WebView2. Outputs: isolated JSON/logs.
// Run: ScreenTranslator.exe --check-model-lifecycle --data-dir ABSOLUTE_RUN --cache-data-dir EXISTING_MODEL_CACHE_PARENT
using System.Diagnostics;
using System.Text.Json;

namespace LocalScreenTranslator;

internal sealed partial class MainWindow
{
    async Task CheckModelLifecycle()
    {
        var evidence=new Dictionary<string,object>();var loads=new List<double>();
        string progress=Path.Combine(Program.Data,"lifecycle-progress.json");
        void Step(string phase)=>File.WriteAllText(progress,JsonSerializer.Serialize(new {phase,pid=Environment.ProcessId,backendPid=backend?.Id,ready,busy,pendingAction,controlsOpen=ControlsOpen}));
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        async Task Wait(Func<bool> condition,int seconds,string label)
        {
            var clock=Stopwatch.StartNew();
            while(!condition()) {if(lastError.Length>0)throw new InvalidOperationException(lastError);if(clock.Elapsed.TotalSeconds>seconds)throw new TimeoutException(label);await Task.Delay(100);}
            Program.Log("PASS: "+label);
        }
        try
        {
            config["model_idle_seconds"]=10; // the check waits for an idle release; real use defaults to two minutes
            Hide();Require(backend is null&&!ready,"Startup loaded models without an action.");
            Require(web.CoreWebView2 is null,"Hidden startup created the controls browser.");
            evidence["hiddenStartupWithoutBrowser"]=true;Step("startup");
            await Hotkey(6);Require(pendingAction==6&&busy&&!ControlsOpen,"Cold toggle did not queue or opened settings.");
            // A second press while loading must not cancel the load; the loading bar's close button does that.
            var loading=backend;await Hotkey(6);Require(backend==loading&&busy&&pendingAction==6,"Second toggle interrupted loading.");
            evidence["coldToggleQueuedWithoutGui"]=true;evidence["secondToggleKeepsLoading"]=true;
            for(int pass=0;pass<2;pass++)
            {
                var clock=Stopwatch.StartNew();await Hotkey(2);
                Require(pendingAction==2&&busy&&!ControlsOpen,"Cold region action did not queue without controls.");Step("loading-"+pass);
                if(pass==0)
                {
                    var owned=backend;long generation=epoch;
                    FrontendFailed(new System.Runtime.InteropServices.COMException("Synthetic controls navigation abort",unchecked((int)0x80004004)));
                    Require(backend==owned&&busy&&!ready&&pendingAction==2&&epoch==generation,"Controls failure interrupted loading.");
                    evidence["controlsAbortDoesNotStopInference"]=true;
                }
                await Wait(()=>selecting&&Application.OpenForms.OfType<RegionSelector>().Any(),180,"queued region action resumes after models become ready");
                loads.Add(clock.Elapsed.TotalSeconds);Step("loaded-"+pass);
                // Cancel the real selector before it can return a rectangle or capture any pixels.
                Application.OpenForms.OfType<RegionSelector>().Single().DialogResult=DialogResult.Cancel;
                await Wait(()=>!selecting,5,"selection cancellation returns without capture");
                Require(pendingCapture is null&&target is null,"Cancelled region captured the desktop.");
                await Wait(()=>backend is null&&!ready,16,"ten-second idle timer releases the owned inference processes");
                Step("idle-"+pass);await Task.Delay(1500);
            }
            evidence["passed"]=true;evidence["queuedActionResumed"]=true;evidence["idleReleased"]=true;
            evidence["desktopCaptured"]=false;evidence["loadSeconds"]=loads;evidence["idleSeconds"]=10;
        }
        catch(Exception error){evidence["passed"]=false;evidence["error"]=error.ToString();Program.Log(error.ToString());Environment.ExitCode=1;}
        finally
        {
            File.WriteAllText(Path.Combine(Program.Data,"lifecycle-summary.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
            Step("complete");Quit();
        }
    }
}
