// Purpose: choose PC Manager install scope and folder, then run the embedded Windows installer.
// Dependencies: Windows .NET Framework 4.x, Windows PowerShell, embedded payload.zip and Install.ps1.
// Outputs: extracted installer cache and a PC Manager installation in the selected directory.
// Command: PCManager-Setup.exe [--extract C:\path] [--quiet --scope AllUsers|CurrentUser --destination C:\path].
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class SetupBootstrap
{
    private static string DefaultPath(bool allUsers)
    {
        return Path.Combine(Environment.GetFolderPath(allUsers
            ? Environment.SpecialFolder.ProgramFiles
            : Environment.SpecialFolder.LocalApplicationData),
            allUsers ? Path.Combine("Xiaomi Revamp", "PC Manager") : Path.Combine("Programs", "Xiaomi Revamp", "PC Manager"));
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--extract")
            {
                Extract(Path.GetFullPath(args[1]));
                return 0;
            }
            if (args.Length > 0)
            {
                if (args.Length != 5 || args[0] != "--quiet" || args[1] != "--scope" ||
                    args[3] != "--destination" || (args[2] != "AllUsers" && args[2] != "CurrentUser"))
                    throw new ArgumentException("Use --quiet --scope AllUsers|CurrentUser --destination C:\\path.");
                return Install(args[2], args[4]);
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new Form())
            {
                form.Text = "PC Manager Setup";
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.ClientSize = new Size(610, 322);
                var heading = new Label { Text = "Install PC Manager", Font = new Font("Segoe UI", 17F), AutoSize = true, Location = new Point(20, 17) };
                var all = new RadioButton { Text = "Install for all users (Program Files)", Checked = true, AutoSize = true, Location = new Point(24, 72) };
                var one = new RadioButton { Text = "Install for this user only", AutoSize = true, Location = new Point(24, 104) };
                var detail = new Label { Text = "A shared install adds a Start menu entry for everyone. Startup is enabled for this account; other accounts can enable it in Settings.", Location = new Point(43, 135), Size = new Size(530, 40) };
                var location = new Label { Text = "Installation folder", AutoSize = true, Location = new Point(24, 188) };
                var path = new TextBox { Text = DefaultPath(true), Location = new Point(24, 211), Width = 466 };
                var browse = new Button { Text = "Browse...", Location = new Point(500, 210), Size = new Size(88, 25) };
                var status = new Label { Text = "Your settings remain in your Windows profile.", Location = new Point(24, 251), Size = new Size(465, 36) };
                var install = new Button { Text = "Install", Location = new Point(403, 285), Size = new Size(88, 27) };
                var close = new Button { Text = "Cancel", Location = new Point(500, 285), Size = new Size(88, 27) };
                all.CheckedChanged += delegate { if (all.Checked && path.Text == DefaultPath(false)) path.Text = DefaultPath(true); };
                one.CheckedChanged += delegate { if (one.Checked && path.Text == DefaultPath(true)) path.Text = DefaultPath(false); };
                browse.Click += delegate
                {
                    using (var picker = new FolderBrowserDialog { Description = "Choose a dedicated PC Manager folder", SelectedPath = path.Text })
                        if (picker.ShowDialog(form) == DialogResult.OK) path.Text = picker.SelectedPath;
                };
                close.Click += delegate { form.Close(); };
                install.Click += delegate
                {
                    if (String.IsNullOrWhiteSpace(path.Text) || path.Text.IndexOf('"') >= 0)
                    {
                        MessageBox.Show(form, "Choose a valid installation folder.", form.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    string selectedScope = all.Checked ? "AllUsers" : "CurrentUser";
                    string selectedPath = path.Text.Trim();
                    install.Enabled = all.Enabled = one.Enabled = browse.Enabled = path.Enabled = false;
                    status.Text = "Installing. Approve the Windows administrator prompt if it appears...";
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        int result = 1;
                        string error = null;
                        try { result = Install(selectedScope, selectedPath); }
                        catch (Exception exception) { error = exception.Message; }
                        if (form.IsDisposed) return;
                        form.BeginInvoke((Action)delegate
                        {
                            if (result == 0) { status.Text = "Installed at " + selectedPath; close.Text = "Finish"; }
                            else
                            {
                                status.Text = error ?? "Setup did not finish. Extract the installer to inspect Install.ps1.";
                                install.Enabled = all.Enabled = one.Enabled = browse.Enabled = path.Enabled = true;
                            }
                        });
                    });
                };
                form.Controls.AddRange(new Control[] { heading, all, one, detail, location, path, browse, status, install, close });
                Application.Run(form);
            }
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length == 0 || args[0] != "--quiet")
                MessageBox.Show(error.Message, "PC Manager Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Install(string scope, string destination)
    {
        if (destination.IndexOf('"') >= 0) throw new ArgumentException("Installation folder contains quotation marks.");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiaomiAIManager", "installer-cache", DateTime.Now.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Extract(root);
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell, "-NoProfile -ExecutionPolicy Bypass -File \"" +
            Path.Combine(root, "Install.ps1") + "\" -Scope " + scope + " -Destination \"" + destination + "\"");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        using (Process process = Process.Start(start))
        {
            if (process == null) throw new InvalidOperationException("Windows PowerShell could not start.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string result = Path.Combine(root, "install-result.txt");
                if (File.Exists(result)) throw new InvalidOperationException(File.ReadAllText(result));
            }
            return process.ExitCode;
        }
    }

    private static void Extract(string root)
    {
        Directory.CreateDirectory(root);
        foreach (string name in new[] { "payload.zip", "Install.ps1" })
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (input == null) throw new FileNotFoundException("The setup payload is missing: " + name);
                using (Stream output = File.Create(Path.Combine(root, name))) input.CopyTo(output);
            }
    }
}
