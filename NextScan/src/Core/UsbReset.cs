// =============================================================================
// NextScan Studio - USB device power-cycle recovery (auto "unplug/replug")
//
// Field problem (2026-08-23, the user's desk): the CanoScan LiDE 400
// occasionally wedges - the carriage stops at the bottom of the glass and
// Canon ScanGear itself reports "Detach the USB cable and reconnect.
// Code:2,250,4". The only remedy Canon offers is physically re-plugging the
// cable, sometimes several times.
//
// The software equivalent of re-plugging is restarting the USB device node:
// pnputil /restart-device <instance>. That requires elevation, and the app
// must never demand admin rights per scan. So the reset runs through a
// one-time-registered scheduled task (NextScan_UsbReset, RunLevel Highest):
// registering it needs one consent, running it later needs none.
//
// The reset targets the PARENT composite node (USB\VID_xxxx&PID_yyyy\serial)
// rather than the MI_ interfaces: restarting the parent re-enumerates the
// whole device, which is what a physical re-plug does.
// =============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;

namespace NextScan.Core
{
    public static class UsbReset
    {
        public const string TaskName = "NextScan_UsbReset";
        public static Action<string> Log = delegate { };

        /// <summary>
        /// Path of the file the scheduled helper reads the target from. Must
        /// match tools\usb_reset.cmd, which reads %~dp0..\tmp - i.e. the tmp\
        /// beside the tools\ directory, NOT a tmp\ beside the engine DLL
        /// (nsprobe, scanhelper and the tests all load the engine from
        /// bin\, so anchoring on the assembly would scatter target files).
        /// </summary>
        public static string TargetFile
        {
            get
            {
                string dir = Path.Combine(ToolDirectory(), "..\\tmp");
                return Path.GetFullPath(Path.Combine(dir, "usb_reset_target.txt"));
            }
        }

        class PnpDevice
        {
            public string Name;
            public string InstanceId;
            public string Status;
        }

        static PnpDevice[] Enumerate()
        {
            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, Status FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");
                System.Collections.Generic.List<PnpDevice> list = new System.Collections.Generic.List<PnpDevice>();
                foreach (ManagementObject mo in searcher.Get())
                {
                    PnpDevice d = new PnpDevice();
                    d.Name = mo["Name"] as string ?? "";
                    d.InstanceId = mo["DeviceID"] as string ?? "";
                    d.Status = mo["Status"] as string ?? "";
                    list.Add(d);
                }
                return list.ToArray();
            }
            catch (Exception ex)
            {
                Log("PnP enumeration failed: " + ex.Message);
                return new PnpDevice[0];
            }
        }

        /// <summary>
        /// True when the scheduled reset task is registered (i.e. the one-time
        /// elevated setup has been done on this machine).
        /// </summary>
        public static bool IsTaskRegistered()
        {
            try
            {
                Process p = Run("schtasks", "/query /tn " + TaskName, 5000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Runs the one-time elevated setup: registers the scheduled task. Call
        /// through ShellExecute+RunAs so Windows asks for consent exactly once.
        /// Returns false when the user declines or registration fails.
        /// </summary>
        public static bool RunSetupElevated()
        {
            try
            {
                string script = Path.Combine(ToolDirectory(), "setup_usb_reset_task.ps1");
                if (!File.Exists(script))
                {
                    Log("setup script missing: " + script);
                    return false;
                }
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe");
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"";
                psi.Verb = "runas";          // the single consent
                psi.UseShellExecute = true;
                psi.CreateNoWindow = false;
                Process p = Process.Start(psi);
                p.WaitForExit(60000);
                bool ok = IsTaskRegistered();
                Log("elevated setup exit=" + p.ExitCode + ", task registered=" + ok);
                return ok;
            }
            catch (Exception ex)
            {
                Log("elevated setup failed: " + ex.Message);
                return false;
            }
        }

        static string ToolDirectory()
        {
            // tools\ lives beside the engine's bin\ (repo layout bin\.., tools\..).
            string baseDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            string candidate = Path.GetFullPath(Path.Combine(baseDir, "..\\tools"));
            if (Directory.Exists(candidate)) return candidate;
            return Path.GetFullPath(Path.Combine(baseDir, "tools"));
        }

        /// <summary>
        /// Attempts a software power-cycle of the scanner's USB device:
        /// find the node by friendly name, hand it to the elevated helper
        /// task, wait for the device to re-enumerate. Returns true when the
        /// device is back and reporting OK.
        /// </summary>
        public static bool TryReset(string friendlyName)
        {
            try
            {
                if (string.IsNullOrEmpty(friendlyName)) return false;

                PnpDevice[] devices = Enumerate();
                // Two passes: the interfaces carry the scanner's name but the node
                // we must restart is the PARENT composite, whose name is generic
                // ("USB Composite Device"). Restarting only an MI_ interface resets
                // that interface's driver stack, not the scanner chip - the carriage
                // stays wedged. So: find the VID/PID from any named match, then
                // restart the bare USB\VID_xxxx&PID_yyyy\<serial> node itself.
                string vidPid = null;
                foreach (PnpDevice d in devices)
                {
                    if (d.Name.IndexOf(friendlyName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!d.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) continue;
                    // "USB\VID_04A9&PID_1912&MI_00\7&..." or "USB\VID_04A9&PID_1912\4C24A8"
                    // both reduce to the hardware-id prefix "USB\VID_04A9&PID_1912\".
                    int cut = d.InstanceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
                    if (cut < 0) cut = d.InstanceId.LastIndexOf('\\');
                    if (cut <= 0) continue;
                    vidPid = d.InstanceId.Substring(0, cut) + "\\";
                    break;
                }
                if (vidPid == null)
                {
                    Log("no USB node matches '" + friendlyName + "' - nothing to reset");
                    return false;
                }

                PnpDevice best = null;
                foreach (PnpDevice d in devices)
                {
                    if (!d.InstanceId.StartsWith(vidPid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (d.InstanceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    best = d;   // bare composite parent: USB\VID_x&PID_y\<serial>
                    break;
                }
                if (best == null)
                {
                    Log("found " + vidPid + " but no bare parent node - falling back to the named interface");
                    foreach (PnpDevice d in devices)
                        if (d.Name.IndexOf(friendlyName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            d.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                        { best = d; break; }
                }
                if (best == null)
                {
                    Log("no usable reset target for '" + friendlyName + "'");
                    return false;
                }
                Log("reset target: " + best.InstanceId + " (" + best.Name + ")");

                if (!IsTaskRegistered())
                {
                    Log("reset task not registered - attempting one-time elevated setup");
                    if (!RunSetupElevated()) return false;
                }

                string target = TargetFile;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, best.InstanceId);

                Process run = Run("schtasks", "/run /tn " + TaskName, 15000);
                if (run.ExitCode != 0)
                {
                    Log("schtasks /run failed: " + run.StandardOutput.ReadToEnd().Trim());
                    return false;
                }

                // Re-enumeration takes a few seconds; the helper itself retries
                // the pnputil call, so allow a generous window.
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(2000);
                    foreach (PnpDevice d in Enumerate())
                        if (d.InstanceId == best.InstanceId && d.Status == "OK")
                        {
                            Log("device back after " + ((i + 1) * 2) + "s - reset worked");
                            return true;
                        }
                }
                Log("device did not come back within 20s");
                return false;
            }
            catch (Exception ex)
            {
                Log("reset attempt failed: " + ex.Message);
                return false;
            }
        }

        static Process Run(string exe, string args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            p.OutputDataReceived += delegate { };
            p.ErrorDataReceived += delegate { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit(timeoutMs);
            return p;
        }
    }
}
