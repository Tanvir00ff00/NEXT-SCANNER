// =============================================================================
// NextScan Studio - what the installer actually does
// Plan ref: MASTER_PLAN section 19.
//
// Kept apart from the window on purpose. Everything here is about files,
// registry keys and shortcuts, and none of it needs to know that a window
// exists; it reports progress through a delegate and is driven from a worker
// thread. The window is then free to be entirely about looking right.
//
// The payload travels inside this executable as a compressed resource, so the
// installer is one file with nothing beside it. What goes into that resource is
// decided by build_installer.ps1, which refuses to build without the model
// files -- their absence does not break anything, it just quietly makes every
// installation detect worse.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace NextScan.Setup
{
    public static class SetupWork
    {
        public const string Product = "NextScan Studio";
        public const string Publisher = "NextScan";
        public const string ExeName = "NextScanner.exe";
        public const string SetupName = "NextScanSetup.exe";
        public const string Connector = "NextScanner.8ba";

        const string RegKey = @"Software\NextScan";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NextScanStudio";
        const string PayloadName = "NextScan.Payload.zip";

        /// <summary>Reported as (what is happening, 0..1). Called off the UI thread.</summary>
        public delegate void Progress(string what, double done);

        // ---- where things go ---------------------------------------------------

        public static string DefaultDirectory
        {
            get
            {
                string programs = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(programs))
                    programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(programs, Product);
            }
        }

        public static string Version
        {
            get
            {
                try
                {
                    Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v.Major + "." + v.Minor + "." + v.Build;
                }
                catch { return "1.0.0"; }
            }
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// .NET 4.8 is 528040. The application is built against it and there is
        /// nothing useful it can do without it, so this is asked before a single
        /// file is written rather than discovered as a crash on first run.
        /// </summary>
        public static bool HasNet48()
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                                  .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k == null) return false;
                    object release = k.GetValue("Release");
                    return release is int && (int)release >= 528040;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Every Photoshop on this machine that has a Plug-ins folder. Several
        /// versions can be installed at once and each keeps its own, so this is
        /// a list rather than an answer.
        /// </summary>
        public static List<string> PhotoshopPluginFolders()
        {
            List<string> found = new List<string>();
            string[] roots =
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string adobe = Path.Combine(root, "Adobe");
                if (!Directory.Exists(adobe)) continue;

                try
                {
                    foreach (string dir in Directory.GetDirectories(adobe, "Adobe Photoshop*"))
                    {
                        string plugins = Path.Combine(dir, "Plug-ins");
                        if (Directory.Exists(plugins) && !found.Contains(plugins)) found.Add(plugins);
                    }
                }
                catch { }   // an unreadable Adobe folder is one Photoshop we skip
            }
            return found;
        }

        // ---- the payload -------------------------------------------------------

        static Stream OpenPayload()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName);
            if (s == null) throw new InvalidOperationException(
                "This installer was built without its payload. Rebuild it with build_installer.ps1.");
            return s;
        }

        public static long PayloadBytes()
        {
            try
            {
                using (Stream s = OpenPayload())
                using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    long total = 0;
                    foreach (ZipArchiveEntry e in zip.Entries) total += e.Length;
                    return total;
                }
            }
            catch { return 0; }
        }

        // ---- install -----------------------------------------------------------

        public static void Install(string directory, bool withConnector, bool desktopShortcut, Progress say)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("no directory");
            Directory.CreateDirectory(directory);

            say("Reading the payload", 0.01);

            using (Stream s = OpenPayload())
            using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                long total = 0;
                foreach (ZipArchiveEntry e in zip.Entries) total += e.Length;
                if (total <= 0) total = 1;

                long done = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.Length == 0 && entry.Name.Length == 0) continue;   // a directory

                    string target = Path.Combine(directory, entry.FullName.Replace('/', '\\'));
                    string parent = Path.GetDirectoryName(target);

                    // A zip entry naming its way out of the directory it is being
                    // written into is not a thing that happens by accident.
                    if (!IsInside(directory, target))
                        throw new InvalidDataException("payload entry outside the install folder: " + entry.FullName);

                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    say(entry.Name, 0.02 + 0.88 * done / (double)total);
                    entry.ExtractToFile(target, true);
                    done += entry.Length;
                }
            }

            say("Recording where it lives", 0.92);
            string exe = Path.Combine(directory, ExeName);

            // The Photoshop acquire module reads this when nobody has started the
            // application yet, which is exactly what happens when someone installs
            // and goes straight to Photoshop's Import menu. The application keeps
            // its own per-user copy current from then on.
            using (RegistryKey k = Root().CreateSubKey(RegKey))
            {
                k.SetValue("AppPath", exe, RegistryValueKind.String);
                k.SetValue("InstallDir", directory, RegistryValueKind.String);
                k.SetValue("Version", Version, RegistryValueKind.String);
            }

            // The installer becomes the uninstaller: one program, two jobs, and
            // no second binary to keep in step with this one.
            string setupCopy = Path.Combine(directory, SetupName);
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                if (!string.Equals(self, setupCopy, StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, setupCopy, true);
            }
            catch { }

            using (RegistryKey k = Root().CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Product);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", Publisher);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", directory);
                k.SetValue("UninstallString", "\"" + setupCopy + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + setupCopy + "\" --uninstall --silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirectorySize(directory) / 1024), RegistryValueKind.DWord);
            }

            say("Making shortcuts", 0.95);
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Product);
            Directory.CreateDirectory(menu);
            MakeShortcut(Path.Combine(menu, Product + ".lnk"), exe, directory, Product);
            MakeShortcut(Path.Combine(menu, "Uninstall " + Product + ".lnk"), setupCopy, directory,
                         "Remove " + Product, "--uninstall");

            if (desktopShortcut)
                MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                                          Product + ".lnk"), exe, directory, Product);

            if (withConnector)
            {
                say("Connecting Photoshop", 0.98);
                string source = Path.Combine(directory, Connector);
                if (File.Exists(source))
                    foreach (string plugins in PhotoshopPluginFolders())
                        try { File.Copy(source, Path.Combine(plugins, Connector), true); }
                        catch { }   // a Photoshop we cannot write to is one we skip, not a failed install
            }

            say("Done", 1.0);
        }

        // ---- uninstall ---------------------------------------------------------

        public static void Uninstall(Progress say)
        {
            say("Finding the installation", 0.05);

            string directory = null;
            try
            {
                using (RegistryKey k = Root().OpenSubKey(RegKey))
                    if (k != null) directory = k.GetValue("InstallDir") as string;
            }
            catch { }

            say("Removing the Photoshop connector", 0.2);
            foreach (string plugins in PhotoshopPluginFolders())
                try { File.Delete(Path.Combine(plugins, Connector)); } catch { }

            say("Removing shortcuts", 0.35);
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Product);
            TryDeleteTree(menu);
            try
            {
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                                         Product + ".lnk"));
            }
            catch { }

            say("Removing the program", 0.55);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                // Scans, settings and diagnostics live elsewhere, under the
                // operator's own profile, and are left alone. An uninstaller that
                // takes someone's scans with it is a worse fault than one that
                // leaves a folder behind.
                TryDeleteTree(directory);
            }

            say("Clearing the registry", 0.85);
            try { Root().DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Root().DeleteSubKeyTree(RegKey, false); } catch { }

            say("Done", 1.0);
        }

        /// <summary>
        /// Re-launches from the temporary folder so the installation folder can
        /// be removed whole. A running program cannot delete the file it is
        /// running from, and leaving one behind means leaving the folder too.
        /// </summary>
        public static bool RelaunchFromTemp(string[] args)
        {
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                string temp = Path.Combine(Path.GetTempPath(),
                                           "NextScanSetup_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(self, temp, true);

                string pass = "--uninstall --relaunched";
                foreach (string a in args)
                    if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)) pass += " --silent";

                Process.Start(new ProcessStartInfo(temp, pass) { UseShellExecute = false });
                return true;
            }
            catch { return false; }
        }

        // ---- small things ------------------------------------------------------

        static RegistryKey Root()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }

        static bool IsInside(string directory, string candidate)
        {
            string a = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            string b = Path.GetFullPath(candidate);
            return b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        static long DirectorySize(string directory)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return total;
        }

        static void TryDeleteTree(string directory)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch { }
        }

        /// <summary>
        /// Written through the shell's own scripting object rather than by
        /// composing a .lnk by hand. Late bound, so nothing has to be referenced
        /// and there is no interop assembly to ship.
        /// </summary>
        static void MakeShortcut(string linkPath, string target, string workingDir, string description,
                                 string arguments = "")
        {
            try
            {
                Type shell = Type.GetTypeFromProgID("WScript.Shell");
                if (shell == null) return;

                object wsh = Activator.CreateInstance(shell);
                object link = shell.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, wsh,
                                                 new object[] { linkPath });
                Type t = link.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workingDir });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
                if (!string.IsNullOrEmpty(arguments))
                    t.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { arguments });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch { }   // a missing shortcut is a blemish, not a failed installation
        }
    }
}
