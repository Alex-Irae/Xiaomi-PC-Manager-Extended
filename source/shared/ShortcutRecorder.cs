// Purpose: record physical shortcut presses without typing names or invoking existing bindings.
// Dependencies: Windows user32 and .NET Desktop 8. Outputs: one confirmed chord; no key history.
// Build: tools/build_suite.py. Open through a component's shortcut settings.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaomiRevamp.Suite;

internal sealed class SuiteShortcutDialog : Form
{
    readonly Label hint = new() { Text = "Press your combination. Press Ctrl twice for Double Ctrl. Esc cancels." };
    readonly TextBox value = new() { ReadOnly = true };
    readonly Button use = new() { Text = "Use shortcut", DialogResult = DialogResult.OK, Enabled = false };
    readonly HashSet<uint> held = new(), suppressed = new();
    readonly HookCallback callback;
    IntPtr hook;
    bool recording = true, ctrlChord;
    long ctrlDown, lastCtrl;
    internal string Selected { get; private set; } = "";
    internal SuiteShortcutDialog(string current)
    {
        Text = "Press a shortcut"; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(470,210); Font = new("Segoe UI",10);
        hint.SetBounds(18,16,434,46); value.SetBounds(18,72,434,30); value.Text = current;
        var again = new Button { Text = "Record again" }; again.SetBounds(18,120,125,32);
        again.Click += (_,_) => { recording = true; Selected = ""; use.Enabled = false; hint.Text = "Press your combination now. Esc cancels."; lastCtrl = 0; };
        use.SetBounds(214,160,116,32);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; cancel.SetBounds(342,160,110,32);
        CancelButton = cancel; Controls.AddRange(new Control[] { hint,value,again,use,cancel });
        _ = Handle; callback = OnKey;
        hook = SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows refused the shortcut recorder.");
    }
    static bool Modifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or >=0xa0 and <=0xa5;
    uint Modifiers => (held.Any(k=>k is 0x11 or 0xa2 or 0xa3)?2u:0) | (held.Any(k=>k is 0x12 or 0xa4 or 0xa5)?1u:0) |
        (held.Any(k=>k is 0x10 or 0xa0 or 0xa1)?4u:0) | (held.Any(k=>k is 0x5b or 0x5c)?8u:0);
    internal static string Chord(uint key,uint modifiers) => SuiteChord.Parse(string.Concat(
        (modifiers&2)!=0?"Ctrl+":"",(modifiers&1)!=0?"Alt+":"",(modifiers&4)!=0?"Shift+":"",(modifiers&8)!=0?"Win+":"",((Keys)key).ToString())).Text;
    void Choose(string chord)
    {
        Selected = chord; recording = false;
        BeginInvoke(()=> { value.Text = chord == "double_ctrl" ? "Double Ctrl" : chord; hint.Text = "Release the keys, then choose Use shortcut."; use.Enabled = held.Count == 0; });
    }
    IntPtr OnKey(int code,IntPtr message,IntPtr data)
    {
        if (code < 0 || message.ToInt32() is not (0x100 or 0x104 or 0x101 or 0x105)) return CallNextHookEx(hook,code,message,data);
        uint key = (uint)Marshal.ReadInt32(data); bool down = message.ToInt32() is 0x100 or 0x104;
        return ProcessKey(key,down,GetForegroundWindow()==Handle) ? new(1) : CallNextHookEx(hook,code,message,data);
    }
    // Consume the recorded press and every matching release, even if focus changes mid-chord.
    internal bool ProcessKey(uint key,bool down,bool foreground)
    {
        if (down && suppressed.Contains(key)) return true;
        if (!down && suppressed.Remove(key))
        {
            held.Remove(key);
            if (recording && (key is 0x11 or 0xa2 or 0xa3) && !ctrlChord && Environment.TickCount64-ctrlDown < 600)
            {
                long now = Environment.TickCount64;
                if (lastCtrl > 0 && now-lastCtrl <= 450) Choose("double_ctrl"); else lastCtrl = now;
            }
            if (!recording && held.Count == 0 && Selected.Length > 0) BeginInvoke(()=>use.Enabled = true);
            return true;
        }
        if (!recording || !foreground) return false;
        if (down)
        {
            suppressed.Add(key); bool fresh = held.Add(key);
            if (fresh)
            {
                if (key is 0x11 or 0xa2 or 0xa3) { ctrlDown = Environment.TickCount64; ctrlChord = held.Count > 1; }
                else if (!Modifier(key))
                {
                    ctrlChord = true; lastCtrl = 0;
                    if (key == 0x1b && Modifiers == 0) BeginInvoke(()=>DialogResult = DialogResult.Cancel);
                    else try { Choose(Chord(key,Modifiers)); }
                    catch (ArgumentException error) { BeginInvoke(()=>hint.Text = error.Message); }
                }
                else { ctrlChord = true; lastCtrl = 0; }
            }
        }
        // Consume modifiers too, avoiding Start/menu activation on their release.
        return true;
    }
    protected override void Dispose(bool disposing)
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        base.Dispose(disposing); GC.KeepAlive(callback);
    }
    delegate IntPtr HookCallback(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int kind,HookCallback callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
