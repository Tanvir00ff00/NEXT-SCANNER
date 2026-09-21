// =============================================================================
// NextScan Studio - what this build calls itself
// Plan ref: MASTER_PLAN section 13.
//
// One place holds the version. Not three: the About panel, the executable's
// file properties and the installer all take it from here, because a version
// that is written down in several places is a version that disagrees with
// itself the first time somebody is in a hurry.
//
// build.ps1 reads Version out of this file to stamp the executable, and
// build_installer.ps1 takes its default from the same line. Bump it here and
// everything that reports a version reports the new one.
// =============================================================================

namespace NextScan.Core
{
    public static class AppInfo
    {
        /// <summary>
        /// Raised whenever something lands that an operator would notice. The
        /// second number is for features, the first for a release that changes
        /// how the thing is used.
        /// </summary>
        public const string Version = "1.2";

        public const string Product = "NextScan Studio";

        /// <summary>Where it lives. It is open source; this is not a support address.</summary>
        public const string Repository = "github.com/Tanvir00ff00/NEXT-SCANNER";
    }
}
