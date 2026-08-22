// ============================================================================
// Photoshop Scanner Bridge - Instant Zero-Latency ExtendScript
// ============================================================================

var EXE_PATH   = "C:/PS_Fix/scanhelper.exe";
var BUILD_PATH = "C:/PS_Fix/build_scan.bat";
var WORK_DIR   = "C:/PS_Fix/tmp";
var INI_PATH   = "C:/PS_Fix/scan.ini";
var STAT_PATH  = "C:/PS_Fix/tmp/scan_status.txt";
var OUT_LIST   = "C:/PS_Fix/tmp/scan_output.txt";

function s2t(s) { return stringIDToTypeID(s); }

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
    } catch (e) { }
    return ok;
}

function loadIniSettings() {
    var settings = {};
    try {
        var f = new File(INI_PATH);
        if (f.exists && f.open("r")) {
            while (!f.eof) {
                var line = f.readln();
                if (!line || line.indexOf("#") === 0) continue;
                var eq = line.indexOf("=");
                if (eq > 0) {
                    var k = line.substring(0, eq).replace(/^\s+|\s+$/g, "").toLowerCase();
                    var v = line.substring(eq + 1).replace(/^\s+|\s+$/g, "");
                    settings[k] = v;
                }
            }
            f.close();
        }
    } catch (e) { }
    return settings;
}

function getDescriptorString(desc, key, defaultVal) {
    try {
        var k = s2t(key);
        if (desc && desc.hasKey(k)) {
            return desc.getString(k);
        }
    } catch (e) { }
    return defaultVal;
}

function main() {
    try {
        var wd = new Folder(WORK_DIR);
        if (!wd.exists) { wd.create(); }

        var exeFile = new File(EXE_PATH);
        if (!exeFile.exists) {
            alert("Scanner bridge executable is missing.\nPlease run C:\\PS_Fix\\build_scan.bat once.");
            try { new File(BUILD_PATH).execute(); } catch (e) { }
            return;
        }

        // Clean up prior output and status files
        try {
            var sf = new File(STAT_PATH);
            if (sf.exists) sf.remove();
            var of = new File(OUT_LIST);
            if (of.exists) of.remove();
        } catch (e) { }

        // Check if called from recorded Photoshop Action
        var hasActionParams = false;
        var actionParams = null;
        try {
            if (typeof(app.playbackParameters) !== "undefined" && app.playbackParameters.count > 0) {
                actionParams = app.playbackParameters;
                hasActionParams = true;
            }
        } catch (e) { }

        var isSilentAction = false;
        try {
            if (typeof(app.playbackDisplayDialogs) !== "undefined" && app.playbackDisplayDialogs == DialogModes.NO) {
                isSilentAction = true;
            }
        } catch (e) { }

        // Build command line with -jsx flag to indicate Photoshop is directly waiting to open files
        var cmd = 'start "" "' + exeFile.fsName + '" -jsx';

        if (hasActionParams && actionParams) {
            var device   = getDescriptorString(actionParams, "device", "");
            var driver   = getDescriptorString(actionParams, "driver", "");
            var dpi      = getDescriptorString(actionParams, "dpi", "");
            var pagesize = getDescriptorString(actionParams, "pagesize", "");
            var bitdepth = getDescriptorString(actionParams, "bitdepth", "");
            var source   = getDescriptorString(actionParams, "source", "");
            var format   = getDescriptorString(actionParams, "format", "");
            var nativeui = getDescriptorString(actionParams, "nativeui", "");
            var deskew   = getDescriptorString(actionParams, "deskew", "");

            if (device)   cmd += ' -device "' + device + '"';
            if (driver)   cmd += ' -driver "' + driver + '"';
            if (dpi)      cmd += ' -dpi "' + dpi + '"';
            if (pagesize) cmd += ' -pagesize "' + pagesize + '"';
            if (bitdepth) cmd += ' -bitdepth "' + bitdepth + '"';
            if (source)   cmd += ' -source "' + source + '"';
            if (format)   cmd += ' -format "' + format + '"';
            if (nativeui) cmd += ' -nativeui "' + nativeui + '"';
            if (deskew)   cmd += ' -deskew "' + deskew + '"';

            if (isSilentAction) {
                cmd += ' -nodialog';
            }
        }

        // Launch helper
        if (!runBat("run_scan.bat", cmd)) {
            alert("Could not launch scanner bridge.");
            return;
        }

        // Wait for scan to complete or cancel
        var maxWait = 3000; // 300s (5 mins)
        var statFile = new File(STAT_PATH);
        while (maxWait > 0 && !statFile.exists) {
            nap(80);
            maxWait--;
        }

        var statText = "";
        if (statFile.exists) {
            try {
                if (statFile.open("r")) {
                    statText = statFile.readln();
                    statFile.close();
                }
            } catch (e) { }
        }

        // Instantly open scanned images inside Photoshop with zero latency!
        if (statText === "DONE") {
            var outListFile = new File(OUT_LIST);
            if (outListFile.exists && outListFile.open("r")) {
                while (!outListFile.eof) {
                    var imgPath = outListFile.readln().replace(/^\s+|\s+$/g, "");
                    if (imgPath.length > 0) {
                        var imgFile = new File(imgPath);
                        if (imgFile.exists) {
                            try {
                                app.displayDialogs = DialogModes.NO;
                                app.open(imgFile);
                            } catch (e) { }
                        }
                    }
                }
                outListFile.close();
            }
        }

        // Record Action parameters
        var currentSettings = loadIniSettings();
        var outDesc = new ActionDescriptor();
        outDesc.putString(s2t("device"),   currentSettings["device"]   || "");
        outDesc.putString(s2t("driver"),   currentSettings["driver"]   || "twain");
        outDesc.putString(s2t("dpi"),      currentSettings["dpi"]      || "300");
        outDesc.putString(s2t("pagesize"), currentSettings["pagesize"] || "a4");
        outDesc.putString(s2t("bitdepth"), currentSettings["bitdepth"] || "color");
        outDesc.putString(s2t("source"),   currentSettings["source"]   || "glass");
        outDesc.putString(s2t("format"),   currentSettings["format"]   || "jpg");
        outDesc.putString(s2t("nativeui"), currentSettings["nativeui"] || "off");
        outDesc.putString(s2t("deskew"),   currentSettings["deskew"]   || "off");

        app.playbackParameters = outDesc;

        // Clean up
        try { if (statFile.exists) statFile.remove(); } catch (e) { }

    } catch (err) { }
}

main();