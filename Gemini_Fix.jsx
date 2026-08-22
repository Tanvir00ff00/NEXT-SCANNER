// ============ SETTINGS ============
var EXE      = "C:/PS_Fix/geminifix.exe";
var BUILD    = "C:/PS_Fix/build_gemini.bat";
var WORK_DIR = "C:/PS_Fix/tmp";
var WAIT_SEC = 150;
var SHOW_LOG = false;      // true only when something breaks
// ==================================

var LOG = [];
function W(s) {
    LOG.push((LOG.length + 1) + ") " + s);
    try {
        var lf = new File(WORK_DIR + "/gjsx_log.txt");
        try { lf.encoding = "BINARY"; } catch (e) { }
        if (lf.open("w")) { lf.write(LOG.join("\r\n")); lf.close(); }
    } catch (e) { }
}

function nap(ms) {
    try { $.sleep(ms); return; } catch (e) { }
    var t = new Date().getTime();
    while (new Date().getTime() - t < ms) { }
}

// run anything through a .bat -- the only way that works on CS 8.0 too
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
    } catch (e) { W("runBat " + name + " FAILED: " + e); }
    return ok;
}

function readIni(key, def) {
    try {
        var f = new File("C:/PS_Fix/settings.ini");
        if (!f.exists) { return def; }
        f.open("r");
        var txt = f.read(); f.close();
        var lines = txt.split("\n");
        for (var i = 0; i < lines.length; i++) {
            var ln = lines[i];
            var eq = ln.indexOf("=");
            if (eq < 1) { continue; }
            var k = ln.substring(0, eq).replace(/^\s+|\s+$/g, "");
            if (k === key) { return ln.substring(eq + 1).replace(/^\s+|\s+$/g, ""); }
        }
    } catch (e) { }
    return def;
}

try {
    var wd = new Folder(WORK_DIR);
    if (!wd.exists) { wd.create(); }
    W("PS " + app.version);

    // ---- first run: build the helper once ----
    var exeFile = new File(EXE);
    if (!exeFile.exists) {
        alert("First time setup.\nAbout 20 seconds, only once.");
        try { new File(BUILD).execute(); } catch (e) { W("build launch FAILED: " + e); }
        for (var b = 0; b < 120; b++) {
            exeFile = new File(EXE);
            if (exeFile.exists) { break; }
            nap(500);
        }
        exeFile = new File(EXE);
        if (!exeFile.exists) {
            alert("Setup failed.\nRun C:\\PS_Fix\\build_gemini.bat by hand once.");
            throw new Error("no exe");
        }
    }

    if (app.documents.length === 0) {
        alert("No image is open!");
    } else {

        var doc = app.activeDocument;
        W("doc " + doc.name);

        var stamp = new Date().getTime();
        var inFile  = new File(WORK_DIR + "/gin_"  + stamp + ".jpg");
        var outFile = new File(WORK_DIR + "/gout_" + stamp + ".png");

        // ---- export current image, original stays untouched ----
        var saved = false;
        try {
            var opt = new JPEGSaveOptions();
            try { opt.quality = 12; } catch (e) { }
            doc.saveAs(inFile, opt, true, Extension.LOWERCASE);
            saved = true;
            W("save A ok");
        } catch (e) { W("save A failed: " + e); }

        if (!saved) {
            try { doc.saveAs(inFile, new JPEGSaveOptions(), true); saved = true; W("save B ok"); }
            catch (e) { W("save B failed: " + e); }
        }

        var chk = new File(inFile.fsName);
        W("input on disk: " + chk.exists + " KB " + (chk.exists ? Math.round(chk.length / 1024) : "-"));

        if (!chk.exists || chk.length === 0) {
            alert("Could not export the image.");
        } else {

            var flagFile = new File(WORK_DIR + "/gdone.flag");
            try { if (flagFile.exists) { flagFile.remove(); } } catch (e) { }

            var job = new File(WORK_DIR + "/gjob.txt");
            try { job.encoding = "UTF-8"; } catch (e) { }
            var wrote = false;
            try {
                if (job.open("w")) {
                    job.write("IN=" + inFile.fsName + "\r\nOUT=" + outFile.fsName + "\r\n");
                    job.close();
                    wrote = true;
                }
            } catch (e) { W("job FAILED: " + e); }
            W("job written: " + wrote);

            if (!wrote) {
                alert("Could not write the job file.\nCheck C:\\PS_Fix\\tmp permissions.");
            } else {

                // ---- launch the worker ----
                var fired = false;
                try { fired = new File(EXE).execute(); } catch (e) { W("execute FAILED: " + e); }
                if (!fired) {
                    fired = runBat("grun.bat", 'start "" "' + new File(EXE).fsName + '"');
                    W("bat fallback: " + fired);
                }
                W("launched: " + fired);

                if (!fired) {
                    alert("Could not start the helper.\nRun C:\\PS_Fix\\geminifix.exe by hand once.");
                } else {

                    var notify = readIni("notify", "com");
                    W("notify mode: " + notify);

                    if (notify === "com") {
                        // the exe pushes the result into Photoshop itself.
                        // nothing to wait for -- Photoshop stays free.
                        W("com mode, not waiting");
                    } else {
                        var done = false;
                        for (var k = 0; k < WAIT_SEC * 10; k++) {
                            var fl = new File(WORK_DIR + "/gdone.flag");
                            if (fl.exists) { done = true; break; }
                            nap(100);
                        }
                        W("flag: " + done);

                        var rf = new File(outFile.fsName);
                        W("result: " + rf.exists + " KB " + (rf.exists ? Math.round(rf.length / 1024) : "-"));

                        if (rf.exists && rf.length > 0) {
                            try { app.open(new File(outFile.fsName)); W("opened"); }
                            catch (e) { W("open FAILED: " + e); }
                        } else {
                            alert("Gemini did not return an image.\nSee C:\\PS_Fix\\gemini_log.txt");
                        }
                    }
                }
            }
        }
    }
} catch (err) {
    W("FATAL: " + err + (err.line ? " line " + err.line : ""));
    SHOW_LOG = true;
}

if (SHOW_LOG) { alert("GEMINI FIX\n\n" + LOG.join("\n")); }