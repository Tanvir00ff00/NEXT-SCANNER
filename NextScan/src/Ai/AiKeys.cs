// =============================================================================
// NextScan Studio - where the API keys live
// Plan ref: docs/AI_LAYER.md
//
// A shop machine is used by more than one person, and an API key is money:
// anyone who reads it can spend it. So the keys are not in the settings file
// beside the scan resolution, and they are not in the diagnostics folder --
// which is the easy mistake, because that folder is deliberately easy to send
// to somebody when something goes wrong.
//
// DPAPI encrypts under the logged-in Windows account. It ships with Windows,
// needs no dependency, and leaves us no key of our own to lose. Copying the
// file to another machine gives that machine nothing.
// =============================================================================
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NextScan.Ai
{
    public static class AiKeys
    {
        static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NextScan", "keys");
            }
        }

        static string PathFor(string providerId)
        {
            // The id comes from our own provider list, never from input, but a
            // file name is a file name. An empty id passes a loop over its
            // characters without objection and lands on a file called ".key",
            // so it is rejected before the loop rather than by it.
            if (string.IsNullOrEmpty(providerId)) throw new AiTrouble("bad provider id");
            foreach (char c in providerId)
                if (!char.IsLetterOrDigit(c)) throw new AiTrouble("bad provider id");

            return Path.Combine(Folder, providerId.ToLowerInvariant() + ".key");
        }

        public static bool Has(string providerId)
        {
            try { return File.Exists(PathFor(providerId)); }
            catch { return false; }
        }

        public static void Set(string providerId, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { Forget(providerId); return; }

            try
            {
                Directory.CreateDirectory(Folder);
                byte[] sealed_ = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(PathFor(providerId), sealed_);
            }
            catch (Exception ex)
            {
                throw new AiTrouble("Could not store the key: " + ex.Message, ex);
            }
        }

        /// <summary>The key, or null. Never logged, never returned to the UI for display.</summary>
        public static string Get(string providerId)
        {
            try
            {
                string path = PathFor(providerId);
                if (!File.Exists(path)) return null;

                byte[] plain = ProtectedData.Unprotect(
                    File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // Written by another Windows account, or the profile was
                // rebuilt. Unreadable is the same as absent as far as the
                // caller is concerned.
                return null;
            }
        }

        public static void Forget(string providerId)
        {
            try { File.Delete(PathFor(providerId)); } catch { }
        }

        /// <summary>
        /// The last four characters, for confirming which key is in place
        /// without putting the key back on screen.
        /// </summary>
        public static string Tail(string providerId)
        {
            string key = Get(providerId);
            if (string.IsNullOrEmpty(key)) return "";
            return key.Length <= 4 ? new string('*', key.Length) : "****" + key.Substring(key.Length - 4);
        }
    }
}
