// =============================================================================
// NextScan Studio - colour profiles
// Plan ref: MASTER_PLAN section 14.3.
//
// A scan that arrives untagged is not colour managed, it is colour guessed:
// Photoshop reads the numbers as whatever its working space happens to be, or
// stops to ask, and the answer changes from one machine to the next. A tagged
// scan says what its numbers mean, and keeps saying it after the file leaves
// here.
//
// What gets claimed is deliberately modest. Only two things are ever attached:
// a profile the device itself named, or sRGB. sRGB is not a guess dressed up --
// eSCL scans are sRGB by specification, and consumer flatbeds target it -- but
// it is still an assumption, so it is said out loud rather than implied. A
// profile is never invented, and one that does not survive inspection is
// dropped rather than passed on.
// =============================================================================
using System;
using System.IO;

namespace NextScan.Core
{
    public static class IccProfile
    {
        const string SrgbFile = "sRGB Color Space Profile.icm";

        // Windows keeps profiles in one place, and a name a device hands back is
        // usually a bare file name that resolves there.
        static string ColorDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                @"spool\drivers\color");
        }

        static byte[] _srgb;
        static bool _srgbLookedFor;

        /// <summary>
        /// The sRGB profile Windows ships with. Null if it is not there, which
        /// is not worth failing a scan over: an untagged page is exactly what
        /// was handed over before any of this existed.
        /// </summary>
        public static byte[] Srgb()
        {
            if (_srgbLookedFor) return _srgb;
            _srgbLookedFor = true;
            _srgb = Load(SrgbFile);
            return _srgb;
        }

        /// <summary>
        /// Reads a profile by path, or by the bare name a device tends to give,
        /// and returns it only if it really is one.
        /// </summary>
        public static byte[] Load(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;

            try
            {
                string path = nameOrPath;
                if (!File.Exists(path))
                {
                    path = Path.Combine(ColorDirectory(), Path.GetFileName(nameOrPath));
                    if (!File.Exists(path)) return null;
                }

                // An upper bound because this gets copied into every page of
                // every scan. Real profiles are a few kilobytes; the largest
                // sane ones are a few hundred.
                FileInfo info = new FileInfo(path);
                if (info.Length < 132 || info.Length > 8 * 1024 * 1024) return null;

                byte[] bytes = File.ReadAllBytes(path);
                return IsProfile(bytes) ? bytes : null;
            }
            catch { return null; }   // an unreadable profile is one we do not attach
        }

        /// <summary>
        /// Checks the two things an ICC profile cannot get wrong: it says how
        /// long it is, and it says what it is.
        ///
        /// Worth checking rather than trusting a file extension. Something
        /// truncated or mistyped, handed to Photoshop as a profile, is worse
        /// than no profile at all: it produces a document that claims to know
        /// what its colours mean and does not.
        /// </summary>
        public static bool IsProfile(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 132) return false;

            // Size first, big endian, as everything in an ICC header is.
            long declared = ((long)bytes[0] << 24) | ((long)bytes[1] << 16) |
                            ((long)bytes[2] << 8) | bytes[3];
            if (declared < 132 || declared > bytes.Length) return false;

            // 'acsp' at offset 36 is the signature every profile carries.
            return bytes[36] == (byte)'a' && bytes[37] == (byte)'c' &&
                   bytes[38] == (byte)'s' && bytes[39] == (byte)'p';
        }
    }
}
