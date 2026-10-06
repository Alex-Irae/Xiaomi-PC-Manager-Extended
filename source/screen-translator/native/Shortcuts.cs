// Purpose: user-defined global shortcuts and native key capture, including physical Copilot keys.
// Dependencies: Windows user32, .NET Desktop 8. Outputs: a shortcut string, never a key history.
// Command: ./dev.ps1 [SDK options], or ScreenTranslator.exe.
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalScreenTranslator;

internal readonly record struct ShortcutBinding(uint Key,uint Modifiers)
{
    internal bool IsCopilot=>Modifiers==12&&Key==(uint)Keys.F23||Modifiers==8&&Key==(uint)Keys.C;
    internal static bool Modifier(uint key)=>key is 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or >=0xa0 and <=0xa5;
    internal static ShortcutBinding Parse(string value)
    {
        value=value.Trim();if(value.Equals("Copilot",StringComparison.OrdinalIgnoreCase))return new((uint)Keys.F23,12);
        if(value.Length>80)throw new ArgumentException("Shortcut is too long");
        uint modifiers=0;Keys key=Keys.None;
        foreach(string part in value.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            uint modifier=part.ToLowerInvariant() switch {"alt"=>1,"ctrl" or "control"=>2,"shift"=>4,"win" or "windows"=>8,_=>0};
            if(modifier>0){if((modifiers&modifier)!=0)throw new ArgumentException("Repeated shortcut modifier");modifiers|=modifier;continue;}
            string name=part.Length==1&&char.IsAsciiDigit(part[0])?"D"+part:part;
            if(key!=Keys.None||char.IsAsciiDigit(name[0])||!Enum.TryParse(name,true,out key)||!Enum.IsDefined(key))throw new ArgumentException("Use one key, optionally with Ctrl, Alt, Shift or Win");
        }
        uint code=(uint)key;
        if(code<9||code>254||Modifier(code)||code==0x1b&&modifiers==0)throw new ArgumentException("Choose a key other than a modifier or Escape alone");
        if(modifiers==8&&code==(uint)Keys.L||modifiers==3&&code==(uint)Keys.Delete)throw new ArgumentException("That security shortcut belongs to Windows");
        return new(code,modifiers);
    }
    public override string ToString()
    {
        if(Key==(uint)Keys.F23&&Modifiers==12)return "Copilot";
        string name=((Keys)Key).ToString();if(Key>=48&&Key<=57)name=((char)Key).ToString();
        return string.Concat(new[]{(Modifiers&2)>0?"Ctrl+":"",(Modifiers&1)>0?"Alt+":"",(Modifiers&4)>0?"Shift+":"",(Modifiers&8)>0?"Win+":""})+name;
    }
}

internal sealed class KeyboardHook : IDisposable
{
    delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct KeyEvent {internal uint key,scan,flags,time;internal UIntPtr extra;}
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput {internal ushort key,scan;internal uint flags,time;internal UIntPtr extra;}
    [StructLayout(LayoutKind.Explicit,Size=32)] struct InputData {[FieldOffset(0)] internal KeyboardInput keyboard;}
    [StructLayout(LayoutKind.Sequential)] struct Input {internal uint type;internal InputData data;}
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int kind,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? module);
    readonly HookProc callback;
    readonly Func<uint,uint,bool> accepts;
    readonly Action<uint,uint> pressed;
    readonly HashSet<uint> held=new(),suppressed=new();
    IntPtr handle;
    internal KeyboardHook(Func<uint,uint,bool> accepts,Action<uint,uint> pressed)
    {
        this.accepts=accepts;this.pressed=pressed;callback=Dispatch;
        // Seed modifiers outside the callback: Windows updates asynchronous state after the hook.
        foreach(uint key in new uint[]{0x5b,0x5c,0xa0,0xa1,0xa2,0xa3,0xa4,0xa5})if(GetAsyncKeyState((int)key)<0)held.Add(key);
        handle=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
        if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows could not attach the shortcut listener");
    }
    internal uint Modifiers=>
        (held.Contains(0x12)||held.Contains(0xa4)||held.Contains(0xa5)?1u:0)|(held.Contains(0x11)||held.Contains(0xa2)||held.Contains(0xa3)?2u:0)|
        (held.Contains(0x10)||held.Contains(0xa0)||held.Contains(0xa1)?4u:0)|(held.Contains(0x5b)||held.Contains(0x5c)?8u:0);
    internal bool Process(uint key,bool down,bool maskMenu=true)
    {
        bool repeat=held.Contains(key);if(down)held.Add(key);else held.Remove(key);
        if(!down)return suppressed.Remove(key);
        if(ShortcutBinding.Modifier(key))return false;
        if(suppressed.Contains(key))return true;
        uint modifiers=Modifiers;
        if(!accepts(key,modifiers))return false;
        suppressed.Add(key);
        if(!repeat)
        {
            // An unused synthetic key prevents a suppressed Win chord opening Start on release.
            // Only this marker is ignored, so accessibility/remapping software can still invoke shortcuts.
            if(maskMenu&&(modifiers&8)>0)MaskWindowsMenu();
            pressed(key,modifiers);
        }
        return true;
    }
    static void MaskWindowsMenu()
    {
        var inputs=new[]{new Input{type=1,data=new InputData{keyboard=new KeyboardInput{key=0xe8,extra=new UIntPtr(0x535452)}}},
            new Input{type=1,data=new InputData{keyboard=new KeyboardInput{key=0xe8,flags=2,extra=new UIntPtr(0x535452)}}}};
        SendInput(2,inputs,Marshal.SizeOf<Input>());
    }
    IntPtr Dispatch(int code,IntPtr message,IntPtr data)
    {
        if(code>=0)
        {
            var value=Marshal.PtrToStructure<KeyEvent>(data);int kind=message.ToInt32();
            if(!(value.key==0xe8&&value.extra==new UIntPtr(0x535452))&&(kind is 0x100 or 0x104 or 0x101 or 0x105))
            {
                try{if(Process(value.key,kind is 0x100 or 0x104))return new IntPtr(1);}
                catch { /* Never break the input chain because an app callback fails. */ }
            }
        }
        return CallNextHookEx(handle,code,message,data);
    }
    public void Dispose(){if(handle!=IntPtr.Zero){UnhookWindowsHookEx(handle);handle=IntPtr.Zero;}GC.KeepAlive(callback);}
}

