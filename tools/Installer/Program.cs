using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace WinMuseSetup;

/// <summary>
/// Setup.exe (with the app on its end): shows the install window.  /silent [/dir=PATH] [/desktop] [/noopenwith]
/// installs without it.
/// Uninstall.exe (the same program without the app, next to setup.ini in the install folder): asks, then removes.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var exe = Application.ExecutablePath;
        bool silent = args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
        string dirArg = args.FirstOrDefault(a => a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))?.Substring(5);
        bool Flag(string f) => args.Any(a => a.Equals(f, StringComparison.OrdinalIgnoreCase));

        try
        {
            var removeAt = Array.FindIndex(args, a => a.Equals("/remove", StringComparison.OrdinalIgnoreCase));
            if (removeAt >= 0 && removeAt + 1 < args.Length) return Remove(args[removeAt + 1], silent);

            var payload = Payload.Find(exe);
            if (payload is { } p) return Install(exe, p, dirArg, silent, Flag("/desktop"), !Flag("/noopenwith"));

            // Uninstall.exe in the install folder: run a copy from TEMP so the folder can be deleted.
            var dir = Path.GetDirectoryName(exe);
            if (!File.Exists(Path.Combine(dir, "setup.ini")))
            {
                Fail("This installer is incomplete (the app is missing from it).", silent);
                return 2;
            }
            var setup = new Setup(Payload.ParseIni(File.ReadAllText(Path.Combine(dir, "setup.ini"))));
            if (!silent && MessageBox.Show($"Remove {setup.Name}?\n\nYour library, playlists and settings are kept, and your music files are not touched.",
                    $"Uninstall {setup.Name}", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
            var temp = Path.Combine(Path.GetTempPath(), "WinMuse-Uninstall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var copy = Path.Combine(temp, "Uninstall.exe");
            File.Copy(exe, copy);
            Process.Start(new ProcessStartInfo(copy, $"/remove \"{dir}\"" + (silent ? " /silent" : "")) { UseShellExecute = false });
            return 0;
        }
        catch (Exception ex)
        {
            Fail(ex.Message, silent);
            return 3;
        }
    }

    private static int Install(string exe, (long Offset, long Length) payload, string dir, bool silent, bool desktop, bool openWith)
    {
        Setup setup;
        byte[] icon = null;
        long size;
        using (var zip = new ZipArchive(new SubStream(File.OpenRead(exe), payload.Offset, payload.Length), ZipArchiveMode.Read))
        {
            var ini = zip.GetEntry("setup.ini") ?? throw new InvalidDataException("setup.ini is missing from the installer.");
            using (var r = new StreamReader(ini.Open())) setup = new Setup(Payload.ParseIni(r.ReadToEnd()));
            var png = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("Assets/AppIcon.png", StringComparison.OrdinalIgnoreCase));
            if (png != null) using (var s = png.Open()) using (var m = new MemoryStream()) { s.CopyTo(m); icon = m.ToArray(); }
            size = zip.Entries.Sum(e => e.Length);
        }
        if (setup.ArchProblem() is { } problem) { Fail(problem, silent); return 4; }

        if (silent)
        {
            var target = dir ?? Setup.DefaultDir(setup.Name);
            foreach (var p in Setup.RunningFrom(target)) { p.Kill(); p.WaitForExit(5000); }
            setup.Install(exe, payload, target, desktop, openWith, (_, _) => { });
            return 0;
        }
        Application.Run(new SetupForm(setup, exe, payload, icon, size, dir));
        return 0;
    }

    private static int Remove(string dir, bool silent)
    {
        var ini = Path.Combine(dir, "setup.ini");
        var setup = new Setup(File.Exists(ini) ? Payload.ParseIni(File.ReadAllText(ini)) : new());
        setup.Uninstall(dir);
        if (!silent) MessageBox.Show($"{setup.Name} was removed.", $"Uninstall {setup.Name}", MessageBoxButtons.OK, MessageBoxIcon.Information);
        // This copy lives in TEMP; delete it (and its folder) once it has exited.
        var self = Path.GetDirectoryName(Application.ExecutablePath);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rd /s /q \"{self}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        return 0;
    }

    private static void Fail(string message, bool silent)
    {
        if (silent) Console.Error.WriteLine(message);
        else MessageBox.Show(message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
