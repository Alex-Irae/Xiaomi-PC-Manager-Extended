// Purpose: exercise shared bindings and real Windows hotkey handover with isolated fake app agents.
// Dependencies: .NET 8 Desktop/Win32 only. Outputs: numbered check directory, config.json and summary.json.
// Commands after compilation: private-dotnet ShortcutHarness.dll verify; or observe OUTPUT_JSON (45 seconds).
// Observe records only Ctrl events and gesture counts. Never starts PC hardware or screen capture.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using XiaomiRevamp.Suite;

internal static class ShortcutHarness
{
    static string Root => AppContext.BaseDirectory;
    static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static void Wait(Func<bool> condition, string label)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(12))
        {
            try { if (condition()) { Console.WriteLine("PASS: " + label); return; } } catch (IOException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException(label);
    }
    static Process Agent(string component)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(Root, "ShortcutHarness.dll")); start.ArgumentList.Add("agent"); start.ArgumentList.Add(component);
        return Process.Start(start)!;
    }
    static SuiteStatus? Status(string component) => SuiteStore.Status(component);
    static bool Owns(string component, params string[] expected)
    {
        var status = Status(component);
        return status is not null && status.Error is null && expected.All(status.Owned.Contains);
    }
    static bool Mirrored(string component, string action, string chord)
    {
        string path = Path.Combine(Root, "mirror-" + component + ".json");
        if (!File.Exists(path)) return false;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        return values.TryGetValue(action, out var value) && value == chord;
    }
    static bool Available(string chord)
    {
        var parsed = SuiteChord.Parse(chord);
        bool available = RegisterHotKey(IntPtr.Zero, 29991, parsed.Modifiers | 0x4000, parsed.Key);
        if (available) UnregisterHotKey(IntPtr.Zero, 29991);
        return available;
    }
    [STAThread]
    static int Main(string[] args)
    {
        if(args[0]=="observe")
        {
            ApplicationConfiguration.Initialize();var samples=new List<object>();int actions=0;
            Hook probe=(code,message,data)=>{if(code>=0&&(uint)Marshal.ReadInt32(data) is 0x11 or 0xA2 or 0xA3)samples.Add(new{key=Marshal.ReadInt32(data),kind=message.ToInt32(),tick=Environment.TickCount64});return CallNextHookEx(IntPtr.Zero,code,message,data);};
            IntPtr listener=SetWindowsHookEx(13,probe,GetModuleHandle(null),0);
            if(listener==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            using var keyboard=new SuiteKeyboard(new(){["file-search.open"]="double_ctrl"},_=>Interlocked.Increment(ref actions));
            using var window=new Form{Text="Xiaomi Revamp keyboard probe",ClientSize=new Size(470,90),ShowInTaskbar=true};
            window.Controls.Add(new Label{Text="Double Ctrl diagnostic. Only Ctrl events are counted.",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter});
            using var end=new System.Windows.Forms.Timer{Interval=45000};end.Tick+=(_,_)=>window.Close();end.Start();
            try{Application.Run(window);}
            finally{UnhookWindowsHookEx(listener);GC.KeepAlive(probe);File.WriteAllText(args[1],JsonSerializer.Serialize(new{actions,samples},new JsonSerializerOptions{WriteIndented=true}));}
            return 0;
        }
        if (args[0] == "agent")
        {
            ApplicationConfiguration.Initialize(); string component = args[1];
            using var client = new SuiteClient(component, new(), action => File.AppendAllText(Path.Combine(Root, "actions-" + component + ".txt"), action + "\n"),
                document => SuiteStore.AtomicWrite(Path.Combine(Root, "mirror-" + component + ".json"), document.Bindings), () => "Check agent");
            using var stop = new System.Windows.Forms.Timer { Interval = 100 };
            stop.Tick += (_, _) => { if (File.Exists(Path.Combine(Root, "stop-" + Environment.ProcessId))) Application.ExitThread(); };
            stop.Start(); Application.Run(); return 0;
        }
        var children = new List<Process>(); var evidence = new Dictionary<string, object>();
        try
        {
            File.WriteAllText(Path.Combine(Root, "suite-install.json"), JsonSerializer.Serialize(new { schema = 1, profileId = "check", portable = true }));
            foreach (string component in new[] { "file-search", "screen-translator" })
            {
                string path = SuiteEnvironment.Executable(component); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "Check marker only, never executed.");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "suite-component.json"), JsonSerializer.Serialize(new { schema = 1, component }));
            }
            SuiteStore.Initialize(SuiteStore.Defaults);
            string snapshot = SuiteStore.FilePath("atomic-check.json");
            SuiteStore.AtomicWrite(snapshot, new { version = 1 });
            using (var reader = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                SuiteStore.AtomicWrite(snapshot, new { version = 2 });
                using var old = JsonDocument.Parse(reader);
                Assert(old.RootElement.GetProperty("version").GetInt32() == 1, "Replacement changed a reader's snapshot.");
            }
            using (var blocked = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var release = Task.Run(() => { Thread.Sleep(80); blocked.Dispose(); });
                SuiteStore.AtomicWrite(snapshot, new { version = 3 });
                release.Wait();
            }
            Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(snapshot)!, "*.pending").Any(), "Atomic writes leaked a temporary file.");
            Console.WriteLine("PASS: atomic replacement preserves reader snapshots and retries brief Windows file contention");
            using(var gesture=new SuiteKeyboard(new(){["file-search.open"]="double_ctrl"},action=>evidence["doubleCtrlRecovered"]=true))
            {
                var dispatch=typeof(SuiteKeyboard).GetMethod("OnKey",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                IntPtr eventData=Marshal.AllocHGlobal(24);
                try
                {
                    void Key(uint key,bool down){Marshal.WriteInt32(eventData,(int)key);dispatch.Invoke(gesture,new object[]{0,new IntPtr(down?0x100:0x101),eventData});}
                    // A hook can miss a release during a desktop transition or another listener's suppression.
                    Key(0x41,true);Key(0xA2,true);Key(0xA2,false);Key(0xA2,true);Key(0xA2,false);
                    Assert(evidence.ContainsKey("doubleCtrlRecovered"),"A missed non-Ctrl release permanently disabled Double Ctrl.");
                    Console.WriteLine("PASS: Double Ctrl recovers after a missed key release");
                }
                finally{Marshal.FreeHGlobal(eventData);}
            }
            Assert(RegisterHotKey(IntPtr.Zero,29993,0x4007,(uint)Keys.F17),"Could not reserve the conflict fixture.");
            using(var partial=new SuiteKeyboard(new(){["good"]="Ctrl+Alt+Shift+F16",["blocked"]="Ctrl+Alt+Shift+F17"},_=>{}))
            {
                Assert(partial.Error is not null && partial.Registered("good")&&!partial.Registered("blocked")&&!Available("Ctrl+Alt+Shift+F16"),"One external conflict disabled unrelated shortcuts.");
                UnregisterHotKey(IntPtr.Zero,29993);
                Wait(()=>{partial.RetryBlocked();return partial.Error is null&&!Available("Ctrl+Alt+Shift+F17");},"a released external shortcut conflict recovers without losing the working keys");
            }
            Console.WriteLine("PASS: partial registration reports conflicts and preserves other shortcut actions");
            SuiteStore.Edit(value => { foreach (string action in value.Bindings.Keys.ToArray()) value.Bindings[action] = "none"; value.Bindings["file-search.open"] = "Ctrl+Alt+Shift+F21"; value.Bindings["screen-translator.toggle"] = "Ctrl+Alt+Shift+F22"; });
            Assert(SuiteChord.Parse("Win+Shift+F23").Text == "Copilot", "Copilot aliases disagree.");
            Assert(Available("Ctrl+Alt+Shift+F21") && Available("Ctrl+Alt+Shift+F22"), "Check shortcuts are already in use.");
            var search = Agent("file-search"); children.Add(search); var translator = Agent("screen-translator"); children.Add(translator);
            Wait(() => Owns("file-search", "file-search") && Owns("screen-translator", "screen-translator"), "standalone applications own their shortcuts");
            Assert(!Available("Ctrl+Alt+Shift+F21") && !Available("Ctrl+Alt+Shift+F22"), "Standalone ownership did not register Windows hotkeys.");
            var hub = Agent("pc-manager"); children.Add(hub);
            Wait(() => Owns("pc-manager", "file-search", "screen-translator") && Status("file-search")?.Owner == "PC Manager" && Status("screen-translator")?.Owner == "PC Manager", "PC Manager takes ownership from both applications");
            Assert(Status("file-search")!.Owned.Length == 0 && Status("screen-translator")!.Owned.Length == 0, "A component kept shortcut ownership while the hub was active.");
            SuiteStore.SetBinding("file-search.open", "Ctrl+Alt+Shift+F20");
            Wait(() => Mirrored("file-search", "file-search.open", "Ctrl+Alt+Shift+F20") && Mirrored("pc-manager", "file-search.open", "Ctrl+Alt+Shift+F20") && !Available("Ctrl+Alt+Shift+F20"), "hub edits reach the search app and update the Windows registration");
            SuiteStore.SetBinding("screen-translator.toggle", "Ctrl+Alt+Shift+F19");
            Wait(() => Mirrored("screen-translator", "screen-translator.toggle", "Ctrl+Alt+Shift+F19") && Mirrored("pc-manager", "screen-translator.toggle", "Ctrl+Alt+Shift+F19"), "app edits are visible in PC Manager");
            SuiteStore.SetBinding("screen-translator.toggle", "Ctrl+Alt+Shift+F20");
            Wait(() => Mirrored("file-search", "file-search.open", "Ctrl+Alt+Shift+F19") && Mirrored("screen-translator", "screen-translator.toggle", "Ctrl+Alt+Shift+F20") && Owns("pc-manager", "file-search", "screen-translator"), "assigned suite keys swap and synchronize without changing actions");
            SuiteStore.Reserve(new() { ["PC Manager shortcut 1"] = "Ctrl+Alt+Shift+F18" });
            SuiteStore.SetBinding("screen-translator.toggle", "Ctrl+Alt+Shift+F18");
            Assert(SuiteStore.Read().Reservations["PC Manager shortcut 1"] == "Ctrl+Alt+Shift+F20", "Suite-to-custom swap lost the custom action's previous key.");
            SuiteStore.Reserve(new() { ["PC Manager shortcut 1"] = "Ctrl+Alt+Shift+F18" });
            Assert(SuiteStore.Read().Bindings["screen-translator.toggle"] == "Ctrl+Alt+Shift+F20", "Custom-to-suite swap lost the previous key.");
            SuiteStore.Reserve(new());
            Wait(() => Available("Ctrl+Alt+Shift+F18"), "removed custom shortcut releases its Windows registration");
            Assert(RegisterHotKey(IntPtr.Zero, 29992, 0x4007, (uint)Keys.F18), "Could not reserve the external conflict fixture.");
            bool refused = false;
            try { SuiteStore.SetBinding("screen-translator.toggle", "Ctrl+Alt+Shift+F18"); } catch (ArgumentException) { refused = true; }
            finally { UnregisterHotKey(IntPtr.Zero, 29992); }
            Assert(refused && SuiteStore.Read().Bindings["screen-translator.toggle"] == "Ctrl+Alt+Shift+F20", "External conflict changed saved state.");
            Console.WriteLine("PASS: suite/custom swaps retain actions; external Windows conflicts retain saved state");
            void Press(SuiteShortcutDialog dialog,uint key,bool down) { Assert(dialog.ProcessKey(key,down,true), "Recorded key escaped the listener.");Application.DoEvents(); }
            using(var dialog=new SuiteShortcutDialog("none"))
            {
                Press(dialog,0xa2,true);Press(dialog,0xa4,true);Press(dialog,(uint)Keys.K,true);
                Assert(dialog.Selected=="Ctrl+Alt+K", "Physical combination recorded incorrectly.");
                Press(dialog,(uint)Keys.K,true);Press(dialog,(uint)Keys.K,false);Press(dialog,0xa4,false);Press(dialog,0xa2,false);
                Assert(dialog.Controls.OfType<Button>().Single(button=>button.Text=="Use shortcut").Enabled,"Recorder did not wait for all keys to release.");
                Assert(!dialog.ProcessKey((uint)Keys.A,true,true),"Completed recorder kept swallowing unrelated typing.");
            }
            using(var dialog=new SuiteShortcutDialog("none"))
            {Press(dialog,0xa2,true);Press(dialog,0xa2,false);Press(dialog,0xa2,true);Press(dialog,0xa2,false);Assert(dialog.Selected=="double_ctrl","Double Ctrl did not record.");}
            using(var dialog=new SuiteShortcutDialog("none"))
            {Press(dialog,0x5b,true);Press(dialog,0xa0,true);Press(dialog,(uint)Keys.F23,true);Press(dialog,(uint)Keys.F23,false);Press(dialog,0xa0,false);Press(dialog,0x5b,false);Assert(dialog.Selected=="Copilot","Copilot did not normalize.");}
            using(var dialog=new SuiteShortcutDialog("none"))
            {Assert(!dialog.ProcessKey((uint)Keys.A,true,false),"Recorder captured typing from another window.");Press(dialog,0xa4,true);Press(dialog,(uint)Keys.Tab,true);Assert(dialog.Selected=="","Windows-reserved chord was accepted.");Press(dialog,(uint)Keys.Tab,false);Press(dialog,0xa4,false);}
            Console.WriteLine("PASS: recorder combinations, repeats, release, Double Ctrl, Copilot and focus guard");
            hub.Kill(); hub.WaitForExit();
            Wait(() => Owns("file-search", "file-search") && Owns("screen-translator", "screen-translator") && Status("file-search")?.Owner == "Standalone" && Status("screen-translator")?.Owner == "Standalone", "hub crash returns ownership to both standalone apps");
            Assert(!Available("Ctrl+Alt+Shift+F20") && !Available("Ctrl+Alt+Shift+F19"), "Fallback registrations disappeared.");
            var replacement = Agent("pc-manager"); children.Add(replacement);
            Wait(() => Owns("pc-manager", "file-search", "screen-translator") && Status("file-search")?.Owned.Length == 0 && Status("screen-translator")?.Owned.Length == 0, "restarted hub reclaims ownership without competing handlers");
            File.WriteAllText(Path.Combine(Root, "stop-" + replacement.Id), "stop"); Assert(replacement.WaitForExit(7000), "Hub did not exit gracefully.");
            Wait(() => Owns("file-search", "file-search") && Owns("screen-translator", "screen-translator"), "graceful hub exit restores standalone ownership");
            evidence["passed"] = true; evidence["standalone"] = true; evidence["hubTakeover"] = true; evidence["bidirectionalBindings"] = true; evidence["assignedKeysSwapped"] = true; evidence["externalConflictsRejected"] = true; evidence["recorder"] = true; evidence["crashFallback"] = true; evidence["restartTakeover"] = true; evidence["gracefulFallback"] = true;
            return 0;
        }
        catch (Exception error) { evidence["passed"] = false; evidence["error"] = error.ToString(); Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited) { File.WriteAllText(Path.Combine(Root, "stop-" + child.Id), "stop"); if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); } }
                child.Dispose();
            }
            File.WriteAllText(Path.Combine(Root, "summary.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr owner, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr owner, int id);
    delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int kind,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr handle);
    [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr handle,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string? name);
}
