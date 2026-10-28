// Purpose: run a keyword's command line or a script file (.bat, .cmd, .ps1, .py, .sh) for the search bar.
// Hidden: no window, a notice says how it ended. Terminal: a window titled with the keyword that stays open.
// Dependencies: PowerShell 7 (pwsh), cmd, Git Bash and the py launcher, each only when a keyword or file uses it.
using System.Diagnostics;
using System.Text.RegularExpressions;

internal static class Runner
{
    internal static readonly string[] Runnable = {".bat", ".cmd", ".ps1", ".py", ".sh"};
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");
    static string Bash
    {
        get
        {
            string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            return File.Exists(bash) ? bash : throw new FileNotFoundException("Git Bash is not installed (expected " + bash + ").");
        }
    }
    static string Python => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe")) ? "py.exe" : "python.exe";
    // The keyword as a window title: letters, digits and . _ - only, so it cannot break the shell line it is put in.
    static string Title(string title) { string clean = Regex.Replace(title, "[^A-Za-z0-9._-]", ""); return clean.Length > 0 ? clean : "keyword"; }
    static string Single(string text) => "'" + text.Replace("'", "''") + "'";            // PowerShell literal
    static string Posix(string text) => "'" + text.Replace("'", "'\\''") + "'";          // bash literal

    /// <summary>A command line in PowerShell 7, cmd or Git Bash, started in the user's folder.</summary>
    internal static ProcessStartInfo Command(string shell, string text, bool terminal, string title)
    {
        title = Title(title);
        var start = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = Home };
        switch (shell)
        {
            case "pwsh":
                start.FileName = "pwsh.exe";
                foreach (string arg in terminal ? new[] { "-NoExit", "-Command", "$Host.UI.RawUI.WindowTitle=" + Single(title) + "; " + text } : new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", text }) start.ArgumentList.Add(arg);
                break;
            case "cmd":
                // /s: cmd removes only the outer quotes and runs the rest exactly as typed.
                start.FileName = Cmd;
                start.Arguments = terminal ? "/d /s /k \"title " + title + " & " + text + "\"" : "/d /s /c \"" + text + "\"";
                break;
            case "bash":
                start.FileName = Bash;
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(terminal ? "printf '\\033]0;%s\\007' " + Posix(title) + "; " + text + "\nexec bash -i" : text);
                break;
            default: throw new ArgumentException("Unknown shell " + shell);
        }
        return start;
    }

    /// <summary>A script file, run by the program its type needs, in the script's own folder.</summary>
    internal static ProcessStartInfo Script(string path, bool terminal, string title)
    {
        title = Title(title);
        string type = Path.GetExtension(path).ToLowerInvariant();
        if (!Runnable.Contains(type)) throw new ArgumentException(Path.GetFileName(path) + " is not a script that can be run (" + string.Join(", ", Runnable) + ").");
        if (!File.Exists(path)) throw new FileNotFoundException(path + " is not there.");
        var start = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        switch (type)
        {
            case ".bat": case ".cmd":
                start.FileName = Cmd;
                start.Arguments = terminal ? "/d /s /k \"title " + title + " & \"" + path + "\"\"" : "/d /s /c \"\"" + path + "\"\"";
                break;
            case ".py":
                if (terminal) { start.FileName = Cmd; start.Arguments = "/d /s /k \"title " + title + " & " + Python + " \"" + path + "\"\""; }
                else { start.FileName = Python; start.ArgumentList.Add("-u"); start.ArgumentList.Add(path); }
                break;
            case ".ps1":
                return Folder(Bypass(Command("pwsh", "& " + Single(path), terminal, title)), path);
            default: // .sh
                return Folder(Command("bash", "bash " + Posix(path.Replace('\\', '/')), terminal, title), path);
        }
        return start;
    }
    static ProcessStartInfo Folder(ProcessStartInfo start, string path) { start.WorkingDirectory = Path.GetDirectoryName(path)!; return start; }
    // A script file is not blocked by the machine's PowerShell script policy; a command line never was.
    static ProcessStartInfo Bypass(ProcessStartInfo start) { start.ArgumentList.Insert(0, "Bypass"); start.ArgumentList.Insert(0, "-ExecutionPolicy"); return start; }

    /// <summary>Starts it. Terminal: its own window, which stays open. Hidden: no window, no input; when it ends,
    /// <paramref name="notice"/> gets a title, the last line it wrote and whether it failed.</summary>
    internal static void Start(ProcessStartInfo start, bool terminal, string title, Action<string, string, bool> notice)
    {
        if (terminal) { start.CreateNoWindow = false; Process.Start(start)?.Dispose(); return; }
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start " + start.FileName + ".");
        string output = "", error = "";
        process.OutputDataReceived += (_, line) => { if (!string.IsNullOrWhiteSpace(line.Data)) output = line.Data.Trim(); };
        process.ErrorDataReceived += (_, line) => { if (!string.IsNullOrWhiteSpace(line.Data)) error = line.Data.Trim(); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        process.StandardInput.Close(); // a command that asks for input gets nothing instead of waiting unseen
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            int code = process.ExitCode; process.Dispose();
            if (code == 0) notice(title + " done", output.Length > 0 ? output : "Finished without output.", false);
            else notice(title + " failed (exit code " + code + ")", error.Length > 0 ? error : output.Length > 0 ? output : "No message.", true);
        });
    }
}
