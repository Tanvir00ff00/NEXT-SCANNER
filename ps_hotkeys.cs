using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

public class PSHotkeys
{
    const string ROOT = @"C:\PS_Fix";
    static string LOG  = Path.Combine(ROOT, "hotkeys_log.txt");

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    static LowLevelKeyboardProc _proc = HookCallback;
    static IntPtr _hookID = IntPtr.Zero;
    static NotifyIcon tray;

    static void L(string m)
    {
        try { File.AppendAllText(LOG, DateTime.Now.ToString("HH:mm:ss") + "  " + m + "\r\n"); }
        catch { }
    }

    static bool IsPhotoshopActive()
    {
        try {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            Process p = Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("Photoshop", StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    static bool HasActiveDocument()
    {
        try {
            object app = Marshal.GetActiveObject("Photoshop.Application");
            if (app == null) return false;
            object docs = app.GetType().InvokeMember("Documents", BindingFlags.GetProperty, null, app, null);
            int count = (int)docs.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, docs, null);
            return count > 0;
        } catch { return false; }
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    static void RunScanHelper()
    {
        try {
            Process[] existing = Process.GetProcessesByName("scanhelper");
            if (existing.Length > 0 && existing[0].MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(existing[0].MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(existing[0].MainWindowHandle);
                L("Brought existing scanhelper.exe to foreground");
                return;
            }

            string exe = Path.Combine(ROOT, "scanhelper.exe");
            if (File.Exists(exe)) {
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = ROOT;
                Process.Start(psi);
                L("Triggered scanhelper.exe via F6 hotkey");
            }
        } catch (Exception ex) {
            L("Failed to start scanhelper: " + ex.Message);
        }
    }

    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            Keys key = (Keys)vkCode;

            if (key == Keys.F6)
            {
                if (IsPhotoshopActive())
                {
                    L("F6 pressed in Photoshop -> opening Scanner Studio");
                    RunScanHelper();
                    return (IntPtr)1; // swallow key so PS doesn't beep or toggle color palette
                }
            }
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        try {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr h = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
                if (h != IntPtr.Zero) return h;
            }
        } catch { }
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, IntPtr.Zero, 0);
    }

    [STAThread]
    public static void Main(string[] args)
    {
        try {
            int currentPid = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("ps_hotkeys")) {
                if (p.Id != currentPid) {
                    try { p.Kill(); p.WaitForExit(1000); } catch { }
                }
            }

            Application.EnableVisualStyles();
            _hookID = SetHook(_proc);
            L("PS Hotkey Hook active (F6 enabled anytime in Photoshop)");

            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "Photoshop Hotkey Companion (F6 Scan)";
            tray.Visible = true;

            ContextMenu cm = new ContextMenu();
            cm.MenuItems.Add("Photoshop Hotkeys (Active)", delegate { });
            cm.MenuItems[0].Enabled = false;
            cm.MenuItems.Add("-");
            cm.MenuItems.Add("Exit", delegate {
                if (_hookID != IntPtr.Zero) { UnhookWindowsHookEx(_hookID); _hookID = IntPtr.Zero; }
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                Application.ExitThread();
            });
            tray.ContextMenu = cm;

            Application.Run(new ApplicationContext());
        } catch (Exception ex) {
            L("Fatal error in ps_hotkeys: " + ex.ToString());
        } finally {
            if (_hookID != IntPtr.Zero) { UnhookWindowsHookEx(_hookID); _hookID = IntPtr.Zero; }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
        }
    }
}
