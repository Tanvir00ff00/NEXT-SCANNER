// =============================================================================
// NextScan - lets ONLYOFFICE's editors run with no Document Server behind them.
//
// Injected by the application into every frame before the page's own scripts
// (WebView2's AddScriptToExecuteOnDocumentCreated), and does nothing outside
// the four editor apps. What a server would otherwise provide:
//
//   the document     getEmpty() returns the bytes host.js fetched, instead of
//                    a blank document
//   permissions      the offline licence answer, so the editor opens for edit
//   saving           Ctrl+S and the save button ask the host page; the host
//                    asks us for the bytes (__ooGetFileData)
//   images           inserted from a file or a URL, resolved here as data:
//                    URLs instead of being uploaded to a server
//   chrome           buttons for server-only features (chat, co-editing,
//                    sharing) are hidden, and so is the vendor mark in the
//                    header: ONLYOFFICE is credited in About, as its licence
//                    asks since 9.4 (docs/DOCUMENT_WORKSPACE.md)
//
// Adapted from office-web's offline-shim.js (wasmtools, AGPL-3.0), which
// worked out which of the editor's internals a server normally answers.
// =============================================================================
(function () {
    'use strict';

    var path = location.pathname || '';
    var app = /\/apps\/(documenteditor|spreadsheeteditor|presentationeditor|pdfeditor|visioeditor)\/main\//.exec(path);
    if (!app) return;
    var isPdf = app[1] === 'pdfeditor';

    function every(ms, limit, attempt) {
        var tries = 0;
        var timer = setInterval(function () {
            tries++;
            var done = false;
            try { done = attempt(); } catch (e) { }
            if (done || tries > limit) clearInterval(timer);
        }, ms);
    }

    function editorApi() {
        return (window.Asc && window.Asc.editor) || window.editor || null;
    }

    // ---- the document -------------------------------------------------
    function pendingBytes() {
        try {
            var p = window.parent && window.parent !== window ? window.parent.__ooPendingDocBin : null;
            if (p && p.length) return p.slice();
        } catch (e) { }
        return null;
    }

    // Re-applied whenever the editor's own getEmpty replaces ours: the SDK
    // may define it after the object is first assigned, and a flag on the
    // object would then say "hooked" about a function that no longer is.
    function hookGetEmpty(common) {
        if (!common || (common.getEmpty && common.getEmpty.__ns)) return;
        var original = common.getEmpty;
        var ours = function () {
            var bytes = pendingBytes();
            if (bytes) {
                try { window.parent.__nsDocumentTaken = true; } catch (e) { }
                return bytes;
            }
            return original ? original.apply(this, arguments) : new Uint8Array(0);
        };
        ours.__ns = true;
        common.getEmpty = ours;
    }

    // The editor asks a Document Server whether the document was saved by
    // the same version it is; with no server the answer never comes and it
    // tells the operator the editor "has been updated" and reloads, forever.
    window.compareVersions = true;

    // ---- permissions --------------------------------------------------
    function applyLicence(common, asc) {
        if (!common || !asc || !common.baseEditorsApi || !common.baseEditorsApi.prototype) return false;
        var proto = common.baseEditorsApi.prototype;
        if (proto.__nsLicence) return true;
        proto._onEndPermissions = function () {
            if (this.isOnLoadLicense) {
                var result = new common.asc_CAscEditorPermissions();
                result.setLicenseType(asc.c_oLicenseResult.Success);
                result.setCanBranding(true);
                result.setCustomization(true);
                result.setRights(asc.c_oRights.Edit);
                this.sendEvent('asc_onGetEditorPermissions', result);
            }
        };
        proto.__nsLicence = true;
        return true;
    }

    var common_;
    try {
        Object.defineProperty(window, 'AscCommon', {
            get: function () { return common_; },
            set: function (v) {
                common_ = v;
                hookGetEmpty(v);
                if (window.Asc) applyLicence(v, window.Asc);
            },
            configurable: true
        });
    } catch (e) { }

    // Kept up for a minute rather than stopping at the first success, for
    // the reason above.
    every(50, 1200, function () {
        if (window.AscCommon) hookGetEmpty(window.AscCommon);
        if (window.AscCommon && window.Asc) applyLicence(window.AscCommon, window.Asc);
        var ed = editorApi();
        return !!(ed && ed.isDocumentLoadComplete);
    });

    // ---- saving -------------------------------------------------------
    function askToSave() {
        try { window.parent.postMessage({ type: 'oo-save-request' }, '*'); } catch (e) { }
    }

    window.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && (e.key === 's' || e.key === 'S' || e.keyCode === 83)) {
            e.preventDefault();
            e.stopPropagation();
            askToSave();
        }
    }, true);

    document.addEventListener('click', function (e) {
        var t = e.target;
        if (t && t.closest && t.closest('#slot-btn-dt-save, #fm-btn-save')) {
            e.preventDefault();
            e.stopPropagation();
            askToSave();
        }
    }, true);

    // The editor greys its save button while it believes a server holds the
    // latest copy. Here nothing does, so it is always offered.
    setInterval(function () {
        var slots = document.querySelectorAll('#slot-btn-dt-save, #fm-btn-save');
        for (var i = 0; i < slots.length; i++) {
            var b = slots[i].querySelector('button, .btn') || slots[i];
            b.classList.remove('disabled');
            b.removeAttribute('disabled');
        }
    }, 700);

    // The whole document in the editor's own format, which the application
    // converts back into the file's format.
    window.__ooGetFileData = function () {
        var ed = editorApi();
        if (!ed) return null;

        if (isPdf) {
            try {
                var dr = ed.DocumentRenderer;
                var base = dr && dr.file && dr.file.getFileBinary();
                return base && base.length ? base : null;
            } catch (e) { return null; }
        }

        if (typeof ed.asc_nativeGetFileData !== 'function') return null;
        var before = window.native;
        var length = -1;
        window.native = {
            Save_End: function (header, len) { length = len; },
            Save_Begin: function () { }
        };
        var data;
        try { data = ed.asc_nativeGetFileData(); }
        finally {
            if (before === undefined) delete window.native; else window.native = before;
        }
        if (!data) return null;
        if (length >= 0 && length < data.length) data = data.subarray(0, length);
        return data;
    };

    // A PDF is saved as the original plus a stream of the changes made to
    // it; x2t merges the two (the same path a Document Server takes).
    window.__ooGetPdfChanges = function () {
        var ed = editorApi();
        if (!ed || !ed.DocumentRenderer) return null;
        try {
            var changes = ed.DocumentRenderer.Save();
            return changes && changes.length ? changes : null;
        } catch (e) { return null; }
    };

    // ---- the assistant's hands ------------------------------------------
    // Runs code against ONLYOFFICE's document API (Api.GetDocument(), and
    // Api.GetActiveSheet() in a workbook -- the API its plugins and Document
    // Builder use), the way its own plugin runner does (sdkjs common/plugins.js
    // callCommandInternal): before, the code, after, then the end-of-script
    // work that loads new fonts and redraws. Returns a Promise of whatever the
    // code returns, made plain by JSON.
    // The builders are layered -- AscBuilder.Word.Api, then Slide on top of
    // it, then Cell on top of that -- and AscBuilder.Api is the Word one in
    // every editor. The one that belongs to this editor is the one with its
    // entry point: GetSheets, GetPresentation or GetDocument.
    function builder() {
        var b = window.AscBuilder;
        if (!b) return null;
        var candidates = [b.Cell && b.Cell.Api, b.Slide && b.Slide.Api, b.Word && b.Word.Api, b.Api];
        var wanted = app[1] === 'spreadsheeteditor' ? 'GetSheets'
                   : app[1] === 'presentationeditor' ? 'GetPresentation' : 'GetDocument';
        for (var i = 0; i < candidates.length; i++)
            if (candidates[i] && typeof candidates[i][wanted] === 'function') return candidates[i];
        return b.Api || null;
    }

    window.__nsRun = function (code, recalculate) {
        return new Promise(function (resolve, reject) {
            var api = editorApi();
            var Api = builder();
            if (!api || !Api) { reject(new Error('The document is not ready.')); return; }
            if (api.isLongAction && api.isLongAction()) { reject(new Error('The editor is busy; try again in a moment.')); return; }
            if (api.canRunBuilderScript && !api.canRunBuilderScript()) { reject(new Error('The document cannot be changed now (read-only or locked).')); return; }

            var result, failure = null;
            api._beforeEvalCommand();
            try { result = (new Function('Api', code))(Api); }
            catch (e) { failure = e; }

            function finish() {
                api.evalCommand = false;
                var done = function () {
                    if (failure) { reject(failure); return; }
                    try { resolve(result === undefined ? null : JSON.parse(JSON.stringify(result))); }
                    catch (e) { resolve(String(result)); }
                };
                if (api.onEndBuilderScript) api.onEndBuilderScript(done); else done();
            }

            var history = window.AscCommon && window.AscCommon.History;
            if (recalculate !== false && !failure && history && history.Is_LastPointEmpty && !history.Is_LastPointEmpty())
                api._afterEvalCommand(finish);
            else
                finish();
        });
    };

    // ---- Print and Download As: the answer --------------------------------
    // saveWithParts sends the document and, on the last part's "ok", does
    // nothing unless it was given a callback: a Document Server delivers the
    // finished file's URL later, over its websocket. With no server that
    // message never comes, the editor waits forever, and nothing after it
    // can start. So the last "ok" (which carries the URL, see DocView.Server)
    // goes straight to the editor's own completion handler.
    every(100, 600, function () {
        var common = window.AscCommon;
        if (!common || !common.saveWithParts || common.saveWithParts.__ns) return false;
        var original = common.saveWithParts;
        var ours = function (fSendCommand, fCallback, fCallbackRequest, data, container) {
            var request = fCallbackRequest || function (income, status) { if (fCallback) fCallback(income, status); };
            return original.call(this, fSendCommand, fCallback, request, data, container);
        };
        ours.__ns = true;
        common.saveWithParts = ours;
        return true;
    });

    // ---- Print ---------------------------------------------------------
    // Online, the finished PDF is printed from a hidden frame. Here the
    // application prints it, with Windows' own print dialog.
    every(100, 600, function () {
        var common = window.AscCommon;
        var proto = common && common.baseEditorsApi && common.baseEditorsApi.prototype;
        if (!proto || !proto.processSavedFile || proto.processSavedFile.__ns) return false;
        var original = proto.processSavedFile;
        var ours = function (url, downloadType, fileType) {
            if (downloadType === 'asc_onPrintUrl') {
                try { window.parent.postMessage({ type: 'oo-print', url: String(url) }, '*'); } catch (e) { }
                return;
            }
            return original.apply(this, arguments);
        };
        ours.__ns = true;
        proto.processSavedFile = ours;
        return true;
    });

    // ---- Download As ----------------------------------------------------
    // The converted file comes back as a URL the editor downloads through a
    // hidden helper of its own, which a WebView2 never reports as a download.
    // It is handed to the application instead, which asks where to put it.
    every(100, 600, function () {
        var common = window.AscCommon;
        if (!common || !common.getFile || common.getFile.__ns) return false;
        var ours = function (url) {
            try { window.parent.postMessage({ type: 'oo-download', url: String(url) }, '*'); } catch (e) { }
        };
        ours.__ns = true;
        common.getFile = ours;
        return true;
    });

    // ---- images -------------------------------------------------------
    function toDataUrl(blob) {
        return new Promise(function (resolve, reject) {
            var reader = new FileReader();
            reader.onload = function () { resolve(reader.result); };
            reader.onerror = function () { reject(reader.error); };
            reader.readAsDataURL(blob);
        });
    }

    every(100, 600, function () {
        var common = window.AscCommon;
        if (!common || !common.sendImgUrls || common.__nsImgUrls) return false;
        common.sendImgUrls = function (api, images, callback) {
            var list = images || [];
            Promise.all(list.map(function (u) {
                if (typeof u !== 'string') return 'error';
                if (u.indexOf('data:') === 0 || u.indexOf('blob:') === 0) return u;
                if (u.indexOf('http://') === 0 || u.indexOf('https://') === 0) {
                    return fetch(u).then(function (r) { return r.blob(); }).then(toDataUrl).catch(function () { return 'error'; });
                }
                try {
                    var mapped = common.g_oDocumentUrls && common.g_oDocumentUrls.getImageUrl(u);
                    if (mapped) return mapped;
                } catch (e) { }
                return 'error';
            })).then(function (resolved) {
                callback(resolved.map(function (u) { return { url: u, path: u }; }));
            });
        };
        common.__nsImgUrls = true;
        return true;
    });

    every(100, 600, function () {
        var common = window.AscCommon;
        if (!common || !common.UploadImageFiles || common.__nsUpload) return false;
        common.UploadImageFiles = function (files, documentId, userId, jwt, shardKey, wopi, session, callback) {
            var errors = (window.Asc && window.Asc.c_oAscError && window.Asc.c_oAscError.ID) || {};
            var list = [];
            for (var i = 0; files && i < files.length; i++) list.push(files[i]);
            if (!list.length) { callback(errors.UplImageFileCount !== undefined ? errors.UplImageFileCount : -15); return; }
            Promise.all(list.map(toDataUrl)).then(function (urls) {
                var urlsTable = common.g_oDocumentUrls;
                var named = {};
                for (var k = 0; k < urls.length; k++) {
                    var n = (urlsTable && urlsTable.imageCount ? urlsTable.imageCount : 0) + k + 1;
                    var ext = /^data:image\/(\w+)/.exec(urls[k]);
                    named['media/nsimage' + Date.now() + '_' + n + '.' + (ext ? ext[1].replace('jpeg', 'jpg') : 'png')] = urls[k];
                }
                if (urlsTable && urlsTable.addUrls) urlsTable.addUrls(named);
                callback(errors.No !== undefined ? errors.No : 0, urls);
            }).catch(function () {
                callback(errors.UplImageUrl !== undefined ? errors.UplImageUrl : -13);
            });
        };
        common.__nsUpload = true;
        return true;
    });

    // ---- chrome -------------------------------------------------------
    function hideServerOnly() {
        if (!document.head) return false;
        var style = document.createElement('style');
        style.textContent =
            '#header-logo, .brand-logo,' +
            '#slot-btn-chat, #left-btn-chat,' +
            '#slot-btn-coauthmode, #slot-btn-share,' +
            '#fm-btn-suggest, #fm-btn-back, #fm-btn-create, #fm-btn-recent,' +
            '[data-layout-name="header-users"], [data-layout-name="header-user"]' +
            '{ display: none !important; }';
        document.head.appendChild(style);
        return true;
    }
    if (!hideServerOnly()) document.addEventListener('DOMContentLoaded', hideServerOnly);

    // No "leave this page?" prompt: the application asks about unsaved
    // changes itself, once, in its own words.
    try {
        var real = null;
        Object.defineProperty(window, 'onbeforeunload', {
            get: function () { return real; },
            set: function (fn) {
                real = function (e) { try { if (fn) fn.call(window, e); } catch (x) { } return undefined; };
            },
            configurable: true
        });
    } catch (e) { }
})();
