// Purpose: intercept the Windows Copilot chord while the resident is running and route it to a chosen action.
// Dependencies: Win32 low-level keyboard hook and the existing action router. Outputs: no persistent OS remap.
// Command: app/PCManager.exe --tray; configure the Copilot key on the Keyboard page.
using System.Runtime.InteropServices;

namespace XiaomiAIManager.Services;

internal sealed class CopilotKeyRouter : IDisposable
{
    private const int WhKeyboardLl = 13, VkF23 = 0x86;
    private const nint WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    private readonly ManagerApplication app;
    private readonly HookCallback callback;
    private nint hook;
    private KeyboardShortcut binding = new() { Chord = "Win+Shift+F23", Action = "none" };
    private bool pressed;
    internal string? Error { get; }
    internal int InterceptCount { get; private set; }

    internal CopilotKeyRouter(ManagerApplication app)
    {
        this.app = app;
        callback = OnKeyboard;
        hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
        if (hook == 0) Error = "Copilot key interception is unavailable (Windows error " + Marshal.GetLastWin32Error() + ").";
    }

    internal void Configure(KeyboardShortcut value) => binding = value;

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code < 0 || hook == 0 || (message != WmKeyDown && message != WmSysKeyDown && message != WmKeyUp && message != WmSysKeyUp))
            return CallNextHookEx(hook, code, message, data);
        if (Marshal.ReadInt32(data) != VkF23 || binding.Action == "system")
            return CallNextHookEx(hook, code, message, data);

        bool down = message == WmKeyDown || message == WmSysKeyDown;
        if (down)
        {
            bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            if (!win || !shift) return CallNextHookEx(hook, code, message, data);
            if (!pressed && binding.Action != "none")
            {
                var action = binding;
                app.Post(() => app.Advanced?.RunCustomAction(action));
            }
            if (!pressed) { InterceptCount++; app.Post(app.RefreshWindows); }
            pressed = true;
            return 1;
        }
        if (pressed) { pressed = false; return 1; }
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
        GC.KeepAlive(callback);
    }

    private delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint handle);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint handle, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
