// Purpose: synchronize suite bindings and transfer shortcut ownership between the hub and standalone apps.
// Dependencies: Windows .NET 8 Desktop only. Outputs: isolated suite settings, leases and command queues.
// Build: python tools/build_suite.py from the suite directory. Run: install/PC Manager/PCManager.exe --manager.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Security.Principal;

namespace XiaomiRevamp.Suite;

internal static class SuiteEnvironment
{
    internal static string Root { get; } = FindRoot();
    internal static bool Enabled => Root.Length > 0;
    internal static bool Portable { get; } = FindPortable();
    internal static string Identity => Enabled ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..12] : "";
    internal static string DataRoot { get; } = FindData();
    internal static string Data(string component)
    {
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "suite-install.json")));
        string directory = marker.RootElement.TryGetProperty("componentData", out var paths) && paths.TryGetProperty(component, out var path)
            ? ExpandData(path.GetString()!) : Path.Combine(DataRoot, component);
        if (marker.RootElement.TryGetProperty("dataRoot", out _))
        {
            Directory.CreateDirectory(directory);
            string ownership = Path.Combine(directory, ".revamp-data.json");
            if (!File.Exists(ownership))
            {
                try { using var stream = new FileStream(ownership, FileMode.CreateNew); JsonSerializer.Serialize(stream, new { schema = 1, installRoot = Root, component }); }
                catch (IOException) when (File.Exists(ownership)) { }
            }
        }
        return directory;
    }
    static string ExpandData(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)
        .Replace("{sid}", WindowsIdentity.GetCurrent().User!.Value, StringComparison.Ordinal));
    internal static string Executable(string component) => Path.Combine(Root, component switch
    {
        "pc-manager" => "PC Manager", "file-search" => "AI Center", "screen-translator" => "Screen Translator",
        _ => throw new ArgumentException("Unknown suite component.")
    }, component switch { "pc-manager" => "PCManager.exe", "file-search" => "AI Center.exe", _ => "ScreenTranslator.exe" });
    internal static bool Installed(string component)
    {
        if (!Enabled || !File.Exists(Executable(component))) return false;
        string metadata = Path.Combine(Path.GetDirectoryName(Executable(component))!, "suite-component.json");
        try
        {
            if (!File.Exists(metadata)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            return document.RootElement.GetProperty("schema").GetInt32() == 1 && document.RootElement.GetProperty("component").GetString() == component;
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException) { return false; }
    }
    internal static bool LegacyRunning(string component)
    {
        string name = component == "file-search" ? "Local\\XiaomiSemanticSearchNative" : component == "screen-translator" ? "Local\\ScreenTranslator" : throw new ArgumentException("Unknown original app.");
        try { if (Mutex.TryOpenExisting(name, out var instance)) { instance.Dispose(); return true; } return false; }
        catch (UnauthorizedAccessException) { return true; }
    }
    static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "suite-install.json"))) return directory.FullName;
        return "";
    }
    static string FindData()
    {
        if (!Enabled) return "";
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "suite-install.json")));
        string profile = marker.RootElement.GetProperty("profileId").GetString() ?? "main";
        if (profile.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) throw new InvalidDataException("Invalid suite profile identifier.");
        if (marker.RootElement.TryGetProperty("dataRoot", out var path)) return ExpandData(path.GetString()!);
        return Portable ? Path.Combine(Root, "_data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaomiRevampSuite", profile);
    }
    static bool FindPortable()
    {
        if (!Enabled) return false;
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "suite-install.json")));
        return marker.RootElement.TryGetProperty("portable", out var value) && value.GetBoolean();
    }
}

internal sealed class SuiteDocument
{
    public int Schema { get; set; } = 1;
    public long Revision { get; set; }
    public Dictionary<string, string> Bindings { get; set; } = new();
    public Dictionary<string, string> Reservations { get; set; } = new();
    public Dictionary<string, long> PausedUntil { get; set; } = new();
    public Dictionary<string, List<SuiteCommand>> Commands { get; set; } = new();
    public string Theme { get; set; } = "system";
    public string Accent { get; set; } = "#3482ff";
    public bool SharedAppearance { get; set; }
}
internal sealed class SuiteCommand
{
    public long Number { get; set; }
    public string Action { get; set; } = "";
    public long Created { get; set; }
}
internal sealed record SuiteStatus(string Component, int Pid, string State, string Owner, string? Error, long Updated, string[] Owned, object? Keyboard = null);
internal sealed record SuiteLease(int Pid, long Started, long Updated);

