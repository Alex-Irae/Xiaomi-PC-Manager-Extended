// Purpose: editable Windows hotkeys, independent of OEM keyboard software.
// Dependencies: Win32 RegisterHotKey and the resident's existing action router.
// Outputs: registrations for this process only. Command: app/XiaomiAIManager.exe --tray.
using System.Runtime.InteropServices;
using System.Text.Json;

namespace XiaomiAIManager.Services;

internal sealed class GlobalShortcuts : NativeWindow, IDisposable
{
    private readonly ManagerApplication app;
    private readonly Dictionary<int, KeyboardShortcut> active = [];
    internal string? Error { get; private set; }
    internal GlobalShortcuts(ManagerApplication app) { this.app = app; CreateHandle(new CreateParams()); }
    internal static (uint modifiers, uint key) Parse(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("Use Ctrl, Alt, Shift or Win plus a key. Fn is a firmware key, not a Windows modifier.");
        uint modifiers = 0;
        foreach (string part in parts[..^1])
        {
            uint flag = part.ToLowerInvariant() switch { "ctrl" => 2, "alt" => 1, "shift" => 4, "win" => 8, _ => throw new ArgumentException("Unknown shortcut modifier.") };
            if ((modifiers & flag) != 0) throw new ArgumentException("A shortcut repeats a modifier.");
            modifiers |= flag;
        }
        string name = parts[^1];
        if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
        if (!Enum.TryParse<Keys>(name, true, out var key) || (int)key <= 0 || (int)key > 254 || key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
            throw new ArgumentException("Choose a Windows key such as K, S, F10 or Space.");
        return (modifiers | 0x4000, (uint)key); // MOD_NOREPEAT avoids repeated mode switches while held.
    }
    internal static List<KeyboardShortcut> Validate(JsonElement args, Preferences p)
    {
        var items = args.GetProperty("shortcuts").Deserialize<List<KeyboardShortcut>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (items.Count > 16) throw new ArgumentException("At most 16 custom shortcuts are supported.");
        var used = new HashSet<(uint, uint)>();
        foreach (var item in items)
        {
            if (!used.Add(Parse(item.Chord))) throw new ArgumentException("Two shortcuts use the same keys.");
            if (!Actions.Contains(item.Action)) throw new ArgumentException("Choose a listed shortcut action.");
            if (item.Action == "app" && !p.QuickLinks.Any(l => l.Id == item.AppId)) throw new ArgumentException("Choose an existing app link for the shortcut.");
        }
        return items;
    }
    internal static readonly string[] Actions = ["none", "panel", .. XiControl.Input.KeyRouter.ManagerPages.Select(page => "page." + page), "windowssettings", "modes", "hz", "screenoff", "travel", "touchpad", "touchscreen", "monitor", "owl", "projection", "screenshot", "calc", "xiaoai", "app"];
    internal bool Configure(IEnumerable<KeyboardShortcut> items)
    {
        foreach (int id in active.Keys) UnregisterHotKey(Handle, id);
        active.Clear(); Error = null;
        int index = 1;
        foreach (var item in items)
        {
            try
            {
                var (modifiers, key) = Parse(item.Chord);
                if (!RegisterHotKey(Handle, index, modifiers, key)) throw new InvalidOperationException($"{item.Chord} is reserved or already used by another app.");
                active[index++] = item;
            }
            catch (Exception ex) { Error = ex.Message; break; }
        }
        return Error is null;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && active.TryGetValue((int)m.WParam, out var shortcut)) app.Post(() => app.Advanced?.RunCustomAction(shortcut));
        base.WndProc(ref m);
    }
    public void Dispose() { foreach (int id in active.Keys) UnregisterHotKey(Handle, id); active.Clear(); DestroyHandle(); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint handle, int id);
}
