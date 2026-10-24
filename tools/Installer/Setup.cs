using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinMuseSetup;

/// <summary>Installs and removes the app for the current user (no administrator rights): the same registration
/// tools\Build-App.ps1 does when it installs (Start menu, optional desktop shortcut, Settings &gt; Apps, "Open with").</summary>
internal sealed class Setup
{
    public static readonly string[] AudioTypes =
        { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".wma", ".ape", ".wv", ".dsf", ".dff", ".mka" };
    private const string OpenWithKey = @"Software\Classes\Applications\WinMuse.exe";

    public string Name { get; }
    public string RegKey { get; }
    public string Version { get; }
    public string Arch { get; }

    public Setup(Dictionary<string, string> ini)
    {
        Name = ini.TryGetValue("Name", out var n) && n.Length > 0 ? n : "Music";
        RegKey = ini.TryGetValue("RegKey", out var k) && k.Length > 0 ? k : "WinMuse-" + new string(Name.Where(char.IsLetterOrDigit).ToArray());
        Version = ini.TryGetValue("Version", out var v) ? v : "1.0.0";
        Arch = ini.TryGetValue("Arch", out var a) ? a : "x64";
    }

    public static string DefaultDir(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", name);

    public static string StartMenuLink(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name + ".lnk");
    public static string DesktopLink(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk");

    /// <summary>The installer's zip says which CPU it was built for; an ARM64 build can't run on an Intel/AMD PC.</summary>
    public string ArchProblem()
    {
        if (!Arch.Equals("ARM64", StringComparison.OrdinalIgnoreCase)) return null;
        var machine = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
        return string.Equals(machine, "ARM64", StringComparison.OrdinalIgnoreCase) ? null
            : $"This installer is for ARM PCs (Snapdragon and similar). Ask for the x64 version of {Name}.";
    }

    /// <summary>Copies of the app running from <paramref name="dir"/>.</summary>
    public static List<Process> RunningFrom(string dir)
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName("WinMuse"))
        {
            try { if (p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) list.Add(p); }
            catch { } // another user's process or access denied
        }
        return list;
    }

    // ---------------- Install ----------------

    public void Install(string setupExe, (long Offset, long Length) payload, string dir, bool desktop, bool openWith, Action<double, string> progress)
    {
        Directory.CreateDirectory(dir);
        using (var zip = new ZipArchive(new SubStream(File.OpenRead(setupExe), payload.Offset, payload.Length), ZipArchiveMode.Read))
        {
            var total = Math.Max(1, zip.Entries.Sum(e => e.Length));
            long done = 0;
            var root = Path.GetFullPath(dir + Path.DirectorySeparatorChar);
            foreach (var e in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never outside the install folder
                if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                e.ExtractToFile(target, true);
                done += e.Length;
                progress(0.92 * done / total, "Copying files…");
            }
        }

        // The uninstaller is this exe without the app on its end.
        progress(0.94, "Adding the uninstaller…");
        var uninstaller = Path.Combine(dir, "Uninstall.exe");
        using (var src = File.OpenRead(setupExe))
        using (var dst = File.Create(uninstaller))
        {
            var buf = new byte[81920];
            long left = payload.Offset;
            while (left > 0)
            {
                var n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) break;
                dst.Write(buf, 0, n);
                left -= n;
            }
        }

        progress(0.97, "Adding shortcuts…");
        var exe = Path.Combine(dir, "WinMuse.exe");
        CreateShortcut(StartMenuLink(Name), exe, Name);
        if (desktop) CreateShortcut(DesktopLink(Name), exe, Name);
        if (openWith) RegisterOpenWith(exe);
        RegisterUninstall(dir, exe, uninstaller);
        progress(1, "Done");
    }

    private void RegisterOpenWith(string exe)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(OpenWithKey))
            k.SetValue("FriendlyAppName", Name);
        using (var k = Registry.CurrentUser.CreateSubKey(OpenWithKey + @"\shell\open\command"))
            k.SetValue("", $"\"{exe}\" \"%1\"");
        using (var k = Registry.CurrentUser.CreateSubKey(OpenWithKey + @"\SupportedTypes"))
            foreach (var t in AudioTypes) k.SetValue(t, "");
        foreach (var t in AudioTypes)
            Registry.CurrentUser.CreateSubKey($@"Software\Classes\{t}\OpenWithList\WinMuse.exe")?.Dispose();
    }

    private void RegisterUninstall(string dir, string exe, string uninstaller)
    {
        long size = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) size += f.Length;
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + RegKey);
        k.SetValue("DisplayName", Name);
        k.SetValue("DisplayIcon", exe + ",0");
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", "WinMuse");
        k.SetValue("InstallLocation", dir);
        k.SetValue("UninstallString", $"\"{uninstaller}\"");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
    }

    private static void CreateShortcut(string lnk, string target, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        var shell = Activator.CreateInstance(type);
        try
        {
            var s = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var st = s.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, s, new object[] { target });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, s, new object[] { Path.GetDirectoryName(target) });
            st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, s, new object[] { target + ",0" });
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, s, new object[] { description });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, s, null);
            Marshal.FinalReleaseComObject(s);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    // ---------------- Uninstall ----------------

    /// <summary>Removes the app from <paramref name="dir"/>. Shortcuts and "Open with" are removed only when they
    /// point into this folder, so another copy of the app keeps its own. The library and settings are kept.</summary>
    public void Uninstall(string dir)
    {
        foreach (var p in RunningFrom(dir)) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
        var exe = Path.Combine(dir, "WinMuse.exe");

        foreach (var lnk in new[] { StartMenuLink(Name), DesktopLink(Name) })
            if (File.Exists(lnk)) try { File.Delete(lnk); } catch { }

        string command;
        using (var k = Registry.CurrentUser.OpenSubKey(OpenWithKey + @"\shell\open\command"))
            command = k?.GetValue("") as string;
        if (command != null && command.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Registry.CurrentUser.DeleteSubKeyTree(OpenWithKey, false);
            foreach (var t in AudioTypes) Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{t}\OpenWithList\WinMuse.exe", false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + RegKey, false);

        for (int attempt = 0; attempt < 5 && Directory.Exists(dir); attempt++)
        {
            try { Directory.Delete(dir, true); }
            catch { System.Threading.Thread.Sleep(600); }
        }
    }
}