internal static class SuiteStore
{
    internal static readonly Dictionary<string, string> Defaults = new()
    {
        ["file-search.open"] = "double_ctrl", ["screen-translator.toggle"] = "Copilot",
        ["screen-translator.screen"] = "Ctrl+Alt+F", ["screen-translator.region"] = "Ctrl+Alt+T",
        ["screen-translator.original"] = "Ctrl+Alt+O", ["screen-translator.filter"] = "Ctrl+Alt+D"
    };
    static string Folder => SuiteEnvironment.Data("shared");
    internal static string FilePath(string name) { Directory.CreateDirectory(Folder); return Path.Combine(Folder, name); }
    internal static string ReadText(string path)
    {
        // Readers retain a complete old snapshot while another app atomically replaces the path.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    internal static SuiteDocument Read()
    {
        string path = FilePath("settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SuiteDocument>(ReadText(path)) ?? throw new InvalidDataException("Invalid suite settings.") : new();
    }
    internal static void AtomicWrite<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            for (int attempt = 0; ; attempt++)
            {
                try { if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); break; }
                // 32/33: sharing or lock violation. 1175-1177: File.Replace could not swap the
                // files because another program (an indexer, a scanner) had one open.
                catch (IOException error) when (attempt < 25 && (error.HResult & 0xffff) is 32 or 33 or 1175 or 1176 or 1177) { Thread.Sleep(20); }
                catch (UnauthorizedAccessException) when (attempt < 25) { Thread.Sleep(20); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static SuiteDocument Edit(Action<SuiteDocument> edit)
    {
        using var gate = Acquire("settings.lock", true)!;
        var value = Read();
        edit(value);
        Validate(value);
        value.Revision++;
        AtomicWrite(FilePath("settings.json"), value);
        return value;
    }
    internal static FileStream? Acquire(string name, bool wait = false)
    {
        for (int attempt = 0; attempt < (wait ? 40 : 1); attempt++)
        {
            try { return new FileStream(FilePath(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { if (wait) Thread.Sleep(10); }
        }
        if (wait) throw new IOException("Suite settings are busy. Try saving again.");
        return null;
    }
    internal static void Initialize(Dictionary<string, string> defaults) => Edit(value =>
    {
        foreach (var item in defaults) if (!value.Bindings.ContainsKey(item.Key)) value.Bindings[item.Key] = SuiteChord.Parse(item.Value).Text;
    });
    internal static void SetBinding(string action, string chord) => Edit(value =>
    {
        if (!Defaults.ContainsKey(action)) throw new ArgumentException("Unknown suite shortcut action.");
        CheckAvailable(value,chord);
        Assign(value.Bindings, value.Reservations, action, chord);
    });
    static void CheckAvailable(SuiteDocument value,string chord)
    {
        var parsed=SuiteChord.Parse(chord);
        bool owned=value.Bindings.Values.Concat(value.Reservations.Values).Any(item=>SuiteChord.Parse(item).Text==parsed.Text);
        if (!owned && parsed.Text is not ("none" or "double_ctrl" or "Copilot") && !(parsed.Modifiers == 8 && parsed.Key == (uint)Keys.C))
        {
            if (!RegisterHotKey(IntPtr.Zero, 31991, parsed.Modifiers | 0x4000, parsed.Key)) throw new ArgumentException("That shortcut is reserved by Windows or used by another app. The previous binding was retained.");
            UnregisterHotKey(IntPtr.Zero, 31991);
        }
    }
    internal static void Assign(Dictionary<string,string> target, Dictionary<string,string> other, string action, string chord)
    {
        chord = SuiteChord.Parse(chord).Text;
        string previous = SuiteChord.Parse(target.GetValueOrDefault(action, "none")).Text;
        if (chord != "none")
        {
            foreach (var source in new[] { target, other })
                foreach (string owner in source.Where(item => !(ReferenceEquals(source,target) && item.Key == action) && SuiteChord.Parse(item.Value).Text == chord).Select(item => item.Key).ToArray())
                    source[owner] = previous;
        }
        target[action] = chord;
    }
    internal static void Reserve(Dictionary<string, string> reservations) => Edit(value =>
    {
        foreach (var item in reservations) { CheckAvailable(value,item.Value); Assign(value.Reservations, value.Bindings, item.Key, item.Value); }
        foreach (string removed in value.Reservations.Keys.Except(reservations.Keys).ToArray()) value.Reservations.Remove(removed);
    });
    internal static void Validate(SuiteDocument value)
    {
        var occupied = new Dictionary<string, string>();
        foreach (var item in value.Bindings.Concat(value.Reservations))
        {
            if (value.Bindings.ContainsKey(item.Key) && !Defaults.ContainsKey(item.Key)) throw new ArgumentException("Unknown suite shortcut action.");
            string chord = SuiteChord.Parse(item.Value).Text;
            if (chord == "none") continue;
            if (occupied.TryGetValue(chord, out var owner)) throw new ArgumentException($"{chord} is already assigned to {owner}. Change that binding first.");
            occupied[chord] = item.Key;
        }
        if (value.Theme is not ("system" or "light" or "dark") || !System.Text.RegularExpressions.Regex.IsMatch(value.Accent, "^#[0-9a-fA-F]{6}$")) throw new ArgumentException("Invalid suite appearance.");
    }
    internal static void Send(string action) => Edit(value =>
    {
        if (!Defaults.ContainsKey(action)) throw new ArgumentException("Unknown suite action.");
        string component = action.Split('.')[0];
        if (!value.Commands.TryGetValue(component, out var queue)) value.Commands[component] = queue = new();
        queue.RemoveAll(command => DateTime.UtcNow.Ticks - command.Created > TimeSpan.FromMinutes(2).Ticks);
        if (queue.Count >= 32) throw new InvalidOperationException("Too many pending suite commands.");
        queue.Add(new() { Number = value.Revision + 1, Action = action, Created = DateTime.UtcNow.Ticks });
    });
    internal static SuiteStatus? Status(string component)
    {
        string path = FilePath("status-" + component + ".json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<SuiteStatus>(ReadText(path)) : null; }
        catch (IOException) { return null; }
    }
    internal static bool Alive(int pid, long? started = null)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && (started is null || process.StartTime.ToUniversalTime().Ticks == started); }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal readonly record struct SuiteChord(string Text, uint Modifiers, uint Key)
{
    internal static SuiteChord Parse(string text)
    {
        text = text.Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) return new("none", 0, 0);
        if (text.Equals("double_ctrl", StringComparison.OrdinalIgnoreCase)) return new("double_ctrl", 0, 0);
        if (text.Equals("alt_space", StringComparison.OrdinalIgnoreCase)) text = "Alt+Space";
        if (text.Equals("Copilot", StringComparison.OrdinalIgnoreCase)) text = "Win+Shift+F23";
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrEmpty)) throw new ArgumentException("Choose a key or a modifier combination.");
        uint modifiers = 0;
        foreach (string part in parts[..^1])
        {
            uint flag = part.ToLowerInvariant() switch { "ctrl" or "control" => 2, "alt" => 1, "shift" => 4, "win" or "windows" => 8, _ => throw new ArgumentException("Unknown shortcut modifier.") };
            if ((modifiers & flag) != 0) throw new ArgumentException("Repeated shortcut modifier.");
            modifiers |= flag;
        }
        string keyName = parts[^1];
        if (keyName.Length == 1 && char.IsAsciiDigit(keyName[0])) keyName = "D" + keyName;
        if (!Enum.TryParse<Keys>(keyName, true, out var key) || (uint)key is 0 or > 254 || key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LWin or Keys.RWin or Keys.Escape)
            throw new ArgumentException("Choose a supported key. Escape remains the translation dismiss key.");
        if ((modifiers == 3 && key == Keys.Delete) || (modifiers == 8 && key == Keys.L) || (modifiers == 1 && key is Keys.Tab or Keys.F4)) throw new ArgumentException("That shortcut is reserved by Windows.");
        string canonical = string.Concat(new[] { (2u, "Ctrl+"), (1u, "Alt+"), (4u, "Shift+"), (8u, "Win+") }.Where(item => (modifiers & item.Item1) != 0).Select(item => item.Item2)) + (keyName.Length == 1 ? keyName.ToUpperInvariant() : key.ToString());
        if (modifiers == 12 && key == Keys.F23) canonical = "Copilot";
        return new(canonical, modifiers, (uint)key);
    }
}

internal sealed class SuiteKeyboard : NativeWindow, IDisposable
{
    readonly Dictionary<int, string> hotkeys = new();
    readonly Dictionary<int, (string Action, SuiteChord Chord)> blocked = new();
    readonly Dictionary<string, string> failures = new();
    readonly object failureLock = new();
    internal string? Error { get { lock(failureLock)return failures.Count==0?null:string.Join(" ",failures.Values); } }
    internal bool Registered(string action){lock(failureLock)return !failures.ContainsKey(action);}
    internal void RetryBlocked(){if(Handle!=IntPtr.Zero)PostMessage(Handle,0x8052,IntPtr.Zero,IntPtr.Zero);}
    readonly Dictionary<string, SuiteChord> hooks = new();
    readonly HashSet<uint> held = new(), suppressed = new();
    readonly Action<string> invoke;
    readonly HookCallback callback;
    readonly Thread thread;
    readonly ManualResetEventSlim started = new();
    Exception? startupError;
    IntPtr hook;
    long ctrlDown, lastCtrl;
    bool ctrlChord;
    long controlEvents, doubleCtrlActions;
    internal object Diagnostics => new { controlEvents=Interlocked.Read(ref controlEvents),doubleCtrlActions=Interlocked.Read(ref doubleCtrlActions),listenerThreadAlive=thread.IsAlive };
    internal SuiteKeyboard(Dictionary<string, string> bindings, Action<string> invoke)
    {
        this.invoke = invoke; callback = OnKey;
        // Keep Windows' low-level hook off the UI thread, which can wait for WebView or firmware.
        thread = new Thread(() =>
        {
            try { Initialize(bindings); started.Set(); Application.Run(); }
            catch (Exception error) { startupError = error; started.Set(); }
            finally { Cleanup(); }
        }) { IsBackground = true, Name = "Suite shortcuts" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Shortcut thread did not start.");
        if (startupError is not null) { thread.Join(); throw new InvalidOperationException(startupError.Message, startupError); }
    }
    void Initialize(Dictionary<string, string> bindings)
    {
        CreateHandle(new CreateParams());
        try
        {
            int index = 1200;
            foreach (var item in bindings)
            {
                var chord = SuiteChord.Parse(item.Value);
                if (chord.Text == "none") continue;
                if (chord.Text is "double_ctrl" or "Copilot" || chord.Modifiers == 8 && chord.Key == (uint)Keys.C) hooks[item.Key] = chord;
                else
                {
                    if (RegisterHotKey(Handle, index, chord.Modifiers | 0x4000, chord.Key)) hotkeys[index]=item.Key;
                    else {blocked[index]=(item.Key,chord);lock(failureLock)failures[item.Key]=$"{chord.Text} for {item.Key} is used by another application. Other shortcuts remain active.";}
                    index++;
                }
            }
            if (hooks.Count > 0)
            {
                hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new InvalidOperationException("Windows refused the suite shortcut listener.");
            }
        }
        catch { Cleanup(); throw; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x8051) { Application.ExitThread(); return; }
        if (message.Msg == 0x8052)
        {
            foreach(var item in blocked.ToArray())
                if(RegisterHotKey(Handle,item.Key,item.Value.Chord.Modifiers|0x4000,item.Value.Chord.Key))
                {hotkeys[item.Key]=item.Value.Action;blocked.Remove(item.Key);lock(failureLock)failures.Remove(item.Value.Action);}
            return;
        }
        if (message.Msg == 0x312 && hotkeys.TryGetValue(message.WParam.ToInt32(), out var action)) invoke(action);
        base.WndProc(ref message);
    }
    IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is 0x100 or 0x104 or 0x101 or 0x105)
        {
            uint key = (uint)Marshal.ReadInt32(data);
            bool down = message.ToInt32() is 0x100 or 0x104;
            bool control = key is 0x11 or 0xA2 or 0xA3;
            if(control)Interlocked.Increment(ref controlEvents);
            // A release may be lost across desktop switches or swallowed by another hook.
            // Windows' async state still describes keys before this event, including Ctrl repeats.
            if (control && down) held.RemoveWhere(value => !Pressed((int)value));
            bool fresh = down ? held.Add(key) : held.Remove(key);
            long now = Environment.TickCount64;
            if (down && fresh)
            {
                if (control) { ctrlDown = now; ctrlChord = held.Any(value => value is not (0x11 or 0xA2 or 0xA3)); }
                else { lastCtrl = 0; if (held.Any(value => value is 0x11 or 0xA2 or 0xA3)) ctrlChord = true; }
            }
            if (!down && fresh && control && !held.Any(value => value is 0x11 or 0xA2 or 0xA3))
            {
                if (!ctrlChord && now - ctrlDown < 600)
                {
                    if (lastCtrl > 0 && now - lastCtrl <= 450)
                    {
                        lastCtrl = 0;
                        Interlocked.Increment(ref doubleCtrlActions);
                        foreach (var item in hooks.Where(item => item.Value.Text == "double_ctrl")) SafeInvoke(item.Key);
                    }
                    else lastCtrl = now;
                }
                else lastCtrl = 0;
            }
            if (!down && suppressed.Remove(key)) return new IntPtr(1);
            uint modifiers = (Pressed(0x11) ? 2u : 0) | (Pressed(0x12) ? 1u : 0) | (Pressed(0x10) ? 4u : 0) | (Pressed(0x5B) || Pressed(0x5C) ? 8u : 0);
            var match = hooks.FirstOrDefault(item => item.Value.Key == key && item.Value.Modifiers == modifiers && item.Value.Text != "double_ctrl");
            if (down && (suppressed.Contains(key) || match.Key is not null))
            {
                suppressed.Add(key);
                if (fresh && match.Key is not null) SafeInvoke(match.Key);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }
    void SafeInvoke(string action) { try { invoke(action); } catch { /* An app failure must not break the keyboard hook chain. */ } }
    static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public void Dispose()
    {
        if (thread.IsAlive && Handle != IntPtr.Zero) PostMessage(Handle, 0x8051, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != thread && !thread.Join(5000)) throw new InvalidOperationException("Shortcut thread is still stopping; ownership was retained.");
        started.Dispose();
    }
    void Cleanup()
    {
        foreach (int id in hotkeys.Keys) UnregisterHotKey(Handle, id);
        hotkeys.Clear();
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        if (Handle != IntPtr.Zero) DestroyHandle();
        GC.KeepAlive(callback);
    }
    delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr owner, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr owner, int id);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr a, IntPtr b);
}

internal sealed class SuiteClient : IDisposable
{
    readonly string component;
    readonly Action<string> invoke;
    readonly Action<SuiteDocument> changed;
    readonly Func<string> status;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    readonly Dictionary<string, FileStream> leases = new();
    SuiteKeyboard? keyboard;
    string signature = "";
    long revision = -1, consumed, lastStatus;
    long lastRetry;
    bool? lastDefer;
    readonly long started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    internal string? Error { get; private set; }
    internal bool Hub => component == "pc-manager";
    internal bool Registered(string action) => leases.ContainsKey(action.Split('.')[0]) && keyboard?.Registered(action)==true;
    internal SuiteClient(string component, Dictionary<string, string> defaults, Action<string> invoke, Action<SuiteDocument> changed, Func<string> status)
    {
        this.component = component; this.invoke = invoke; this.changed = changed; this.status = status;
        SuiteStore.Initialize(defaults);
        string checkpoint = SuiteStore.FilePath("consumed-" + component + ".txt");
        if (File.Exists(checkpoint)) long.TryParse(File.ReadAllText(checkpoint), out consumed);
        timer.Tick += (_, _) => Poll();
        Poll(); timer.Start();
    }
    internal static bool HubRunning()
    {
        try
        {
            string path = SuiteStore.FilePath("hub.json");
            var lease = File.Exists(path) ? JsonSerializer.Deserialize<SuiteLease>(SuiteStore.ReadText(path)) : null;
            return lease is not null && DateTime.UtcNow.Ticks - lease.Updated < TimeSpan.FromSeconds(3).Ticks && SuiteStore.Alive(lease.Pid, lease.Started);
        }
        catch (IOException) { return false; }
    }
    internal void Poll()
    {
        try
        {
            Error = keyboard?.Error;
            if (Hub) SuiteStore.AtomicWrite(SuiteStore.FilePath("hub.json"), new SuiteLease(Environment.ProcessId, started, DateTime.UtcNow.Ticks));
            var document = SuiteStore.Read();
            bool defer = !Hub && HubRunning();
            bool settingsChanged = document.Revision != revision || lastDefer != defer;
            string[] wanted = Hub ? new[] { "file-search", "screen-translator" }.Where(SuiteEnvironment.Installed).ToArray() : defer ? Array.Empty<string>() : new[] { component };
            var wantedSet = wanted.ToHashSet();
            // Close registrations before releasing ownership, so the next owner can register immediately.
            if (leases.Keys.Any(owner => !wantedSet.Contains(owner))) Release();
            foreach (string owner in wanted)
                if (!leases.ContainsKey(owner) && SuiteStore.Acquire("owner-" + owner + ".lock") is { } lease) leases[owner] = lease;
            var bindings = document.Bindings.Where(item => leases.ContainsKey(item.Key.Split('.')[0]) && (!document.PausedUntil.TryGetValue(item.Key.Split('.')[0], out var until) || until < DateTime.UtcNow.Ticks)).ToDictionary(item => item.Key, item => item.Value);
            if (Hub && (!document.PausedUntil.TryGetValue("pc-manager",out var hubPause) || hubPause<DateTime.UtcNow.Ticks))
                foreach (var item in document.Reservations.Where(item=>item.Key=="PC Manager Copilot"||item.Key.StartsWith("PC Manager shortcut ")))
                    bindings[item.Key=="PC Manager Copilot"?"pc-manager.copilot":"pc-manager.custom."+item.Key["PC Manager shortcut ".Length..]]=item.Value;
            string nextSignature = JsonSerializer.Serialize(bindings.OrderBy(item => item.Key));
            if (nextSignature != signature)
            {
                keyboard?.Dispose(); keyboard = null;
            }
            // Release displaced suite keys before the hub reconfigures its custom keys.
            if (settingsChanged) { changed(document); revision = document.Revision; lastDefer = defer; }
            if (nextSignature != signature)
            {
                try { keyboard = new(bindings, SafeInvoke); signature = nextSignature; Error = keyboard.Error; }
                catch (Exception error) { signature = ""; Error = error.Message; }
            }
            if(keyboard?.Error is not null && Environment.TickCount64-lastRetry>1000)
            {lastRetry=Environment.TickCount64;keyboard.RetryBlocked();}
            if (!Hub && document.Commands.TryGetValue(component, out var commands))
                foreach (var command in commands.Where(command => command.Number > consumed).OrderBy(command => command.Number))
                {
                    consumed = command.Number;
                    File.WriteAllText(SuiteStore.FilePath("consumed-" + component + ".txt"), consumed.ToString());
                    if (DateTime.UtcNow.Ticks - command.Created < TimeSpan.FromMinutes(2).Ticks) SafeInvoke(command.Action);
                }
            if (DateTime.UtcNow.Ticks - lastStatus > TimeSpan.FromSeconds(1).Ticks)
            {
                lastStatus = DateTime.UtcNow.Ticks;
                SuiteStore.AtomicWrite(SuiteStore.FilePath("status-" + component + ".json"), new SuiteStatus(component, Environment.ProcessId, status(), Hub ? "PC Manager" : defer ? "PC Manager" : "Standalone", Error, lastStatus, leases.Keys.ToArray(),keyboard?.Diagnostics));
            }
        }
        catch (Exception error) { Error = error.Message; }
    }
    void SafeInvoke(string action) { try { invoke(action); } catch (Exception error) { Error = error.Message; } }
    internal void Suspend(bool paused)
    {
        SuiteStore.Edit(value => { foreach (string owner in new[] { "pc-manager", "file-search", "screen-translator" }) value.PausedUntil[owner] = paused ? DateTime.UtcNow.AddMinutes(2).Ticks : 0; });
        Poll();
    }
    internal void ReleaseKeyboard() { keyboard?.Dispose(); keyboard = null; signature = ""; }
    void Release()
    {
        keyboard?.Dispose(); keyboard = null; signature = "";
        foreach (var lease in leases.Values) lease.Dispose();
        leases.Clear();
    }
    public void Dispose()
    {
        timer.Stop(); timer.Dispose(); Release();
        // Releasing the hub lease is best effort: a busy file must not crash an app that is closing.
        try { if (Hub) SuiteStore.AtomicWrite(SuiteStore.FilePath("hub.json"), new SuiteLease(0, 0, 0)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
