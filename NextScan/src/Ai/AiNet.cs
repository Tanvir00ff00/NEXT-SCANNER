// =============================================================================
// NextScan Studio - the network settings every provider needs
// Plan ref: docs/AI_LAYER.md
//
// .NET Framework decides which TLS versions HTTPS may use through one
// process-wide setting, and on its defaults a server that accepts only modern
// TLS is refused before a byte is sent: "Could not create SSL/TLS secure
// channel". One of the operator's own servers was unreachable for exactly this
// reason while it answered a browser fine. So TLS 1.2 is always allowed, and
// TLS 1.3 wherever Windows itself has it (Windows 11 and Server 2022 and
// later); asking for 1.3 on a Windows without it breaks connections instead.
// =============================================================================
using System;
using System.Net;

namespace NextScan.Ai
{
    public static class AiNet
    {
        static bool _done;

        /// <summary>Allows TLS 1.2, and 1.3 where Windows supports it. Safe to call often.</summary>
        public static void ModernTls()
        {
            if (_done) return;
            _done = true;
            try
            {
                SecurityProtocolType wanted = SecurityProtocolType.Tls12;
                Version os = Environment.OSVersion.Version;
                if (os.Major >= 10 && os.Build >= 20348) wanted |= (SecurityProtocolType)12288;    // Tls13
                ServicePointManager.SecurityProtocol |= wanted;
            }
            catch
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            }
        }
    }
}
