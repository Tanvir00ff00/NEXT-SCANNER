#target photoshop
// =============================================================================
// NEXT SCANNER STUDIO — Photoshop File -> Import Native Connector
// Compatible with Photoshop CS6, CC 2014-2020, and Photoshop 2021-2026
// =============================================================================

/*
<javascriptresource>
<name>Next Scanner...</name>
<menu>automate</menu>
<category>Next Scanner</category>
<enableinfo>true</enableinfo>
<eventid>8cba8cd6-cb66-11d1-bc43-0060b0a13dc4</eventid>
</javascriptresource>
*/

function runNextScanner() {
    var possiblePaths = [
        "C:\\Program Files\\NextScanner\\NextScanner.exe",
        Folder.userData.fsName + "\\Programs\\NextScanner\\NextScanner.exe",
        "C:\\PS_Fix\\NextScanner\\bin\\NextScanner.exe",
        "C:\\PS_Fix\\scanhelper.exe"
    ];
    
    var f = null;
    for (var i = 0; i < possiblePaths.length; i++) {
        var testFile = new File(possiblePaths[i]);
        if (testFile.exists) {
            f = testFile;
            break;
        }
    }
    
    if (!f.exists) {
        alert("Next Scanner executable not found!\nPlease run the installer to set up Next Scanner Studio.");
        return;
    }
    
    var handoffFlag = new File(Folder.temp.fsName + "\\nextscan_handoff.txt");
    if (handoffFlag.exists) handoffFlag.remove();
    
    f.execute();
    
    var maxWaitMs = 120000;
    var waited = 0;
    var scannedFile = null;
    
    while (waited < maxWaitMs) {
        $.sleep(500);
        waited += 500;
        
        if (handoffFlag.exists) {
            handoffFlag.open("r");
            var outPath = handoffFlag.read();
            handoffFlag.close();
            handoffFlag.remove();
            
            if (outPath && outPath.length > 0) {
                scannedFile = new File(outPath);
                if (scannedFile.exists) {
                    open(scannedFile);
                    break;
                }
            }
        }
        
        var outListFile = new File("C:\\PS_Fix\\tmp\\scan_output.txt");
        if (outListFile.exists) {
            outListFile.open("r");
            var lines = [];
            while (!outListFile.eof) {
                var l = outListFile.readln();
                if (l && l.length > 0) lines.push(l);
            }
            outListFile.close();
            outListFile.remove();
            
            if (lines.length > 0) {
                for (var j = 0; j < lines.length; j++) {
                    var imgF = new File(lines[j]);
                    if (imgF.exists) open(imgF);
                }
                break;
            }
        }
    }
}

runNextScanner();
