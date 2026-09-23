// =============================================================================
// NextScan - starts the editor for one document and carries messages between
// it and the application.
//
// The application converts the file before this page loads (x2t, natively) and
// serves the result at /work/<id>/Editor.bin. The editor is started in
// ONLYOFFICE's offline mode (document.url = "_offline_"); the shim running in
// the editor's frame hands it these bytes when it asks for its empty document.
//
// Messages to the application are JSON strings over chrome.webview:
//   { type: "ready" }                       the document is on screen
//   { type: "dirty", value: true|false }    the editor's modified flag
//   { type: "saved", path } / { type: "failed", message }
//   { type: "error", message }
// And from it: { type: "save" } / { type: "saveAs", ext }.
//
// Adapted from office-web (wasmtools, AGPL-3.0), which showed the offline
// protocol this relies on; this version uses the native converter instead of
// a WebAssembly one and saves through the application instead of IndexedDB.
// =============================================================================
(function () {
    'use strict';

    var query = new URLSearchParams(location.search);
    var id = query.get('id') || '';
    var ext = (query.get('ext') || 'docx').toLowerCase();
    var title = query.get('title') || 'Document';
    var lang = query.get('lang') || 'en';
    var dark = query.get('theme') === 'dark';

    var editor = null;
    var saving = false;

    function send(message) {
        try { window.chrome.webview.postMessage(JSON.stringify(message)); } catch (e) { }
    }

    function typeOf(e) {
        if (e === 'pdf' || e === 'xps' || e === 'djvu' || e === 'oxps') return 'pdf';
        if (['xlsx', 'xls', 'xlsm', 'xltx', 'ods', 'csv', 'xlsb'].indexOf(e) >= 0) return 'cell';
        if (['pptx', 'ppt', 'ppsx', 'odp', 'pps', 'potx'].indexOf(e) >= 0) return 'slide';
        return 'word';
    }

    function frame() {
        return document.querySelector('iframe[name="frameEditor"]');
    }

    // ---- start ----------------------------------------------------------

    function start(bytes) {
        // Where the shim, in the editor's frame, finds the document.
        window.__ooPendingDocBin = bytes;

        var kind = typeOf(ext);
        var doc = {
            fileType: ext,
            key: 'ns-' + id + '-' + Date.now(),
            title: title,
            url: '_offline_',
            permissions: { edit: true, download: true, print: true, copy: true, review: true, comment: true }
        };
        // A PDF goes straight to the PDF editor: without this api.js first asks
        // a Document Server whether the file is a form, and there is none.
        if (kind === 'pdf') doc.isForm = false;

        editor = new DocsAPI.DocEditor('editor', {
            type: 'desktop',
            width: '100%',
            height: '100%',
            documentType: kind,
            document: doc,
            editorConfig: {
                mode: 'edit',
                lang: lang,
                canCoAuthoring: false,
                coEditing: { mode: 'strict', change: false },
                user: { id: 'nextscan', name: 'NextScan' },
                customization: {
                    // The editor's own About stays: it is where ONLYOFFICE is
                    // credited, as its licence asks (docs/DOCUMENT_WORKSPACE.md).
                    about: true,
                    feedback: false,
                    help: false,
                    autosave: false,
                    forcesave: false,
                    compactHeader: true,
                    toolbarNoTabs: false,
                    hideRightMenu: false,
                    uiTheme: dark ? 'theme-dark' : 'theme-light',
                    features: {
                        spellcheck: { mode: false, change: false },
                        // "New: Multipage view" and the like: tips about what
                        // changed in ONLYOFFICE's latest release, shown to
                        // someone who never used the one before it.
                        featuresTips: false
                    }
                }
            },
            events: {
                onDocumentReady: function () {
                    // If the editor finished without ever asking the shim for
                    // its document, it opened a blank one: give it ours.
                    if (!window.__nsDocumentTaken && editor && editor.openDocument) {
                        window.__nsDocumentTaken = true;
                        editor.openDocument({ buffer: bytes.slice().buffer });
                    }
                    send({ type: 'ready' });
                },
                onDocumentStateChange: function (event) {
                    send({ type: 'dirty', value: !!(event && event.data) });
                },
                onError: function (event) {
                    var data = event && event.data;
                    send({ type: 'error', message: data ? (data.errorDescription || ('code ' + data.errorCode)) : 'unknown' });
                },
                onRequestSave: function () { save(ext); },
                onDownloadAs: function () { }
            }
        });
    }

    // ---- save -----------------------------------------------------------

    // Asks the editor for its document and hands the bytes to the
    // application, which converts them back with x2t and writes the file.
    function save(target) {
        if (saving) return;
        var f = frame();
        var w = f && f.contentWindow;
        if (!w || typeof w.__ooGetFileData !== 'function') {
            send({ type: 'failed', message: 'The editor is not ready yet.' });
            return;
        }

        var data;
        try { data = w.__ooGetFileData(); } catch (e) { data = null; }
        if (!data || !data.length) {
            send({ type: 'failed', message: 'The editor gave back no document.' });
            return;
        }

        var body = data;
        var url = '/save/' + encodeURIComponent(id) + '?ext=' + encodeURIComponent(target || ext);

        // A PDF is saved as its base plus the changes made to it, which x2t
        // merges; everything else is the whole document in the editor's format.
        if (typeOf(ext) === 'pdf' && typeof w.__ooGetPdfChanges === 'function') {
            var changes = null;
            try { changes = w.__ooGetPdfChanges(); } catch (e) { }
            if (changes && changes.length) {
                var joined = new Uint8Array(8 + data.length + changes.length);
                new DataView(joined.buffer).setUint32(0, data.length, true);
                new DataView(joined.buffer).setUint32(4, changes.length, true);
                joined.set(data, 8);
                joined.set(changes, 8 + data.length);
                body = joined;
                url += '&pdfchanges=1';
            }
        }

        saving = true;
        fetch(url, { method: 'POST', body: body })
            .then(function (r) { return r.json(); })
            .then(function (answer) {
                saving = false;
                if (answer && answer.ok) {
                    send({ type: 'saved', path: answer.path || '' });
                    markSaved(w);
                } else {
                    send({ type: 'failed', message: (answer && answer.message) || 'The file could not be written.' });
                }
            })
            .catch(function (e) {
                saving = false;
                send({ type: 'failed', message: String(e && e.message || e) });
            });
    }

    // Tells the editor its changes are kept, so its own modified flag and our
    // tab's dot clear together.
    function markSaved(w) {
        try {
            var api = w.Asc && w.Asc.editor;
            if (api && api.asc_setDocumentModified) api.asc_setDocumentModified(false);
            else if (api && api.SetDocumentModified) api.SetDocumentModified(false);
        } catch (e) { }
        send({ type: 'dirty', value: false });
    }

    window.addEventListener('message', function (e) {
        var d = e.data;
        if (d && d.type === 'oo-save-request') save(ext);
    });

    try {
        window.chrome.webview.addEventListener('message', function (e) {
            var m = e.data;
            if (typeof m === 'string') { try { m = JSON.parse(m); } catch (x) { return; } }
            if (!m) return;
            if (m.type === 'save') save(ext);
            else if (m.type === 'saveAs') save(m.ext || ext);
        });
    } catch (e) { }

    // ---- go -------------------------------------------------------------

    var source = typeOf(ext) === 'pdf' ? '/work/' + id + '/source.pdf' : '/work/' + id + '/Editor.bin';
    fetch(source)
        .then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.arrayBuffer();
        })
        .then(function (buffer) { start(new Uint8Array(buffer)); })
        .catch(function (e) { send({ type: 'error', message: 'Could not read the converted document: ' + e.message }); });
})();
