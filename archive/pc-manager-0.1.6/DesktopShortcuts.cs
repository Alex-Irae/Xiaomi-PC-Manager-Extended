using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XiaomiAIManager;

internal static class DesktopShortcuts
{
    internal static bool IsKnown(string name) => name is "Calculator" or "Notepad" or "Screenshot" or "Clipboard" or "Screen";
    internal static void Open(string name)
    {
        if (name is "Calculator" or "Notepad")
        {
            string executable = name == "Calculator" ? "calc.exe" : "notepad.exe";
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), executable)) { UseShellExecute = true });
            return;
        }
        ushort[] keys = name switch
        {
            "Screenshot" => [0x5B, 0x10, 0x53], // Win+Shift+S.
            "Clipboard" => [0x5B, 0x56], // Win+V.
            "Screen" => [0x5B, 0x50], // Win+P.
            _ => throw new ArgumentException("Unknown desktop shortcut.")
        };
        // A complete, ordered chord: press each key, then release in reverse order.
        var inputs = keys.Select(key => Key(key, false)).Concat(keys.Reverse().Select(key => Key(key, true))).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows did not accept the desktop shortcut.");
    }
    private static Input Key(ushort key, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse; // Required: INPUT union is 32 bytes on x64.
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
