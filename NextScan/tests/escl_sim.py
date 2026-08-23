"""
NextScan Studio - eSCL simulator (plan section 18.3, EsclSimulator)

  python tests/escl_sim.py [port]

A tiny eSCL HTTP device for the test harness. Personalities via env:
  ESCL_SIM_503_COUNT   NextDocument calls that answer 503 before pages
                       (the real-device "503 storm while warming up")
  ESCL_SIM_JOB_STYLE   uuid | int  (job ids are ints or UUIDs; the client
                       must use the Location header verbatim, plan 7.4.2)
  ESCL_SIM_PAGES       pages per job (default 1)

Serves a fixed 48x32 8-bar JPEG page so the harness asserts exact dims.
"""
import base64, http.server, os, socketserver, sys, uuid

PAGE_B64 = "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAgADADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDl/wBnn/mP/wDbv/7Ur5H+Kn/JT/F//YYvP/R719cfs8/8x/8A7d//AGpXyP8AFT/kp/i//sMXn/o96+x+jD/yVec/9eqf5o8LxL/5PFxB/hw//qPRP22+J/8AyALf/r5X/wBAevk79ob/AJgH/bx/7Tr6x+J//IAt/wDr5X/0B6+Tv2hv+YB/28f+06+N+jN/yTGH/wCvlX8j5/hb/lIbK/8Ar3U/9Ra5yWn/APHhbf8AXJf5Cv1br8pNP/48Lb/rkv8AIV+rdd+P/wB7rf4pfmz9PzL/AH6v/jl+bPyE/Z5/5j//AG7/APtSvkf4qf8AJT/F/wD2GLz/ANHvX1x+zz/zH/8At3/9qV8j/FT/AJKf4v8A+wxef+j3r6P6MP8AyVec/wDXqn+aPkPEv/k8XEH+HD/+o9E/bb4n/wDIAt/+vlf/AEB6+Tv2hv8AmAf9vH/tOvrH4n/8gC3/AOvlf/QHr5O/aG/5gH/bx/7Tr436M3/JMYf/AK+VfyPn+Fv+Uhsr/wCvdT/1FrnJaf8A8eFt/wBcl/kK/Vuvyk0//jwtv+uS/wAhX6t134//AHut/il+bP0/Mv8Afq/+OX5s/9k="
PAGE = base64.b64decode(PAGE_B64)

CAPS_XML_HEAD = (
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
    "<scan:ScannerCapabilities"
    " xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\""
    " xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\" version=\"2.0\">"
    "<pwg:MakeAndModel>NextScan eSCL Simulator</pwg:MakeAndModel>"
    "<scan:Platen>"
    "<scan:PlatenMaxWidth>2550</scan:PlatenMaxWidth>"
    "<scan:PlatenMaxHeight>3510</scan:PlatenMaxHeight>"
    "<scan:SettingProfiles><scan:SettingProfile>"
    "<scan:ColorModes>"
    "<scan:ColorMode>RGB24</scan:ColorMode>"
    "<scan:ColorMode>Grayscale8</scan:ColorMode>"
    "<scan:ColorMode>BlackAndWhite1</scan:ColorMode>"
    "</scan:ColorModes>"
    "<scan:DocumentFormats><scan:DocumentFormat>image/jpeg</scan:DocumentFormat></scan:DocumentFormats>"
    "<scan:SupportedResolutions><scan:DiscreteResolutions>"
    "<scan:DiscreteResolution><scan:XResolution>75</scan:XResolution></scan:DiscreteResolution>"
    "<scan:DiscreteResolution><scan:XResolution>150</scan:XResolution></scan:DiscreteResolution>"
    "<scan:DiscreteResolution><scan:XResolution>300</scan:XResolution></scan:DiscreteResolution>"
    "</scan:DiscreteResolutions></scan:SupportedResolutions>"
    "</scan:SettingProfile></scan:SettingProfiles>"
    "</scan:Platen>"
    "</scan:ScannerCapabilities>"
)

STATUS_XML = (
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
    "<scan:ScannerStatus"
    " xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\" version=\"2.0\">"
    "<scan:ScannerState>Idle</scan:ScannerState>"
    "</scan:ScannerStatus>"
)

STORM = int(os.environ.get("ESCL_SIM_503_COUNT", "0"))
JOB_STYLE = os.environ.get("ESCL_SIM_JOB_STYLE", "uuid")
PAGES = int(os.environ.get("ESCL_SIM_PAGES", "1"))
JOBS = {}


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        print("sim:", fmt % args, file=sys.stderr, flush=True)

    def _send(self, status, data=b"", ctype="text/xml", location=None):
        self.send_response(status)
        if location:
            self.send_header("Location", location)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        if data:
            self.wfile.write(data)

    def do_GET(self):
        p = self.path.split("?")[0]
        if p.endswith("/ScannerCapabilities"):
            self._send(200, CAPS_XML_HEAD.encode())
        elif p.endswith("/ScannerStatus"):
            self._send(200, STATUS_XML.encode())
        elif p.endswith("/NextDocument"):
            job = JOBS.get(p[:-len("/NextDocument")])
            if job is None:
                self._send(404)
            elif job["storm_left"] > 0:
                job["storm_left"] -= 1
                self._send(503)
            elif job["fetched"] >= PAGES:
                self._send(404)
            else:
                job["fetched"] += 1
                self._send(200, PAGE, "image/jpeg")
        else:
            self._send(404)

    def do_POST(self):
        p = self.path.split("?")[0]
        if not p.endswith("/ScanJobs"):
            self._send(404)
            return
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length) if length else b""
        print("sim: ScanJobs body", len(body), "bytes", file=sys.stderr, flush=True)
        jid = str(uuid.uuid4()) if JOB_STYLE == "uuid" else str(len(JOBS) + 1)
        path = "/eSCL/ScanJobs/" + jid
        JOBS[path] = {"fetched": 0, "storm_left": STORM}
        self._send(201, location=path)

    def do_DELETE(self):
        JOBS.pop(self.path.split("?")[0], None)
        self._send(200)


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8951
    print("sim: eSCL simulator on port", port,
          "storm=" + str(STORM), "style=" + JOB_STYLE, "pages=" + str(PAGES),
          file=sys.stderr, flush=True)
    Server(("127.0.0.1", port), Handler).serve_forever()
