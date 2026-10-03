// Purpose: editable Windows hotkeys, independent of OEM keyboard software.
// Dependencies: Win32 RegisterHotKey and the resident's existing action router.
// Outputs: registrations for this process only. Command: app/PCManager.exe --tray.
using System.Runtime.InteropServices;
using System.Text.Json;
using XiaomiRevamp.Suite;

namespace XiaomiAIManager.Services;

internal sealed class GlobalShortcuts : NativeWindow, IDisposable
{
    private readonly ManagerApplication app;
    private SuiteKeyboard? keyboard;
    internal string? Error { get; private set; }
    internal GlobalShortcuts(ManagerApplication app) { this.app = app; CreateHandle(new CreateParams()); }
    internal static (uint modifiers, uint key) Parse(string chord)
    {
        var value = SuiteChord.Parse(chord);
        return (value.Modifiers | 0x4000,value.Key);
    }
    internal static List<KeyboardShortcut> Validate(JsonElement args, Preferences p)
    {
        var items = args.GetProperty("shortcuts").Deserialize<List<KeyboardShortcut>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (items.Count > 16) throw new ArgumentException("At most 16 custom shortcuts are supported.");
        for (int index=0;index<items.Count;index++)
        {
            items[index].Chord = SuiteChord.Parse(items[index].Chord).Text;
            string previous = index<p.Shortcuts.Count ? SuiteChord.Parse(p.Shortcuts[index].Chord).Text : "none";
            if (items[index].Chord != previous && items[index].Chord != "none")
                for (int other=0;other<items.Count;other++)
                    if (other!=index && SuiteChord.Parse(items[other].Chord).Text==items[index].Chord) items[other].Chord = previous;
        }
        var used = new HashSet<string>();
        foreach (var item in items)
        {
            if (item.Chord!="none"&&!used.Add(item.Chord)) throw new ArgumentException("Two shortcuts use the same keys.");
            if (!Actions.Contains(item.Action)) throw new ArgumentException("Choose a listed shortcut action.");
            if (item.Action == "app" && !p.QuickLinks.Any(l => l.Id == item.AppId)) throw new ArgumentException("Choose an existing app link for the shortcut.");
        }
        return items;
    }
    internal static readonly string[] Actions = ["none", "panel", .. XiControl.Input.KeyRouter.ManagerPages.Select(page => "page." + page), "windowssettings", "modes", "hz", "screenoff", "travel", "touchpad", "touchscreen", "monitor", "owl", "projection", "screenshot", "calc", "xiaoai", "app", "suite.file-search.open", "suite.screen-translator.toggle", "suite.screen-translator.region"];
    internal bool Configure(IEnumerable<KeyboardShortcut> items)
    {
        keyboard?.Dispose(); keyboard=null; Error=null;
        if (SuiteEnvironment.Enabled) return true; // The hub registers custom and optional bindings together.
        var values=items.ToArray();
        try
        {
            keyboard=new(values.Select((item,index)=>new KeyValuePair<string,string>(index.ToString(),item.Chord)).ToDictionary(),
                index=>app.Post(()=>app.Advanced?.RunCustomAction(values[int.Parse(index)])));
        }
        catch(Exception error){Error=error.Message;}
        return Error is null;
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
    }
    public void Dispose() { keyboard?.Dispose(); keyboard=null; DestroyHandle(); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint handle, int id);
}

