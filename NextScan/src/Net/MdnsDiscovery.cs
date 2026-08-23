// =============================================================================
// NextScan Studio - mDNS / DNS-SD discovery (plan section 7.4.1)
//
// Raw multicast DNS client on UDP 5353 (224.0.0.251): sends one-shot PTR
// queries for _uscan._tcp.local (plain) and _uscans._tcp.local (TLS), then
// listens a few seconds collecting PTR/SRV/TXT/A records into services.
//
// The plan prefers the Win32 DNS-SD API (DnsServiceBrowse) with this raw
// client as fallback; this increment ships the raw client first because it is
// dependency-free, testable against a scripted responder, and identical on
// every Windows build. The TXT rs= key is honoured for the eSCL root path -
// it is "eSCL" on most devices but NOT always (a recorded trap), and
// hard-coding it produces silent 404s on the exceptions.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NextScan.Net
{
    public class MdnsService
    {
        public string Instance;    // human-readable service instance
        public string Name { get { return Instance; } }
        public string Host;        // A-record host name
        public string Address;     // resolved IPv4
        public int Port;
        public string RootPath = "eSCL";   // TXT rs= key; NOT always "eSCL"
        public string Model = "";          // TXT mdl=
        public bool Tls;                   // _uscans_ vs _uscan_

        public string BaseUrl
        {
            get
            {
                string scheme = Tls ? "https" : "http";
                string host = string.IsNullOrEmpty(Address) ? Host : Address;
                return scheme + "://" + host + ":" + Port + "/" + RootPath.TrimStart('/');
            }
        }
    }

    public static class MdnsDiscovery
    {
        public static Action<string> Log = delegate { };

        /// <summary>
        /// Browses for eSCL scanners for up to timeoutMs. Returns every service
        /// that resolved to an address. One-shot query + passive collection:
        /// simpler than continuous querying and enough for a probe UI.
        /// </summary>
        public static List<MdnsService> Browse(int timeoutMs)
        {
            List<MdnsService> services = new List<MdnsService>();
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                // mDNS responses are multicast to the GROUP on port 5353, not unicast
                // back to the querier's source port, so we must listen on 5353 itself
                // (SO_REUSEADDR makes coexistence with other mDNS stacks possible).
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));

                IPEndPoint mcast = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                byte[] q1 = BuildPtrQuery("_uscan._tcp.local");
                byte[] q2 = BuildPtrQuery("_uscans._tcp.local");
                udp.Send(q1, q1.Length, mcast);
                udp.Send(q2, q2.Length, mcast);

                // Records collected by service instance name.
                Dictionary<string, PtrRecord> ptrs = new Dictionary<string, PtrRecord>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, SrvRecord> srvs = new Dictionary<string, SrvRecord>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, Dictionary<string, string>> txts =
                    new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> addrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    int remaining = (int)Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    udp.Client.ReceiveTimeout = remaining;
                    byte[] data;
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    try
                    {
                        data = udp.Receive(ref from);
                    }
                    catch (SocketException)
                    {
                        break;   // timeout - collection window over
                    }

                    if (data.Length < 12 || (data[2] & 0x80) == 0) continue;   // queries only, thanks
                    ParseResponse(data, ptrs, srvs, txts, addrs);
                }

                foreach (KeyValuePair<string, SrvRecord> kv in srvs)
                {
                    SrvRecord srv = kv.Value;
                    Dictionary<string, string> txt;
                    txts.TryGetValue(kv.Key, out txt);
                    if (txt == null) txt = new Dictionary<string, string>();

                    MdnsService s = new MdnsService();
                    s.Instance = kv.Key;
                    s.Host = srv.Target;
                    s.Port = srv.Port;
                    s.Tls = kv.Key.IndexOf("_uscans", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            srv.Name.IndexOf("_uscans", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (txt.ContainsKey("rs")) s.RootPath = txt["rs"].Trim('/');
                    if (txt.ContainsKey("mdl")) s.Model = txt["mdl"];
                    if (txt.ContainsKey("ty")) s.Instance = txt["ty"];
                    string ip;
                    if (addrs.TryGetValue(srv.Target, out ip)) s.Address = ip;
                    if (!string.IsNullOrEmpty(s.Address)) services.Add(s);
                }

                return services;
            }
            catch (Exception ex)
            {
                Log("mDNS browse failed: " + ex.Message);
                return services;
            }
            finally
            {
                if (udp != null) { try { udp.Close(); } catch { } }
            }
        }

        class PtrRecord { public string Target; public string Name; }
        class SrvRecord { public string Name; public string Target; public int Port; }

        static byte[] BuildPtrQuery(string service)
        {
            List<byte> q = new List<byte>();
            q.AddRange(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });   // header: 1 question
            foreach (string part in service.Split('.'))
            {
                q.Add((byte)part.Length);
                q.AddRange(Encoding.UTF8.GetBytes(part));
            }
            q.Add(0);
            q.AddRange(new byte[] { 0, 12 });   // PTR
            q.AddRange(new byte[] { 0, 1 });    // IN
            return q.ToArray();
        }

        static void ParseResponse(byte[] d,
            Dictionary<string, PtrRecord> ptrs,
            Dictionary<string, SrvRecord> srvs,
            Dictionary<string, Dictionary<string, string>> txts,
            Dictionary<string, string> addrs)
        {
            try
            {
                int qd = (d[4] << 8) | d[5];
                int an = (d[6] << 8) | d[7];
                int ns = (d[8] << 8) | d[9];
                int ar = (d[10] << 8) | d[11];

                int off = 12;
                for (int i = 0; i < qd; i++)
                {
                    string skipped;
                    off = ParseName(d, off, out skipped);
                    off += 4;
                }

                int total = an + ns + ar;
                for (int i = 0; i < total; i++)
                {
                    string owner;
                    off = ParseName(d, off, out owner);
                    if (off + 10 > d.Length) return;
                    int type = (d[off] << 8) | d[off + 1];
                    int rdlen = (d[off + 8] << 8) | d[off + 9];
                    off += 10;
                    int rdStart = off;
                    int next = off + rdlen;
                    if (next > d.Length) return;

                    if (type == 12 && rdlen >= 2)   // PTR: owner is the service type, target the instance
                    {
                        string target;
                        ParseName(d, rdStart, out target);
                        string instance = FirstLabel(target);
                        if (instance.Length > 0 && !ptrs.ContainsKey(instance))
                            ptrs[instance] = new PtrRecord { Target = target, Name = owner };
                    }
                    else if (type == 33 && rdlen >= 8)   // SRV: owner is the instance, target the host
                    {
                        int port = (d[rdStart + 4] << 8) | d[rdStart + 5];
                        string host;
                        ParseName(d, rdStart + 6, out host);
                        srvs[owner] = new SrvRecord { Name = owner, Target = host, Port = port };
                    }
                    else if (type == 16)   // TXT
                    {
                        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        int p = rdStart;
                        while (p < rdStart + rdlen)
                        {
                            int len = d[p];
                            if (len == 0 || p + 1 + len > d.Length) break;
                            string kv = Encoding.UTF8.GetString(d, p + 1, len);
                            int eq = kv.IndexOf('=');
                            if (eq > 0) map[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                            p += 1 + len;
                        }
                        txts[owner] = map;
                    }
                    else if (type == 1 && rdlen == 4)   // A
                    {
                        addrs[owner] = d[rdStart] + "." + d[rdStart + 1] + "." + d[rdStart + 2] + "." + d[rdStart + 3];
                    }

                    off = next;
                }
            }
            catch
            {
                // A malformed record ends parsing of THIS packet only.
            }
        }

        /// <summary>Reads a possibly-compressed DNS name; returns the offset after it.</summary>
        static int ParseName(byte[] d, int off, out string name)
        {
            StringBuilder sb = new StringBuilder();
            bool jumped = false;
            int end = off;
            int guard = 0;
            while (off < d.Length && guard++ < 64)
            {
                int len = d[off];
                if (len == 0)
                {
                    off++;
                    if (!jumped) end = off;
                    break;
                }
                if ((len & 0xC0) == 0xC0)
                {
                    if (off + 1 >= d.Length) break;
                    int ptr = ((len & 0x3F) << 8) | d[off + 1];
                    if (!jumped) end = off + 2;
                    off = ptr;
                    jumped = true;
                    continue;
                }
                off++;
                if (off + len > d.Length) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(d, off, len));
                off += len;
            }
            name = sb.ToString();
            return end;
        }

        static string FirstLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }
    }
}
