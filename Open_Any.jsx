// ============ SETTINGS ============
var EXE      = "C:/PS_Fix/fiximg.exe";
var QDIR     = "C:/PS_Fix/fixqueue";
var WORK_DIR = "C:/PS_Fix/tmp";
var BACKUP   = "C:/PS_Fix/backup_original";
// (no wait setting any more -- this script never blocks Photoshop)
var REPAIR_IN_PLACE = true;   // put the fixed file back where it came from
var KEEP_BACKUP     = true;
var SHOW_LOG = false;         // true when something breaks
// ==================================

var LOG = [];
var T0 = new Date().getTime();

function W(s) {
    LOG.push((LOG.length + 1) + ") [" + (new Date().getTime() - T0) + "ms] " + s);
}

function dumpLog() {
    try {
        var lf = new File(WORK_DIR + "/jsx_log.txt");
        try { lf.encoding = "BINARY"; } catch (e) { }
        if (lf.open("w")) { lf.write(LOG.join("\r\n")); lf.close(); }
    } catch (e) { }
}

// Only ever used for tiny pauses now. The old version fell back to a spin loop that
// pinned a CPU core at 100%, which made the freeze worse rather than better.
function nap(ms) {
    try { $.sleep(ms); } catch (e) { }
}

function isFileObj(o) {
    if (o === null || typeof o !== "object") { return false; }
    try { if (String(o.constructor.name) === "File") { return true; } } catch (e) { }
    try { if (typeof o.fsName === "string" && o.fsName.length > 2) { return true; } } catch (e) { }
    return false;
}

function toFileList(picked) {
    var out = [];
    if (picked === null || picked === false || typeof picked === "undefined") { return out; }
    if (isFileObj(picked)) { out.push(picked); return out; }
    var isArr = false;
    try { isArr = (String(picked.constructor.name) === "Array"); } catch (e) { }
    if (!isArr) { return out; }
    var n = 0;
    try { n = picked.length; } catch (e) { n = 0; }
    if (!(n > 0) || n > 500) { return out; }
    for (var i = 0; i < n; i++) {
        var it = null;
        try { it = picked[i]; } catch (e) { it = null; }
        if (isFileObj(it)) { out.push(it); }
    }
    return out;
}

// a .bat is the only launcher that works on CS 8.0 and 2026 alike
function runBat(name, cmdLine) {
    var ok = false;
    try {
        var b = new File(WORK_DIR + "/" + name);
        try { b.encoding = "BINARY"; } catch (e) { }
        if (b.open("w")) {
            b.write("@echo off\r\n" + cmdLine + "\r\n");
            b.close();
            ok = b.execute();
        }
    } catch (e) { W("runBat FAILED: " + e); }
    return ok;
}

function stamp() {
    var n = new Date();
    function p(x) { return (x < 10 ? "0" : "") + x; }
    return n.getFullYear() + p(n.getMonth() + 1) + p(n.getDate()) + "_"
         + p(n.getHours()) + p(n.getMinutes()) + p(n.getSeconds());
}

// never collide, even on rapid fire or the same photo twice
var uidCounter = 0;
function uid() {
    uidCounter++;
    return String(new Date().getTime())
         + "_" + uidCounter
         + "_" + Math.floor(Math.random() * 100000);
}

// if this photo is already open, Photoshop holds the file and the write fails
function closeIfOpen(path) {
    var closed = 0;
    try {
        var want = String(path).toLowerCase();
        for (var i = app.documents.length - 1; i >= 0; i--) {
            var d = app.documents[i];
            var p = "";
            try { p = String(d.fullName.fsName).toLowerCase(); } catch (e) { continue; }
            if (p === want) {
                try { d.close(SaveOptions.DONOTSAVECHANGES); closed++; } catch (e) { }
            }
        }
    } catch (e) { }
    return closed;
}

