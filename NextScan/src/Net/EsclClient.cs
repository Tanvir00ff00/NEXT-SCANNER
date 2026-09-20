// =============================================================================
// NextScan Studio - eSCL HTTP client (plan section 7.4.2)
//
// Speaks the eSCL ("AirPrint scan") job flow over HTTP:
//   GET  {root}/ScannerCapabilities   -> capabilities XML
//   GET  {root}/ScannerStatus         -> status XML
//   POST {root}/ScanJobs              -> 201 + Location: job URI (used VERBATIM -
//                                        the id may be an int or a UUID; never
//                                        reconstruct it, plan 7.4.2)
//   GET  {jobUri}/NextDocument        -> image bytes; 404/410 = job complete.
//                                        503 MUST be retried: real devices (the
//                                        HP LaserJet MFP M28w among them) return
//                                        503 storms at high dpi while warming up.
//                                        Policy: 30 x 1 s here, 10 attempts for
//                                        every other request.
//   DELETE {jobUri}                   -> cancel
//
// Pure managed, no vendor driver, safe to run in-process (plan section 5.1).
// Raw sockets instead of HttpWebRequest because several printer firmwares sit
// on non-standard ports speaking plain HTTP next to TLS on 443, and we need
// exact control of timeouts and retry pacing.
// =============================================================================
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace NextScan.Net
{
    public class EsclClient
    {
        public Action<string> Log = delegate { };

        readonly string _host;
        readonly int _port;
        readonly bool _tls;
        readonly string _root;      // e.g. "/eSCL" - from the mDNS rs= TXT key

        public EsclClient(string baseUrl)
        {
            // baseUrl form: http://192.168.1.50:8090/eSCL  or  https://host/eSCL/
            if (string.IsNullOrEmpty(baseUrl))
                throw new ArgumentException("eSCL base URL is empty", "baseUrl");

            Uri uri;
            string address = baseUrl.Trim();
            if (address.IndexOf("://", StringComparison.Ordinal) < 0) address = "http://" + address;
            if (!Uri.TryCreate(address, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Invalid eSCL HTTP(S) URL", "baseUrl");
            _tls = uri.Scheme == "https";
            _host = uri.DnsSafeHost.Trim('[', ']');
            _port = uri.Port;
            _root = uri.AbsolutePath.TrimEnd('/');
            if (_root.Length == 0) _root = "/eSCL";

        }

        public string BaseUrl
        {
            get { return (_tls ? "https://" : "http://") + Authority + _root; }
        }

        string Authority
        {
            get { return (_host.IndexOf(':') >= 0 ? "[" + _host + "]" : _host) + ":" + _port; }
        }

        /// <summary>Fetches the capabilities document (retries transient failures).</summary>
        public byte[] GetCapabilities()
        {
            byte[] body;
            string loc;
            int status = Request("GET", _root + "/ScannerCapabilities", null, "application/xml", 10, out body, out loc);
            if (status != 200)
                throw new IOException("ScannerCapabilities returned HTTP " + status);
            return body;
        }

        /// <summary>Fetches the status document.</summary>
        public byte[] GetStatus()
        {
            byte[] body;
            string loc;
            int status = Request("GET", _root + "/ScannerStatus", null, "application/xml", 10, out body, out loc);
            if (status != 200)
                throw new IOException("ScannerStatus returned HTTP " + status);
            return body;
        }

        /// <summary>Creates a scan job; returns the job URI from the Location header.</summary>
        public string CreateJob(byte[] settingsXml)
        {
            byte[] body;
            string location;
            int status = Request("POST", _root + "/ScanJobs", settingsXml, "application/xml", 10, out body, out location);
            if (status != 201 || string.IsNullOrEmpty(location))
                throw new IOException("ScanJobs POST returned HTTP " + status +
                    (string.IsNullOrEmpty(location) ? " without a Location header" : ""));
            Uri job = new Uri(new Uri(BaseUrl + "/ScanJobs"), location);
            if (job.Scheme != (_tls ? "https" : "http") ||
                !string.Equals(job.DnsSafeHost.Trim('[', ']'), _host, StringComparison.OrdinalIgnoreCase) || job.Port != _port)
                throw new IOException("ScanJobs Location points to a different scanner");
            return job.PathAndQuery;
        }

        /// <summary>
        /// Fetches the next page. Returns null when the job is complete (404/410).
        /// 503 is retried up to 30 times at 1 s - the documented real-device
        /// behaviour while a page is still being prepared.
        /// </summary>
        public byte[] GetNextDocument(string jobUri)
        {
            byte[] body;
            string loc;
            // The page endpoint is the job URI + /NextDocument - passing the bare
            // job URI hits a 404 and reads as "job complete with no pages", which
            // is exactly how this bug first showed itself.
            string path = jobUri.TrimEnd('/') + "/NextDocument";
            int status = Request("GET", path, null, "application/xml", 30, out body, out loc);
            if (status == 404 || status == 410) return null;
            if (status != 200)
                throw new IOException("NextDocument returned HTTP " + status);
            return body;
        }

        public void DeleteJob(string jobUri)
        {
            byte[] body;
            string loc;
            try { Request("DELETE", jobUri, null, "application/xml", 3, out body, out loc); }
            catch (Exception ex) { Log("DELETE job failed (ignored): " + ex.Message); }
        }

        // ---------------------------------------------------------------- plumbing
        /// <summary>
        /// One HTTP exchange with the eSCL retry policy: a connection failure or a
        /// 503 is retried (attempts times total, 1 s apart); anything else returns
        /// immediately.
        /// </summary>
        int Request(string method, string path, byte[] payload, string contentType,
                    int attempts, out byte[] body, out string locationHeader)
        {
            locationHeader = null;
            body = null;
            Exception last = null;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    int status = Exchange(method, path, payload, contentType, out body, out locationHeader);
                    if (status == 503)
                    {
                        Log(method + " " + path + " -> 503 (attempt " + attempt + "/" + attempts + ")");
                        if (attempt < attempts) { System.Threading.Thread.Sleep(1000); continue; }
                        return status;
                    }
                    return status;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log(method + " " + path + " threw: " + ex.Message + " (attempt " + attempt + "/" + attempts + ")");
                    if (attempt < attempts) System.Threading.Thread.Sleep(1000);
                }
            }
            if (last != null) throw new IOException("eSCL request failed after " + attempts + " attempts: " + last.Message, last);
            return -1;
        }

        /// <summary>Single synchronous HTTP/1.1 exchange over a fresh connection.</summary>
        int Exchange(string method, string path, byte[] payload, string contentType,
                     out byte[] body, out string locationHeader)
        {
            body = null;
            locationHeader = null;

            using (TcpClient tcp = new TcpClient())
            {
                tcp.NoDelay = true;
                IAsyncResult ar = tcp.BeginConnect(_host, _port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(4000))
                    throw new IOException("connect timeout to " + _host + ":" + _port);
                tcp.EndConnect(ar);
                tcp.ReceiveTimeout = 30000;
                tcp.SendTimeout = 15000;

                Stream s = tcp.GetStream();
                if (_tls)
                {
                    // Validate the scanner certificate with the OS trust store. Silently
                    // accepting every certificate exposes scanned documents to interception.
                    System.Net.Security.SslStream ssl = new System.Net.Security.SslStream(s, false);
                    ssl.AuthenticateAsClient(_host);
                    s = ssl;
                }

                StringBuilder head = new StringBuilder();
                head.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
                head.Append("Host: ").Append(Authority).Append("\r\n");
                head.Append("Accept: */*\r\n");
                head.Append("Connection: close\r\n");
                head.Append("User-Agent: NextScan/1.0\r\n");
                if (payload != null)
                {
                    head.Append("Content-Type: ").Append(contentType).Append("\r\n");
                    head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
                }
                head.Append("\r\n");

                byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                s.Write(headBytes, 0, headBytes.Length);
                if (payload != null) s.Write(payload, 0, payload.Length);

                // Connection: close means the server ends the stream after the body,
                // so read-to-end is the simplest correct framing (we do not need
                // keep-alive for a one-job-per-connection protocol).
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[16384];
                    while (true)
                    {
                        int n = s.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                        if (ms.Length > 64 * 1024 * 1024) throw new IOException("eSCL response exceeds 64 MB");
                    }
                    byte[] resp = ms.ToArray();

                    int headerEnd = IndexOf(resp, Encoding.ASCII.GetBytes("\r\n\r\n"));
                    if (headerEnd < 0) throw new IOException("malformed HTTP response (no header terminator)");
                    string headers = Encoding.ASCII.GetString(resp, 0, headerEnd);
                    body = new byte[resp.Length - headerEnd - 4];
                    Buffer.BlockCopy(resp, headerEnd + 4, body, 0, body.Length);

                    string[] lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    int status = 0;
                    long contentLength = -1;
                    bool chunked = false;
                    foreach (string line in lines)
                    {
                        if (status == 0 && line.StartsWith("HTTP/", StringComparison.Ordinal))
                        {
                            int sp = line.IndexOf(' ');
                            int code;
                            if (sp > 0 && line.Length >= sp + 4 && int.TryParse(line.Substring(sp + 1, 3), out code)) status = code;
                        }
                        else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!long.TryParse(line.Substring(15).Trim(), out contentLength) || contentLength < 0)
                                throw new IOException("Invalid HTTP Content-Length");
                        }
                        else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.Equals(line.Substring(18).Trim(), "chunked", StringComparison.OrdinalIgnoreCase))
                                throw new IOException("Unsupported HTTP Transfer-Encoding");
                            chunked = true;
                        }
                        else if (line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))
                        {
                            locationHeader = line.Substring(9).Trim();
                        }
                    }
                    if (status == 0) throw new IOException("no status line in response");
                    if (chunked) body = DecodeChunks(body);
                    else if (contentLength >= 0 && body.LongLength != contentLength)
                        throw new IOException("Truncated or invalid HTTP body");
                    return status;
                }
            }
        }

        static byte[] DecodeChunks(byte[] bytes)
        {
            using (MemoryStream output = new MemoryStream())
            {
                int pos = 0;
                while (true)
                {
                    int end = pos;
                    while (end + 1 < bytes.Length && !(bytes[end] == 13 && bytes[end + 1] == 10)) end++;
                    if (end + 1 >= bytes.Length) throw new IOException("Missing HTTP chunk size");
                    string line = Encoding.ASCII.GetString(bytes, pos, end - pos).Split(';')[0];
                    int size;
                    if (!int.TryParse(line, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out size) || size < 0)
                        throw new IOException("Invalid HTTP chunk size");
                    pos = end + 2;
                    if (size == 0)
                    {
                        if (pos + 1 >= bytes.Length) throw new IOException("Truncated HTTP chunk terminator");
                        return output.ToArray();
                    }
                    if (size > bytes.Length - pos - 2) throw new IOException("Truncated HTTP chunk");
                    output.Write(bytes, pos, size);
                    pos += size;
                    if (bytes[pos] != 13 || bytes[pos + 1] != 10) throw new IOException("Invalid HTTP chunk delimiter");
                    pos += 2;
                }
            }
        }

        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}
