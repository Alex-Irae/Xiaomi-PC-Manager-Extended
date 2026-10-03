// Purpose: one offline EXE containing selectable apps, models, runtimes and development tools.
// Dependencies: Windows .NET Framework 4.8. Outputs: verified temporary setup files and selected installation.
// Build: tools/build_inclusive_installer.py. Run: Xiaomi-Revamp-Installer.exe [setup arguments].
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Xiaomi Revamp Installer")]
[assembly: System.Reflection.AssemblyVersion("0.2.0.0")]
internal static class InstallerBootstrap
{
    const string Magic = "XRSETUP1";
    static string Quote(string text)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char value in text)
        {
            if (value == '\\') { slashes++; continue; }
            result.Append('\\', value == '"' ? slashes * 2 + 1 : slashes).Append(value); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    static string Child(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Unsafe installer entry.");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe installer entry.");
        return path;
    }
    static void Extract(string target, bool verifyOnly, Action<int> progress)
    {
        using (var file = File.OpenRead(Application.ExecutablePath))
        {
            if (file.Length < 56) throw new InvalidDataException("Incomplete installer.");
            file.Position = file.Length - 56;
            var footer = new BinaryReader(file);
            long offset = footer.ReadInt64(), length = footer.ReadInt64();
            byte[] expected = footer.ReadBytes(32);
            if (Encoding.ASCII.GetString(footer.ReadBytes(8)) != Magic || offset < 1 || length < 1 || offset != file.Length - 56 - length) throw new InvalidDataException("Invalid installer payload.");
            using (var slice = new PayloadStream(file, offset, length))
            {
                using (var sha = SHA256.Create()) if (!sha.ComputeHash(slice).SequenceEqual(expected)) throw new InvalidDataException("Installer checksum failed. Download it again.");
                slice.Position = 0;
                using (var archive = new ZipArchive(slice, ZipArchiveMode.Read, true))
                {
                    long total = archive.Entries.Sum(entry => entry.Length), done = 0;
                    byte[] buffer = new byte[1024 * 1024];
                    foreach (var entry in archive.Entries)
                    {
                        string path = Child(target, entry.FullName);
                        if (!verifyOnly) Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var input = entry.Open())
                        using (var output = verifyOnly ? Stream.Null : File.Create(path))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                            { output.Write(buffer, 0, read); done += read; progress((int)(done * 100 / Math.Max(1, total))); }
                        }
                    }
                }
            }
        }
    }
    static void RemoveTemporary(string target)
    {
        // Only the unique folder created by this invocation reaches this method.
        foreach (string path in Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Temporary link retained.");
        Directory.Delete(target, true);
    }
    [STAThread]
    static int Main(string[] args)
    {
        bool verify = args.Contains("--verify-only"), extract = args.Contains("--extract-only");
        int index = Array.IndexOf(args, "--extract-only");
        string target = extract && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Path.GetTempPath(), "XiaomiRevamp-Setup-" + Guid.NewGuid().ToString("N"));
        bool temporary = !extract;
        try
        {
            if (verify) { Extract(target, true, _ => { }); return 0; }
            if (extract && index + 1 >= args.Length) throw new ArgumentException("Supply an extraction directory.");
            if (Directory.Exists(target)) throw new IOException("Choose a new extraction folder.");
            Directory.CreateDirectory(target);
            if (extract) { Extract(target, false, _ => { }); return 0; }
            Application.EnableVisualStyles();
            using (var form = new Form { Text = "Xiaomi Revamp", ClientSize = new Size(450, 110), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ControlBox = false })
            {
                var label = new Label { Text = "Preparing offline installation...", Location = new Point(20, 18), Size = new Size(410, 25) };
                var bar = new ProgressBar { Location = new Point(20, 55), Size = new Size(410, 23) };
                form.Controls.Add(label); form.Controls.Add(bar);
                Exception failure = null;
                form.Shown += async (_, e) =>
                {
                    try
                    {
                        int shown = -1;
                        await Task.Run(() => Extract(target, false, value => { if (value != shown) { shown = value; form.BeginInvoke((Action)(() => bar.Value = value)); } }));
                    }
                    catch (Exception error) { failure = error; }
                    finally { form.Close(); }
                };
                Application.Run(form); if (failure != null) throw failure;
            }
            using (var process = Process.Start(new ProcessStartInfo(Path.Combine(target, "Xiaomi-Revamp-Setup.exe"), string.Join(" ", args.Select(Quote))) { UseShellExecute = false, WorkingDirectory = target, CreateNoWindow = true }))
            { process.WaitForExit(); return process.ExitCode; }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (!verify && !extract && !args.Contains("--apply")) MessageBox.Show(error.Message, "Xiaomi Revamp installer");
            return 1;
        }
        finally { if (temporary && Directory.Exists(target)) try { RemoveTemporary(target); } catch (IOException) { } }
    }
    sealed class PayloadStream : Stream
    {
        readonly Stream file; readonly long offset, length; long position;
        internal PayloadStream(Stream file, long offset, long length) { this.file = file; this.offset = offset; this.length = length; }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position { get { return position; } set { Seek(value, SeekOrigin.Begin); } }
        public override int Read(byte[] buffer, int start, int count) { file.Position = offset + position; int read = file.Read(buffer, start, (int)Math.Min(count, length - position)); position += read; return read; }
        public override long Seek(long value, SeekOrigin origin) { long next = origin == SeekOrigin.Begin ? value : origin == SeekOrigin.Current ? position + value : length + value; if (next < 0 || next > length) throw new IOException("Invalid payload offset."); return position = next; }
        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
