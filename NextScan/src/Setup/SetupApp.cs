// =============================================================================
// NextScan Studio - installer entry point
// Plan ref: MASTER_PLAN section 19.
//
// One executable with two jobs. Run normally it installs; run with --uninstall
// from the folder it installed into, it removes. Keeping them in one binary
// means the uninstaller cannot fall out of step with the installer, which is
// the usual way an uninstaller comes to leave things behind.
// =============================================================================
using System;
using System.IO;
using System.Windows.Forms;

namespace NextScan.Setup
{
    public static class SetupApp
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool uninstalling = Has(args, "--uninstall");
            bool silent = Has(args, "--silent");
            bool relaunched = Has(args, "--relaunched");

            // The manifest asks for elevation, so this should not happen. It is
            // checked anyway: without it the failure would be a permission error
            // somewhere in the middle of writing to Program Files, with half a
            // program on disk.
            if (!SetupWork.IsAdministrator())
            {
                Complain("This installer needs to run as an administrator.\n\n" +
                         "Right-click it and choose Run as administrator.", silent);
                return 2;
            }

            if (!uninstalling && !SetupWork.HasNet48())
            {
                Complain(SetupWork.Product + " needs the .NET Framework 4.8 or newer.\n\n" +
                         "Install it from Microsoft, then run this again.", silent);
                return 3;
            }

            // A program cannot delete the file it is running from, and leaving
            // that one file behind means leaving the folder behind with it. So
            // an uninstall run from inside the installation folder copies itself
            // out to the temporary folder and continues from there.
            if (uninstalling && !relaunched && RunningFromInstallFolder())
            {
                if (SetupWork.RelaunchFromTemp(args)) return 0;
                // If that failed, carry on anyway: everything except this one
                // file still comes out.
            }

            if (silent)
            {
                try
                {
                    SetupWork.Progress quiet = delegate (string what, double done) { };
                    if (uninstalling) SetupWork.Uninstall(quiet);
                    else SetupWork.Install(SetupWork.DefaultDirectory, true, false, quiet);
                    return 0;
                }
                catch (Exception ex)
                {
                    Complain(ex.Message, true);
                    return 1;
                }
            }

            try { Application.Run(new SetupWindow(uninstalling)); }
            catch (Exception ex)
            {
                Complain(ex.Message, false);
                return 1;
            }
            return 0;
        }

        static bool RunningFromInstallFolder()
        {
            try
            {
                string here = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                return File.Exists(Path.Combine(here, SetupWork.ExeName));
            }
            catch { return false; }
        }

        static bool Has(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void Complain(string what, bool silent)
        {
            if (silent) { Console.Error.WriteLine(what); return; }
            MessageBox.Show(what, SetupWork.Product + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