internal sealed class GlobalShortcut : IDisposable
{
    static int nextId=100;
    readonly IntPtr owner;
    readonly KeyboardHook? hook;
    internal readonly int Id;
    internal readonly ShortcutBinding Binding;
    internal GlobalShortcut(IntPtr owner,ShortcutBinding binding,Action trigger)
    {
        this.owner=owner;Binding=binding;
        if(binding.IsCopilot)
            hook=new KeyboardHook((key,modifiers)=>key==binding.Key&&modifiers==binding.Modifiers,(_,_)=>trigger());
        else
        {
            Id=++nextId;
            if(!Native.RegisterHotKey(owner,Id,binding.Modifiers|0x4000,binding.Key))throw new ArgumentException("That shortcut is reserved by Windows or already in use; the previous shortcut stays active");
        }
    }
    public void Dispose(){hook?.Dispose();if(Id!=0)Native.UnregisterHotKey(owner,Id);}
}

internal sealed class ShortcutDialog : Form
{
    readonly Label hint=new(){AutoSize=false,Text="Press a key or combination. Modifiers are optional."};
    readonly TextBox value=new(){ReadOnly=true};
    readonly Button use=new(){Text="Use shortcut",DialogResult=DialogResult.OK};
    readonly KeyboardHook hook;
    bool recording=true;
    internal string Selected="";
    internal ShortcutDialog(string current)
    {
        Text="Translation shortcut";StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=MinimizeBox=false;ShowInTaskbar=false;ClientSize=new Size(430,205);Font=new Font("Segoe UI",10);
        hint.SetBounds(18,16,390,45);value.SetBounds(18,66,390,30);value.Text=current;
        var record=new Button{Text="Record again"};record.SetBounds(18,112,122,30);record.Click+=(_,_)=>{recording=true;hint.Text="Press your key or combination now.";};
        var copilot=new Button{Text="Copilot key"};copilot.SetBounds(150,112,122,30);copilot.Click+=(_,_)=>SelectBinding("Copilot");
        use.SetBounds(180,158,112,32);use.Enabled=false;
        var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel};cancel.SetBounds(300,158,108,32);CancelButton=cancel;
        Controls.AddRange(new Control[]{hint,value,record,copilot,use,cancel});
        _=Handle;
        hook=new KeyboardHook((key,modifiers)=>recording&&Native.GetForegroundWindow()==Handle,
            (key,modifiers)=>BeginInvoke(()=>
            {
                if(key==(uint)Keys.Escape&&modifiers==0){DialogResult=DialogResult.Cancel;return;}
                try{SelectBinding(ShortcutBinding.Parse(new ShortcutBinding(key,modifiers).ToString()).ToString());}
                catch(ArgumentException error){hint.Text=error.Message;}
            }));
    }
    void SelectBinding(string shortcut){Selected=shortcut;value.Text=shortcut;recording=false;use.Enabled=true;hint.Text="Choose Use to activate this shortcut. Esc still dismisses translation.";}
    protected override void Dispose(bool disposing){if(disposing)hook?.Dispose();base.Dispose(disposing);}
}
