// Purpose: render the production toolbar's focused Dismiss button without loading a backend.
// Dependencies: existing .NET 8 Desktop and ScreenTranslator.dll. Outputs: fresh PNG/JSON evidence.
// Command: private-dotnet ToolbarFocus.dll SCREEN_TRANSLATOR_DLL NEW_OUTPUT_DIRECTORY
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;

internal static class ToolbarFocusCheck
{
    [STAThread]
    static void Main(string[] args)
    {
        Directory.CreateDirectory(args[1]);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        var type=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("LocalScreenTranslator.TranslationToolbar",true)!;
        var rows=new List<object>();
        foreach(bool dark in new[]{false,true})
        {
            int dismissed=0;
            using var toolbar=(Form)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{(Func<Task>)(()=>Task.CompletedTask),(Func<Task>)(()=>Task.CompletedTask),(Action)(()=>dismissed++),(Func<Task>)(()=>Task.CompletedTask)},null)!;
            type.GetMethod("Appearance",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(toolbar,new object[]{Color.FromArgb(223,41,38),dark});
            type.GetMethod("Loading",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(toolbar,new object[]{new Rectangle(0,0,1920,1080)});
            var dismiss=(Button)type.GetField("dismiss",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(toolbar)!;
            toolbar.Show();toolbar.Activate();dismiss.Focus();Application.DoEvents();
            bool rectangularCue=(bool)dismiss.GetType().GetProperty("ShowFocusCues",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dismiss)!;
            if(rectangularCue||!dismiss.Focused||!dismiss.TabStop)throw new InvalidOperationException("The focused button retained the rectangular cue or lost keyboard navigation");
            using(var image=new Bitmap(toolbar.Width,toolbar.Height)){toolbar.DrawToBitmap(image,toolbar.ClientRectangle);image.Save(Path.Combine(args[1],dark?"focused-dark.png":"focused-light.png"),ImageFormat.Png);}
            dismiss.PerformClick();if(dismissed!=1)throw new InvalidOperationException("Dismiss button activation changed");
            rows.Add(new{dark,focused=dismiss.Focused,rectangularCue,tabStop=dismiss.TabStop,dismissed});toolbar.Hide();
        }
        File.WriteAllText(Path.Combine(args[1],"summary.json"),JsonSerializer.Serialize(new{passed=true,backendStarted=false,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS: light/dark focused Dismiss has no rectangular outline and retains activation; no model/backend started");
    }
}