// the resident helper writes a heartbeat every 2 seconds
function daemonAlive() {
    try {
        var a = new File(QDIR + "/daemon.alive");
        if (!a.exists) { return false; }
        var age = (new Date().getTime() - a.modified.getTime()) / 1000;
        return age < 12;
    } catch (e) { return false; }
}

// ---------------- main ----------------
try {
    var wd = new Folder(WORK_DIR); if (!wd.exists) { wd.create(); }
    var qd = new Folder(QDIR);     if (!qd.exists) { qd.create(); }

    var exeFile = new File(EXE);
    if (!exeFile.exists) {
        alert("C:\\PS_Fix\\fiximg.exe is missing.\nRun C:\\PS_Fix\\build.bat once.");
        throw new Error("no exe");
    }

    // dialog first, so nothing ever blocks it
    var raw = null;
    try { raw = File.openDialog("Select an image"); } catch (e) { W("dialog: " + e); }
    var files = toFileList(raw);
    W("picked " + files.length);

    if (files.length === 0) {
        W("cancelled");
    } else {

        var src = files[0];
        var srcPath = src.fsName;
        W("source " + srcPath);

        var target = REPAIR_IN_PLACE
                   ? new File(srcPath)
                   : new File(WORK_DIR + "/fx_" + stamp() + ".jpg");

        // keep the untouched original before overwriting
        if (REPAIR_IN_PLACE && KEEP_BACKUP) {
            try {
                var bf = new Folder(BACKUP); if (!bf.exists) { bf.create(); }
                var base = decodeURI(src.name);
                var dot = base.lastIndexOf(".");
                var stem = (dot > 0) ? base.substring(0, dot) : base;
                var ext = (dot > 0) ? base.substring(dot) : ".jpg";
                src.copy(new File(BACKUP + "/" + stem + "_" + stamp() + ext).fsName);
            } catch (e) { W("backup failed: " + e); }
        }

        // the same photo may already be open -> close it or the write is blocked
        var shut = closeIfOpen(target.fsName);
        if (shut > 0) { W("closed " + shut + " open copy"); nap(250); }

        var tag = uid();
        var flag = new File(QDIR + "/" + tag + ".done");
        try { if (flag.exists) { flag.remove(); } } catch (e) { }

        // ------------------------------------------------------------------
        // This script does NOT wait for the result any more.
        //
        // It used to sit in a loop calling $.sleep() until the helper wrote a
        // flag file. $.sleep() blocks Photoshop's own thread, so Photoshop
        // could not answer Windows and went "Not Responding" - a hard freeze
        // for as long as the conversion took. When the helper was already
        // resident that was 200ms and nobody noticed; when it had to start
        // cold, or the photo was large, it was many seconds.
        //
        // Now we hand the job over and return immediately. fiximg.exe opens
        // the repaired picture in Photoshop itself when it is done.
        // ------------------------------------------------------------------

        // Run the converter directly, every time. No queue, no resident helper, no
        // heartbeat to misread.
        //
        // The queue existed only to shave startup time off a wait that Photoshop was
        // blocking on. That wait is gone, so the queue bought nothing and could still
        // strand a job: if the helper had died, the script happily queued work that
        // nobody would ever pick up, and the photo simply never opened. Launching the
        // exe cannot fail that way - it converts the file AND opens it in Photoshop.
        var started = runBat("oneshot.bat",
            'start "" "' + exeFile.fsName + '"'
          + ' -Open'
          + ' -Out "' + target.fsName + '"'
          + ' -Flag "' + flag.fsName + '"'
          + ' "' + srcPath + '"');
        W("launched " + started);

        if (!started) {
            alert("Could not start the converter.\n"
                + "Check that C:\\PS_Fix\\fiximg.exe and C:\\PS_Fix\\tmp exist.");
        }
    }
} catch (err) {
    W("FATAL: " + err + (err.line ? " line " + err.line : ""));
    SHOW_LOG = true;
}

dumpLog();
if (SHOW_LOG) { alert("OPEN ANY\n\n" + LOG.join("\n")); }