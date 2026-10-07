// Purpose: selectable offline suite installation, standalone optional app setup and owned-file uninstall.
// Dependencies: Windows .NET Framework 4.8, packages.json and a verified content store or component ZIPs.
// Outputs: dedicated application folders, per-user shortcuts/startup entries and ownership metadata.
// Build: python tools/build_suite.py. Run: Xiaomi-Revamp-Setup.exe, File-Search-Setup.exe or Screen-Translator-Setup.exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;
using System.Security.AccessControl;
using System.Runtime.Versioning;

[assembly: AssemblyTitle("Xiaomi Revamp Setup")]
[assembly: AssemblyVersion("0.3.15.0")]
[assembly: AssemblyFileVersion("0.3.15.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8")]

internal static class Setup
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 64000000 };
    static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;
    static readonly Dictionary<string, string> Folders = new Dictionary<string, string> { { "pc-manager", "PC Manager" }, { "file-search", "AI Center" }, { "screen-translator", "Screen Translator" } };
    static readonly Dictionary<string, string> Executables = new Dictionary<string, string> { { "pc-manager", "PCManager.exe" }, { "file-search", "AI Center.exe" }, { "screen-translator", "ScreenTranslator.exe" } };
    static string Sid { get { return WindowsIdentity.GetCurrent().User.Value; } }
    static bool Admin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
    const string Version = "0.3.15"; // suite release; tools/build_suite.py refuses to build when these differ from its table
    static readonly Dictionary<string, string> Versions = new Dictionary<string, string> { { "pc-manager", "0.2.13" }, { "file-search", "0.3.11" }, { "screen-translator", "0.2.8" } };
    static string Uninstaller { get { return "Uninstall.exe"; } }
    static string Expand(string path) { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path).Replace("{sid}", Sid)); }
    static Dictionary<string, object> Marker(string root) { return Object(Json.DeserializeObject(File.ReadAllText(Path.Combine(root, "suite-install.json")))); }
    static bool AllUsers(string root) { var marker = Marker(root); return marker.ContainsKey("allUsers") && (bool)marker["allUsers"]; }
    static string DataPath(string root, string component)
    {
        var marker = Marker(root);
        if (component != "shared" && marker.ContainsKey("componentData")) return Expand((string)Object(marker["componentData"])[component]);
        string data = marker.ContainsKey("dataRoot") ? Expand((string)marker["dataRoot"]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaomiRevampSuite", Profile(root));
        return Path.Combine(data, component);
    }
    static string DefaultData(string root) { return @"%LOCALAPPDATA%\XiaomiRevamp\" + Profile(root); }
    static void Writable(string folder, bool allUsers)
    {
        var directory = new DirectoryInfo(folder); var acl = directory.GetAccessControl();
        var identity = allUsers ? new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null) : new SecurityIdentifier(Sid);
        acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    static string DataTemplate(string choice, string fallback)
    {
        if (string.IsNullOrWhiteSpace(choice)) return fallback;
        return Path.Combine(Target(choice), "Users", "{sid}");
    }
    static void ClaimData(string path, string root, string component)
    {
        path = Target(path); string metadata = Path.Combine(path, ".revamp-data.json");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() && !File.Exists(metadata))
            throw new InvalidOperationException("The chosen data folder already contains unrelated files: " + path);
        if (File.Exists(metadata))
        {
            var owner = Object(Json.DeserializeObject(File.ReadAllText(metadata)));
            if ((string)owner["installRoot"] != root || (string)owner["component"] != component) throw new InvalidOperationException("Data folder belongs to another installation: " + path);
        }
        Directory.CreateDirectory(path); Save(metadata, new { schema = 1, installRoot = root, component = component });
    }
    static void RemoveData(string root, string component)
    {
        string path = Target(DataPath(root, component)), metadata = Path.Combine(path, ".revamp-data.json");
        if (!Directory.Exists(path)) return;
        if (!File.Exists(metadata)) throw new InvalidOperationException("Unmarked data was retained: " + path);
        var owner = Object(Json.DeserializeObject(File.ReadAllText(metadata)));
        if ((string)owner["installRoot"] != root || (string)owner["component"] != component) throw new InvalidOperationException("Data ownership does not match.");
        SafeDelete(path);
    }
    static void SafeDelete(string path)
    {
        path = Target(path);
        VerifyTree(path);
        Directory.Delete(path, true);
    }
    static void VerifyTree(string path)
    {
        foreach (string entry in Directory.GetFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("A junction or symbolic link was retained: " + entry);
            if ((attributes & FileAttributes.Directory) != 0) VerifyTree(entry);
        }
    }
    static string Quote(string text)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char character in text)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    static void MoveDirectory(string source, string destination)
    {
        // Newly extracted executable files can still be held by virus scanners.
        // Retry only transient sharing/access errors; never replace a target.
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (IOException error)
            {
                int code = error.HResult & 0xffff;
                if (attempt >= 19 || (code != 5 && code != 32) || !Directory.Exists(source) || Directory.Exists(destination)) throw;
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt >= 19 || !Directory.Exists(source) || Directory.Exists(destination)) throw;
            }
            System.Threading.Thread.Sleep(250);
        }
    }
    static Dictionary<string, object> Object(object value) { return (Dictionary<string, object>)value; }
    static string Child(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Absolute package path rejected.");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path leaves its application folder.");
        return path;
    }
    static string Target(string path)
    {
        string target = Path.GetFullPath(path).TrimEnd('\\');
        if (target == Path.GetPathRoot(target).TrimEnd('\\') || new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }.Any(value => target.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a dedicated application subfolder.");
        for (var directory = new DirectoryInfo(target); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Installation through a junction or symbolic link is unsupported.");
        return target;
    }
    static string Profile(string root)
    {
        using (var sha = SHA256.Create()) return "install-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
    }
    static void Save(string path, object value)
    {
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        File.WriteAllText(pending, Json.Serialize(value));
        if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
    }
    static void ConfigureProfile(string root, string component, bool startup)
    {
        if (component == "pc-manager") return;
        string directory = DataPath(root, component), path = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        var config = File.Exists(path) ? Object(Json.DeserializeObject(File.ReadAllText(path))) :
            component == "file-search" ? Object(Json.DeserializeObject(File.ReadAllText(Path.Combine(root, Folders[component], "config.example.json")))) : new Dictionary<string, object>();
        if (component == "file-search" && !File.Exists(path))
        {
            config["model_path"] = Path.Combine(root, Folders[component], "models", "qwen3-embedding");
            config["excluded_folders"] = new[] { root, Expand((string)Marker(root)["dataRoot"]), directory };
        }
        config[component == "file-search" ? "run_at_startup" : "autostart"] = startup;
        Save(path, config);
    }
    static void Shortcut(string path, string target, string arguments)
    {
        Type type = Type.GetTypeFromProgID("WScript.Shell"); dynamic shell = Activator.CreateInstance(type);
        dynamic link = shell.CreateShortcut(path); link.TargetPath = target; link.Arguments = arguments; link.WorkingDirectory = Path.GetDirectoryName(target); link.Save();
        System.Runtime.InteropServices.Marshal.ReleaseComObject(link); System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
    }
    static void RemoveShortcut(string path, string target)
    {
        if (!File.Exists(path)) return;
        Type type = Type.GetTypeFromProgID("WScript.Shell"); dynamic shell = Activator.CreateInstance(type);
        dynamic link = shell.CreateShortcut(path);
        try
        {
            // A user may have repointed our shortcut to a different application.
            if (string.Equals(Path.GetFullPath((string)link.TargetPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }
        catch (ArgumentException) { }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(link); System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
    }
    static void Register(string root, string component, bool desktop, bool startup)
    {
        ConfigureProfile(root, component, startup);
        string profile = Profile(root), app = Child(root, Folders[component]), executable = Path.Combine(app, Executables[component]);
        bool all = AllUsers(root);
        string menu = Path.Combine(Environment.GetFolderPath(all ? Environment.SpecialFolder.CommonStartMenu : Environment.SpecialFolder.StartMenu), "Programs", "Xiaomi Revamp"); Directory.CreateDirectory(menu);
        string arguments = component == "pc-manager" ? "--manager" : component == "file-search" ? "--center" : "";
        Shortcut(Path.Combine(menu, Folders[component] + ".lnk"), executable, arguments);
        if (component == "file-search")
        {
            RemoveShortcut(Path.Combine(menu, "AI Center settings.lnk"), executable);
            Shortcut(Path.Combine(menu, "File Search.lnk"), executable, "--search");
        }
        if (desktop) Shortcut(Path.Combine(Environment.GetFolderPath(all ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), Folders[component] + ".lnk"), executable, arguments);
        using (var key = (all ? Registry.LocalMachine : Registry.CurrentUser).CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiRevamp." + profile + "." + component))
        {
            key.SetValue("DisplayName", Folders[component]); key.SetValue("DisplayVersion", Versions[component]); key.SetValue("InstallLocation", app);
            key.SetValue("DisplayIcon", executable); key.SetValue("UninstallString", Quote(Path.Combine(app, Uninstaller)) + " --target " + Quote(root) + " --component " + component);
            key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        }
        if (component == "pc-manager")
        {
            if (!Admin) throw new InvalidOperationException("PC Manager startup settings require administrator approval.");
            if (startup)
            {
                using (var process = Process.Start(new ProcessStartInfo(executable, "--register-startup") { UseShellExecute = false, CreateNoWindow = true })) { process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Windows refused PC Manager startup registration."); }
            }
            else using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), "/Delete /TN " + Quote("XiaomiRevampSuite_" + Identity(root) + "_" + Sid) + " /F") { UseShellExecute = false, CreateNoWindow = true })) process.WaitForExit();
        }
        else using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            string name = "XiaomiRevampSuite." + (component == "file-search" ? "Search." : "Translator.") + Identity(root);
            if (startup) key.SetValue(name, Quote(executable) + " --tray");
            else key.DeleteValue(name, false);
        }
    }
    static string Identity(string root)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Replace("-", "").Substring(0, 12);
    }
    static void Install(string root, string[] selected, bool desktop, string[] startup, string dataChoice, string searchChoice, bool allUsers, bool development, Action<string> progress)
    {
        root = Target(root);
        string ownership = Path.Combine(root, "suite-owned.json");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(ownership)) throw new InvalidOperationException("Choose an empty dedicated folder, or an existing suite installation.");
        var package = Object(Json.DeserializeObject(File.ReadAllText(Path.Combine(Base, "packages.json"))));
        var components = Object(package["components"]);
        var owned = File.Exists(ownership) ? Object(Json.DeserializeObject(File.ReadAllText(ownership))) : new Dictionary<string, object>();
        var priorMarker = File.Exists(Path.Combine(root, "suite-install.json")) ? Marker(root) : null;
        string dataTemplate = priorMarker != null && priorMarker.ContainsKey("dataRoot") ? (string)priorMarker["dataRoot"] : DataTemplate(dataChoice, DefaultData(root));
        var paths = priorMarker != null && priorMarker.ContainsKey("componentData") ? Object(priorMarker["componentData"]) : Folders.ToDictionary(item => item.Key, item => (object)Path.Combine(dataTemplate, item.Key));
        if (priorMarker != null && (!string.IsNullOrWhiteSpace(dataChoice) || !string.IsNullOrWhiteSpace(searchChoice))) throw new InvalidOperationException("Changing data locations during an upgrade is unsupported. Existing paths are retained; use a fresh installation to choose new paths.");
        if (!string.IsNullOrWhiteSpace(searchChoice)) paths["file-search"] = Path.Combine(DataTemplate(searchChoice, ""), "file-search");
        if (priorMarker != null && priorMarker.ContainsKey("allUsers") && (bool)priorMarker["allUsers"] != allUsers) throw new InvalidOperationException("Keep the installation's existing user scope when upgrading.");
        string toolchain = Path.Combine(Base, "development-toolchain.zip");
        string store = package.ContainsKey("contentStore") ? Child(Base, (string)Object(package["contentStore"])["payload"]) : "";
        if (store.Length > 0 && (!File.Exists(store) || Hash(store) != (string)Object(package["contentStore"])["sha256"]))
            throw new InvalidDataException("Installer content checksum failed.");
        if (development && (!package.ContainsKey("developmentToolchain") || (store.Length == 0 && (!File.Exists(toolchain) || Hash(toolchain) != (string)Object(package["developmentToolchain"])["sha256"]))))
            throw new InvalidDataException("Keep the verified development-toolchain.zip beside setup when installing development copies.");
        // Verify every selected archive before changing any installed component.
        foreach (string component in selected)
        {
            if (!Folders.ContainsKey(component)) throw new InvalidOperationException("Unknown component.");
            var item = Object(components[component]); string payload = Child(Base, (string)item["payload"]);
            if ((string)item["folder"] != Folders[component] || (store.Length == 0 && Hash(payload) != (string)item["sha256"])) throw new InvalidDataException("Package checksum failed: " + component);
        }
        Directory.CreateDirectory(root);
        string stage = Child(root, ".staging-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")); Directory.CreateDirectory(stage);
        var commits = new List<Tuple<string, string>>();
        var metadata = new Dictionary<string, byte[]>();
        foreach (string name in new[] { "suite-install.json", "suite-owned.json", "Uninstall.exe" })
        {
            string path = Path.Combine(root, name);
            metadata[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        try
        {
            foreach (string component in selected)
            {
                progress("Preparing " + Folders[component]); var item = Object(components[component]); var files = Object(item["files"]);
                string prepared = Child(stage, Folders[component]); Directory.CreateDirectory(prepared);
                if (store.Length > 0) ExtractStore(store, files, prepared);
                else using (var archive = ZipFile.OpenRead(Child(Base, (string)item["payload"])))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string relative = entry.FullName;
                        if (!files.ContainsKey(relative)) throw new InvalidDataException("Unlisted package file: " + relative);
                        string path = Child(prepared, relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
                        entry.ExtractToFile(path); if (Hash(path) != (string)files[relative]) throw new InvalidDataException("File checksum failed: " + relative);
                    }
                }
                if (Directory.EnumerateFiles(prepared, "*", SearchOption.AllDirectories).Count() != files.Count) throw new InvalidDataException("Incomplete component payload.");
                File.Copy(Path.Combine(Base, Uninstaller), Path.Combine(prepared, Uninstaller));
                if (development)
                {
                    string dev = Path.Combine(prepared, "Development"); Directory.CreateDirectory(dev);
                    string tools = Path.Combine(dev, "toolchain"); Directory.CreateDirectory(tools);
                    if (store.Length > 0) ExtractStore(store, Object(Object(package["developmentToolchain"])["files"]), tools);
                    else using (var archive = ZipFile.OpenRead(toolchain)) foreach (var entry in archive.Entries)
                    {
                        string path = Child(tools, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(path)); entry.ExtractToFile(path);
                    }
                    Writable(dev, allUsers);
                }
                else if (Directory.Exists(Path.Combine(prepared, "Development"))) SafeDelete(Path.Combine(prepared, "Development"));
                string destination = Child(root, Folders[component]), previous = Child(root, ".previous-" + component + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff"));
                if (Directory.Exists(destination))
                {
                    bool retainedDevelopment = File.Exists(Path.Combine(destination, "suite-component.json")) &&
                        (string)Object(Json.DeserializeObject(File.ReadAllText(Path.Combine(destination, "suite-component.json"))))["component"] == component &&
                        Directory.GetFileSystemEntries(destination).All(value => Path.GetFileName(value) == "Development" || Path.GetFileName(value) == "suite-component.json");
                    if (!owned.ContainsKey(component) && !retainedDevelopment) throw new InvalidOperationException("Existing component folder is not owned by this installation.");
                    foreach (string path in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)) using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    MoveDirectory(destination, previous);
                }
                else previous = "";
                commits.Add(Tuple.Create(destination, previous)); MoveDirectory(prepared, destination); owned[component] = item;
                // An upgrade never overwrites the user's editable development copy.
                if (previous.Length > 0 && Directory.Exists(Path.Combine(previous, "Development")))
                {
                    if (Directory.Exists(Path.Combine(destination, "Development"))) MoveDirectory(Path.Combine(destination, "Development"), Child(stage, "fresh-development-" + component));
                    MoveDirectory(Path.Combine(previous, "Development"), Path.Combine(destination, "Development"));
                    Writable(Path.Combine(destination, "Development"), allUsers);
                }
            }
            Save(Path.Combine(root, "suite-install.json"), new { schema = 1, profileId = Profile(root), portable = false, version = Version, dataRoot = dataTemplate, componentData = paths, allUsers = allUsers });
            ClaimData(Path.Combine(Expand(dataTemplate), "shared"), root, "shared");
            foreach (string component in selected) ClaimData(Expand((string)paths[component]), root, component);
            foreach (string template in new[] { dataTemplate, searchChoice.Length == 0 ? dataTemplate : DataTemplate(searchChoice, "") }.Distinct())
            {
                string actual = Expand(template);
                Directory.CreateDirectory(actual); Writable(actual, allUsers);
                if (allUsers && template.Contains("{sid}")) Writable(Path.GetDirectoryName(actual), true);
            }
            Save(ownership, owned); File.Copy(Path.Combine(Base, Uninstaller), Path.Combine(root, Uninstaller), true);
        }
        catch
        {
            foreach (var commit in Enumerable.Reverse(commits))
            {
                if (commit.Item2.Length > 0 && Directory.Exists(Path.Combine(commit.Item1, "Development")) && !Directory.Exists(Path.Combine(commit.Item2, "Development")))
                    MoveDirectory(Path.Combine(commit.Item1, "Development"), Path.Combine(commit.Item2, "Development"));
                if (Directory.Exists(commit.Item1)) MoveDirectory(commit.Item1, Child(stage, "failed-" + Path.GetFileName(commit.Item1)));
                if (commit.Item2.Length > 0) MoveDirectory(commit.Item2, commit.Item1);
            }
            foreach (var file in metadata)
            {
                if (file.Value != null) File.WriteAllBytes(file.Key, file.Value);
                else if (File.Exists(file.Key)) File.Move(file.Key, Child(stage, "failed-" + Path.GetFileName(file.Key)));
            }
            throw;
        }
        // Registration failures leave a complete, owned installation that can be retried or removed.
        try { foreach (string component in selected) Register(root, component, desktop, startup.Contains(component)); }
        catch (Exception error) { throw new InvalidOperationException("Application files were installed. Windows shortcut or startup registration failed; rerun setup to complete it. " + error.Message, error); }
        SafeDelete(stage);
        progress("Installed. Settings and optional app data remain independent. Previous component folders are preserved for recovery.");
    }
    static void ExtractStore(string store, Dictionary<string, object> files, string target)
    {
        // Identical files are compressed once but installed into independent folders.
        using (var archive = ZipFile.OpenRead(store)) foreach (var file in files)
        {
            string digest = (string)file.Value;
            if (digest.Length != 64 || digest.Any(value => "0123456789abcdef".IndexOf(value) < 0)) throw new InvalidDataException("Invalid content digest.");
            var entry = archive.GetEntry(digest);
            if (entry == null) throw new InvalidDataException("Missing installer content: " + file.Key);
            string path = Child(target, file.Key); Directory.CreateDirectory(Path.GetDirectoryName(path));
            entry.ExtractToFile(path);
            if (Hash(path) != digest) throw new InvalidDataException("File checksum failed: " + file.Key);
        }
    }
    static void StopApps(string root, string[] selected)
    {
        foreach (string component in selected)
        {
            string executable = Path.Combine(root, Folders[component], Executables[component]);
            if (!File.Exists(executable)) continue;
            using (var process = Process.Start(new ProcessStartInfo(executable, "--quit") { UseShellExecute = false, CreateNoWindow = true })) if (!process.WaitForExit(15000)) throw new InvalidOperationException("Close " + Folders[component] + " before uninstalling.");
        }
        System.Threading.Thread.Sleep(1000);
    }
    static void EnsureUnlocked(string path)
    {
        // Virus scanners briefly open newly inspected runtime headers. Retry
        // sharing violations, but keep genuinely locked installations intact.
        for (int attempt = 0; ; attempt++)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { } return; }
            catch (IOException error)
            {
                int code = error.HResult & 0xffff;
                if (attempt >= 20 || (code != 32 && code != 33)) throw;
                System.Threading.Thread.Sleep(100);
            }
        }
    }
    static void Remove(string root, string[] selected, bool removeData, bool removeDevelopment, bool removeChanged, Action<string> progress)
    {
        root = Target(root); string manifestPath = Path.Combine(root, "suite-owned.json");
        if (!File.Exists(manifestPath) || !File.Exists(Path.Combine(root, "suite-install.json"))) throw new InvalidOperationException("No verified suite ownership metadata.");
        var owned = Object(Json.DeserializeObject(File.ReadAllText(manifestPath)));
        bool all = AllUsers(root); StopApps(root, selected);
        // Verify optional destructive scopes before deleting any application file.
        foreach (string component in selected.Where(owned.ContainsKey))
        {
            string metadata = Path.Combine(root, Folders[component], "suite-component.json");
            if (!File.Exists(metadata) || (string)Object(Json.DeserializeObject(File.ReadAllText(metadata)))["component"] != component) throw new InvalidOperationException("Application ownership does not match.");
            if (removeData && Directory.Exists(DataPath(root, component)))
            {
                string dataMarker = Path.Combine(DataPath(root, component), ".revamp-data.json");
                if (!File.Exists(dataMarker) || (string)Object(Json.DeserializeObject(File.ReadAllText(dataMarker)))["installRoot"] != root) throw new InvalidOperationException("Unmarked data was retained. No application files were removed.");
                VerifyTree(DataPath(root, component));
            }
            if (removeDevelopment && Directory.Exists(Path.Combine(root, Folders[component], "Development"))) VerifyTree(Path.Combine(root, Folders[component], "Development"));
            if (removeChanged) VerifyTree(Path.Combine(root, Folders[component]));
            if (removeChanged) foreach (string recovery in Directory.GetDirectories(root, ".previous-" + component + "-*"))
            {
                string recoveryMarker = Path.Combine(recovery, "suite-component.json");
                if (!File.Exists(recoveryMarker) || (string)Object(Json.DeserializeObject(File.ReadAllText(recoveryMarker)))["component"] != component)
                    throw new InvalidOperationException("Unmarked recovery folder was retained: " + recovery);
                VerifyTree(recovery);
            }
        }
        foreach (string component in selected)
        {
            if (!owned.ContainsKey(component)) continue;
            string app = Child(root, Folders[component]); var files = Object(Object(owned[component])["files"]);
            foreach (var file in files)
            {
                if (file.Key.StartsWith("Development/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!removeDevelopment && Directory.Exists(Path.Combine(app, "Development")) && file.Key == "suite-component.json") continue;
                string path = Child(app, file.Key);
                if (File.Exists(path)) EnsureUnlocked(path);
            }
        }
        foreach (string component in selected)
        {
            if (!owned.ContainsKey(component)) continue;
            progress("Removing " + Folders[component]); string app = Child(root, Folders[component]); var files = Object(Object(owned[component])["files"]);
            foreach (var file in files)
            {
                if (file.Key.StartsWith("Development/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!removeDevelopment && Directory.Exists(Path.Combine(app, "Development")) && file.Key == "suite-component.json") continue;
                string path = Child(app, file.Key);
                if (File.Exists(path) && (removeChanged || Hash(path) == (string)file.Value)) File.Delete(path);
            }
            if (removeData) RemoveData(root, component);
            if (removeDevelopment && Directory.Exists(Path.Combine(app, "Development"))) SafeDelete(Path.Combine(app, "Development"));
            string uninstall = Path.Combine(app, Uninstaller); if (File.Exists(uninstall)) File.Delete(uninstall);
            if (removeChanged) foreach (var entry in Directory.GetFileSystemEntries(app).Where(value => !Path.GetFileName(value).Equals("Development", StringComparison.OrdinalIgnoreCase) && !(Path.GetFileName(value) == "suite-component.json" && Directory.Exists(Path.Combine(app, "Development")))))
            { if (Directory.Exists(entry)) SafeDelete(entry); else File.Delete(entry); }
            foreach (var directory in Directory.GetDirectories(app, "*", SearchOption.AllDirectories).OrderByDescending(value => value.Length)) if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            if (!Directory.EnumerateFileSystemEntries(app).Any()) Directory.Delete(app);
            if (removeChanged) foreach (string recovery in Directory.GetDirectories(root, ".previous-" + component + "-*")) SafeDelete(recovery);
            string menu = Path.Combine(Environment.GetFolderPath(all ? Environment.SpecialFolder.CommonStartMenu : Environment.SpecialFolder.StartMenu), "Programs", "Xiaomi Revamp");
            foreach (string shortcut in new[] { Path.Combine(menu, Folders[component] + ".lnk"), Path.Combine(Environment.GetFolderPath(all ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), Folders[component] + ".lnk"), component == "file-search" ? Path.Combine(menu, "AI Center settings.lnk") : "", component == "file-search" ? Path.Combine(menu, "File Search.lnk") : "" }) if (shortcut.Length > 0) RemoveShortcut(shortcut, Path.Combine(app, Executables[component]));
            if (Directory.Exists(menu) && !Directory.EnumerateFileSystemEntries(menu).Any()) Directory.Delete(menu);
            (all ? Registry.LocalMachine : Registry.CurrentUser).DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiRevamp." + Profile(root) + "." + component, false);
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) key.DeleteValue("XiaomiRevampSuite." + (component == "file-search" ? "Search." : "Translator.") + Identity(root), false);
            if (component == "pc-manager")
            {
                string name = "XiaomiRevampSuite_" + Identity(root) + "_" + Sid;
                using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), "/Delete /TN " + Quote(name) + " /F") { UseShellExecute = false, CreateNoWindow = true })) process.WaitForExit();
            }
            owned.Remove(component);
        }
        Save(manifestPath, owned);
        if (owned.Count == 0)
        {
            if (removeData) RemoveData(root, "shared");
            if (File.Exists(Path.Combine(root, Uninstaller))) File.Delete(Path.Combine(root, Uninstaller));
            if (Directory.GetFileSystemEntries(root).All(value => Path.GetFileName(value) == "suite-owned.json" || Path.GetFileName(value) == "suite-install.json"))
                foreach (string name in new[] { "suite-owned.json", "suite-install.json" }) File.Delete(Path.Combine(root, name));
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        progress("Selected applications removed. Unselected apps and any retained development copies remain available.");
    }
    [STAThread]
    static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        Func<string, string, string> option = (name, fallback) => { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; };
        try
        {
            Application.EnableVisualStyles();
#if UNINSTALL
            bool remove = true;
            string defaultRoot = File.Exists(Path.Combine(Base, "suite-owned.json")) ? Base : Path.GetDirectoryName(Base.TrimEnd('\\'));
#else
            bool remove = false;
            string defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Xiaomi Revamp");
#endif
            string target = Target(option("--target", defaultRoot));
            if (remove && !args.Contains("--detached"))
            {
                string copy = Path.Combine(Path.GetTempPath(), "Xiaomi-Revamp-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe"); File.Copy(Application.ExecutablePath, copy);
                var forwarded = args.Concat(new[] { "--detached", "--target", target }).Select(Quote);
                Process.Start(new ProcessStartInfo(copy, string.Join(" ", forwarded)) { UseShellExecute = false });
                return 0;
            }
            if (args.Contains("--apply"))
            {
                if (option("--owner", Sid) != Sid) throw new InvalidOperationException("Approve elevation under the same Windows account.");
                string[] selected = option("--components", option("--component", "pc-manager")).Split(',');
                if (selected.Any(value => !Folders.ContainsKey(value))) throw new InvalidOperationException("Unknown application selected.");
                Action<string> report = value => { Console.WriteLine(value); if (args.Contains("--log")) File.AppendAllText(option("--log", ""), value + Environment.NewLine); };
                if (remove || option("--action", "install") == "remove") Remove(target, selected, args.Contains("--remove-data"), args.Contains("--remove-development"), args.Contains("--remove-changed"), report);
                else Install(target, selected, args.Contains("--desktop"), option("--startup", "").Split(','), option("--data-root", ""), option("--search-data", ""), args.Contains("--all-users"), args.Contains("--development"), report);
                return 0;
            }
            var form = new Form { Text = remove ? "Uninstall Xiaomi Revamp applications" : "Install Xiaomi Revamp", ClientSize = new Size(640, 650), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, Font = new Font("Segoe UI", 10) };
            form.Controls.Add(new Label { Text = remove ? "Select what to remove. Your retained apps and development copies remain independent." : "PC Manager with optional offline Screen Translator and AI Center.", Location = new Point(20, 16), Size = new Size(600, 44) });
            Func<string, int, string, bool, TextBox> pathRow = (title, y, value, enabled) =>
            {
                form.Controls.Add(new Label { Text = title, Location = new Point(20, y), Size = new Size(585, 23) });
                var box = new TextBox { Text = value, Location = new Point(20, y + 25), Width = 495, ReadOnly = !enabled }; form.Controls.Add(box);
                var browse = new Button { Text = "Browse...", Location = new Point(525, y + 23), Size = new Size(95, 30), Enabled = enabled }; form.Controls.Add(browse);
                browse.Click += (sender, eventArgs) => { using (var dialog = new FolderBrowserDialog { Description = title, ShowNewFolderButton = true }) if (dialog.ShowDialog(form) == DialogResult.OK) box.Text = dialog.SelectedPath; };
                return box;
            };
            var path = pathRow("Application folder", 65, target, !remove);
            var data = pathRow("Data folder (blank uses each user's Local AppData)", 133, "", !remove);
            var searchData = pathRow("AI Center data folder (blank uses the data folder above)", 201, "", !remove);
            var choices = new Dictionary<string, CheckBox>(); var starts = new Dictionary<string, CheckBox>(); int yRow = 276;
            // A folder that holds only some component archives, such as one app's Installer folder, offers only those.
            Func<string, bool> available = key => true;
            if (!remove && !File.Exists(Path.Combine(Base, "payload.zip")))
            {
                var packaged = Object(Object(Json.DeserializeObject(File.ReadAllText(Path.Combine(Base, "packages.json"))))["components"]);
                available = key => packaged.ContainsKey(key) && File.Exists(Child(Base, (string)Object(packaged[key])["payload"]));
            }
            foreach (var component in Folders)
            {
                bool enabled = true, selected = true;
#if SEARCH_SETUP
                enabled = false; selected = component.Key == "file-search";
#elif TRANSLATOR_SETUP
                enabled = false; selected = component.Key == "screen-translator";
#else
                if (!remove && component.Key == "pc-manager") enabled = false;
                if (!remove && component.Key != "pc-manager" && !available(component.Key)) { selected = false; enabled = false; }
                if (remove && args.Contains("--component")) { enabled = false; selected = option("--component", "") == component.Key; }
                if (remove && File.Exists(Path.Combine(target, "suite-owned.json")) && !Object(Json.DeserializeObject(File.ReadAllText(Path.Combine(target, "suite-owned.json")))).ContainsKey(component.Key)) { selected = false; enabled = false; }
#endif
                var check = new CheckBox { Text = component.Value + (!remove && component.Key == "pc-manager" && selected && !enabled ? " (required)" : ""), Checked = selected, Enabled = enabled, Location = new Point(20, yRow), Width = 295 }; choices[component.Key] = check; form.Controls.Add(check);
                var start = new CheckBox { Text = "Start for me with Windows", Checked = !remove && component.Key == "pc-manager", Enabled = !remove && selected, Location = new Point(340, yRow), Width = 285 }; starts[component.Key] = start; form.Controls.Add(start);
                check.CheckedChanged += (sender, eventArgs) => start.Enabled = !remove && check.Checked; yRow += 36;
            }
            var all = new CheckBox { Text = "Install for all users", Checked = remove && AllUsers(target), Enabled = !remove, Location = new Point(20, 388), Width = 265 }; form.Controls.Add(all);
            var desktop = new CheckBox { Text = "Desktop shortcuts", Checked = true, Enabled = !remove, Location = new Point(340, 388), Width = 265 }; form.Controls.Add(desktop);
            bool embeddedTools = !remove && File.Exists(Path.Combine(Base, "payload.zip"));
            var dev = new CheckBox { Text = remove ? "Remove development folders and their archives" : embeddedTools ? "Keep editable Development folders and included offline build tools" : "Keep editable Development folders (requires development-toolchain.zip)", Checked = !remove && (embeddedTools || File.Exists(Path.Combine(Base, "development-toolchain.zip"))), Location = new Point(20, 420), Width = 605 }; form.Controls.Add(dev);
            var removeData = new CheckBox { Text = "Remove my settings, search index, history and model caches", Visible = remove, Location = new Point(20, 452), Width = 605 }; form.Controls.Add(removeData);
            var changed = new CheckBox { Text = "Also remove changed and added files inside the selected app folders", Checked = true, Visible = remove, Location = new Point(20, 484), Width = 605 }; form.Controls.Add(changed);
            var status = new Label { Text = remove ? "Data removal affects this Windows account. Other users' data is retained." : "Choose dedicated data folders, including a subfolder of Program Files. Models stay with each app. User data is separated by Windows account.", Location = new Point(20, 526), Size = new Size(600, 60) }; form.Controls.Add(status);
            var button = new Button { Text = remove ? "Uninstall selected" : "Install selected", Location = new Point(415, 603), Size = new Size(205, 34) }; form.Controls.Add(button);
            button.Click += async (sender, eventArgs) =>
            {
                button.Enabled = false;
                try
                {
                    string root = Target(path.Text); var selected = choices.Where(item => item.Value.Checked).Select(item => item.Key).ToArray();
                    if (selected.Length == 0) throw new InvalidOperationException("Select at least one application.");
                    string startup = string.Join(",", starts.Where(item => item.Value.Checked && selected.Contains(item.Key)).Select(item => item.Key));
                    Action<string> progress = value => form.BeginInvoke((Action)(() => status.Text = value));
                    string arguments = "--apply --detached --owner " + Quote(Sid) + " --target " + Quote(root) + " --action " + (remove ? "remove" : "install") + " --components " + Quote(string.Join(",", selected)) + " --startup " + Quote(startup) + " --data-root " + Quote(data.Text) + " --search-data " + Quote(searchData.Text) + (desktop.Checked ? " --desktop" : "") + (all.Checked ? " --all-users" : "") + (!remove && dev.Checked ? " --development" : "") + (remove && dev.Checked ? " --remove-development" : "") + (removeData.Checked ? " --remove-data" : "") + (changed.Checked ? " --remove-changed" : "");
                    bool elevate = !Admin && (all.Checked || selected.Contains("pc-manager") || new[] { root, data.Text, searchData.Text }.Any(value => value.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\", StringComparison.OrdinalIgnoreCase)));
                    if (elevate)
                    {
                        status.Text = "Installing or removing selected applications...";
                        using (var process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })) { await Task.Run(() => process.WaitForExit()); if (process.ExitCode != 0) throw new InvalidOperationException("Operation failed. The detailed setup error was displayed."); }
                        status.Text = "Completed.";
                    }
                    else if (remove) await Task.Run(() => Remove(root, selected, removeData.Checked, dev.Checked, changed.Checked, progress));
                    else await Task.Run(() => Install(root, selected, desktop.Checked, startup.Split(','), data.Text, searchData.Text, all.Checked, dev.Checked, progress));
                }
                catch (Exception error) { status.Text = error.Message; MessageBox.Show(form, error.Message, "Xiaomi Revamp setup"); }
                finally { button.Enabled = true; }
            };
            Application.Run(form); return 0;
        }
        catch (Exception error)
        {
            if (args.Contains("--log")) File.AppendAllText(option("--log", ""), error.ToString() + Environment.NewLine);
            if (!args.Contains("--apply")) MessageBox.Show(error.Message, "Xiaomi Revamp setup");
            return 1;
        }
    }
}
