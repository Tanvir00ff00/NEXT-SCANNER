// ============ SETTINGS ============
var EXE      = "C:/PS_Fix/gemserver.exe";
var BUILD    = "C:/PS_Fix/build_bridge.bat";
var QUEUE    = "C:/PS_Fix/queue";
var WORK_DIR = "C:/PS_Fix/tmp";
var SHOW_LOG = false;
// ==================================

var LOG = [];
function W(s) {
    LOG.push((LOG.length + 1) + ") " + s);
    try {
        var lf = new File(WORK_DIR + "/gsend_log.txt");
        try { lf.encoding = "BINARY"; } catch (e) { }
        if (lf.open("w")) { lf.write(LOG.join("\r\n")); lf.close(); }
    } catch (e) { }
}

function nap(ms) {
    try { $.sleep(ms); return; } catch (e) { }
    var t = new Date().getTime();
    while (new Date().getTime() - t < ms) { }
}

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

try {
    var wd = new Folder(WORK_DIR);   if (!wd.exists) { wd.create(); }
    var qd = new Folder(QUEUE);      if (!qd.exists) { qd.create(); }
    W("PS " + app.version);

    // never let a colour dialog interrupt the run
    var oldDlg = null;
    try { oldDlg = app.displayDialogs; app.displayDialogs = DialogModes.NO; } catch (e) { }

    var exeFile = new File(EXE);
    if (!exeFile.exists) {
        alert("First time setup.\nAbout 20 seconds, only once.");
        try { new File(BUILD).execute(); } catch (e) { W("build FAILED: " + e); }
        for (var b = 0; b < 120; b++) {
            exeFile = new File(EXE);
            if (exeFile.exists) { break; }
            nap(500);
        }
        exeFile = new File(EXE);
        if (!exeFile.exists) {
            alert("Setup failed.\nRun C:\\PS_Fix\\build_bridge.bat by hand once.");
            throw new Error("no exe");
        }
    }

    runBat("gbridge.bat", 'start "" "' + new File(EXE).fsName + '"');

    if (app.documents.length === 0) {
        alert("No image is open!");
    } else {
        var doc = app.activeDocument;

        // where does this document live on disk?
        var origPath = "";
        try { origPath = doc.fullName.fsName; }
        catch (e) { W("document has never been saved"); }
        W("orig: " + (origPath.length > 0 ? origPath : "(unsaved)"));

        if (origPath.length === 0) {
            alert("Save this image to a file first.\n"
                + "The bridge replaces the file on disk, so it needs a path.");
        } else {
            var stamp = new Date().getTime();
            var img = new File(WORK_DIR + "/gsend_" + stamp + ".jpg");

            var saved = false;
            try {
                var opt = new JPEGSaveOptions();
                try { opt.quality = 12; } catch (e) { }
                doc.saveAs(img, opt, true, Extension.LOWERCASE);   // asCopy, doc untouched
                saved = true;
            } catch (e) { W("save A failed: " + e); }
            if (!saved) {
                try { doc.saveAs(img, new JPEGSaveOptions(), true); saved = true; }
                catch (e) { W("save B failed: " + e); }
            }

            var chk = new File(img.fsName);
            W("exported: " + chk.exists + " KB " + (chk.exists ? Math.round(chk.length / 1024) : "-"));

            if (!chk.exists || chk.length === 0) {
                alert("Could not export the image.");
            } else {
                var uniq = stamp + "_" + Math.floor(Math.random() * 100000);
            var job = new File(QUEUE + "/" + uniq + ".job");
                try { job.encoding = "UTF-8"; } catch (e) { }
                var wrote = false;
                try {
                    if (job.open("w")) {
                        job.write("IMG=" + chk.fsName + "\r\n");
                        job.write("ORIG=" + origPath + "\r\n");
                        job.close();
                        wrote = true;
                    }
                } catch (e) { W("job FAILED: " + e); }
                W("queued: " + wrote);
                if (!wrote) { alert("Could not write the job file."); }
            }
        }
    }

    try { if (oldDlg !== null) app.displayDialogs = oldDlg; } catch (e) { }

} catch (err) {
    W("FATAL: " + err + (err.line ? " line " + err.line : ""));
    SHOW_LOG = true;
}

if (SHOW_LOG) { alert("GEMINI SEND\n\n" + LOG.join("\n")); }
// warn if the open document is only a temporary copy
        var lowPath = origPath.toLowerCase().replace(/\\/g, "/");
        if (lowPath.indexOf("/ps_fix/tmp/") >= 0 || lowPath.indexOf("/ps_fix/out/") >= 0) {
            W("WARNING: this document lives in the PS_Fix temp folder");
            if (!confirm("This image is a temporary copy, not your real file.\n\n"
                       + origPath + "\n\n"
                       + "The restored photo will overwrite that temp file, not the one "
                       + "on your desktop.\n\nCarry on anyway?")) {
                throw new Error("user stopped");
            }
        }