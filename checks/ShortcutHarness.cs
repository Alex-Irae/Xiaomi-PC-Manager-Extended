// Purpose: exercise shared bindings and real Windows hotkey handover with isolated fake app agents.
// Dependencies: .NET 8 Desktop/Win32 only. Outputs: numbered check directory, config.json and summary.json.
// Command after compilation: private-dotnet ShortcutHarness.dll verify. Never starts PC hardware or screen capture.
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
}
