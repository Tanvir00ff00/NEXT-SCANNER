// Reads a Word document for the assistant: each block in order, with its
// style and the fonts its text is set in, and tables as rows of cells.
// The fonts matter here more than anywhere: a paragraph set in SutonnyMJ or
// another Bijoy font holds ANSI letters that only look Bengali in that font,
// and read as text they are nonsense unless the reader knows.
//
// With args.detail = 'layout' it also says how each block is set -- page size
// and margins, alignment, spacing, indents, tab stops, each run's font, size,
// weight and colour, table column widths, merged cells, borders and fills --
// in the same words and units write_document takes (mm, pt, '#RRGGBB'), so
// what is read can be changed and written back without translating anything.
// Only formatting set on the block itself is listed; what is not listed comes
// from the document's defaults, which are given once at the top.
// Arguments: args.from, args.to (block indexes, optional), args.detail.
var doc = Api.GetDocument();
var count = doc.GetElementsCount();
var from = Math.max(0, args.from || 0);
var to = Math.min(count - 1, args.to === undefined || args.to === null ? count - 1 : args.to);
var layout = args.detail === 'layout';

function mm(twips) { return Math.round((+twips || 0) * 25.4 / 1440 * 10) / 10; }
function pt(twips) { return Math.round((+twips || 0) / 20 * 10) / 10; }
function hex(c) {
    if (!c || c.auto) return null;
    function h(v) { v = Math.max(0, Math.min(255, v | 0)).toString(16).toUpperCase(); return v.length < 2 ? '0' + v : v; }
    return '#' + h(c.r) + h(c.g) + h(c.b);
}

function fontsOf(paragraph) {
    var seen = {};
    try {
        for (var r = 0; r < paragraph.GetElementsCount(); r++) {
            var el = paragraph.GetElement(r);
            if (!el || !el.GetClassType || el.GetClassType() !== 'run') continue;
            var name = null;
            try { name = el.GetFontFamily ? el.GetFontFamily() : null; } catch (e) { }
            if (!name) try { var pr = el.GetTextPr(); name = pr && pr.GetFontFamily ? pr.GetFontFamily() : null; } catch (e) { }
            if (name) seen[name] = true;
        }
    } catch (e) { }
    return Object.keys(seen);
}

function textOf(content) {
    var parts = [];
    try {
        for (var i = 0; i < content.GetElementsCount(); i++) {
            var e = content.GetElement(i);
            if (e && e.GetText) parts.push(e.GetText().replace(/[\r\n\t]+$/, ''));
        }
    } catch (e) { }
    return parts.join('\n');
}

// ---- formatting, from the editor's own JSON of a block ----------------------
function runFormat(rPr) {
    var f = {};
    if (!rPr) return f;
    if (rPr.rFonts) { var name = rPr.rFonts.ascii || rPr.rFonts.hAnsi || rPr.rFonts.cs; if (name) f.font = typeof name === 'string' ? name : (name.name || name.Name); }
    if (rPr.sz) f.size = rPr.sz / 2;
    if (rPr.b === true) f.bold = true;
    if (rPr.i === true) f.italic = true;
    if (rPr.u && rPr.u !== 'none' && rPr.u !== false) f.underline = true;
    if (rPr.strike === true) f.strike = true;
    if (rPr.caps === true) f.caps = true;
    if (rPr.smallCaps === true) f.smallCaps = true;
    if (rPr.spacing) f.spacing = pt(rPr.spacing);
    var c = hex(rPr.color);
    if (c && c !== '#000000') f.color = c;
    if (rPr.vertAlign && rPr.vertAlign !== 'baseline') f.vertAlign = rPr.vertAlign;
    if (rPr.highlight && rPr.highlight !== 'none') f.highlight = rPr.highlight;
    return f;
}

function runText(content) {
    var s = '';
    if (!content) return s;
    for (var i = 0; i < content.length; i++) {
        var x = content[i];
        if (typeof x === 'string') s += x;
        else if (x && (x.type === 'tab' || x.type === 'tabChar')) s += '\t';
        else if (x && (x.type === 'break' || x.type === 'lineBreak')) s += (x.breakType === 'page' ? '[page break]' : '\n');
        else if (x && x.type === 'drawing') s += '[picture]';
    }
    return s;
}

function same(a, b) { return JSON.stringify(a) === JSON.stringify(b); }

function border(b) {
    if (!b || !b.value || b.value === 'none' || b.value === 'nil') return 'none';
    var o = { size: Math.round((b.sz || 4) / 8 * 100) / 100 };
    if (b.value !== 'single') o.style = b.value;
    var c = hex(b.color);
    if (c && c !== '#000000') o.color = c;
    return o;
}

function paraLayout(j, into) {
    var p = j.pPr || {};
    if (p.pStyle) into.style = p.pStyle;
    if (p.jc) into.align = p.jc === 'both' ? 'justify' : p.jc;
    if (p.spacing) {
        if (p.spacing.before !== undefined) into.before = pt(p.spacing.before);
        if (p.spacing.after !== undefined) into.after = pt(p.spacing.after);
        if (p.spacing.line !== undefined) {
            var rule = p.spacing.lineRule || 'auto';
            into.line = rule === 'auto' ? Math.round(p.spacing.line / 240 * 100) / 100 : (rule === 'exact' ? { exact: pt(p.spacing.line) } : { atLeast: pt(p.spacing.line) });
        }
    }
    if (p.ind) {
        var ind = {};
        if (p.ind.left) ind.left = mm(p.ind.left);
        if (p.ind.right) ind.right = mm(p.ind.right);
        if (p.ind.firstLine) ind.first = mm(p.ind.firstLine);
        if (p.ind.hanging) ind.hanging = mm(p.ind.hanging);
        if (Object.keys(ind).length) into.indent = ind;
    }
    if (p.tabs && p.tabs.length) {
        into.tabs = [];
        for (var t = 0; t < p.tabs.length; t++) into.tabs.push({ pos: mm(p.tabs[t].pos), align: p.tabs[t].val });
    }
    if (p.pBdr) {
        var bd = {};
        ['top', 'bottom', 'left', 'right'].forEach(function (s) { if (p.pBdr[s] && p.pBdr[s].value && p.pBdr[s].value !== 'none') bd[s] = border(p.pBdr[s]); });
        if (Object.keys(bd).length) into.border = bd;
    }
    if (p.shd && p.shd.color) { var f = hex(p.shd.color); if (f && f !== '#FFFFFF') into.fill = f; }

    // Runs, joined while they look the same.
    var runs = [];
    var content = j.content || [];
    for (var i = 0; i < content.length; i++) {
        var r = content[i];
        if (!r || (r.type !== 'run' && r.type !== 'hyperlink')) continue;
        var pieces = r.type === 'hyperlink' ? (r.content || []) : [r];
        for (var k = 0; k < pieces.length; k++) {
            var text = runText(pieces[k].content);
            if (!text.length) continue;
            var fmt = runFormat(pieces[k].rPr);
            var last = runs.length ? runs[runs.length - 1] : null;
            if (last && same(last.f, fmt)) last.text += text;
            else runs.push({ text: text, f: fmt });
        }
    }
    if (runs.length === 1) {
        for (var key in runs[0].f) into[key] = runs[0].f[key];
    } else if (runs.length > 1) {
        into.runs = runs.map(function (x) { var o = { text: x.text }; for (var key2 in x.f) o[key2] = x.f[key2]; return o; });
        delete into.text;
    }
    return into;
}

function tableLayout(t, into) {
    var j = JSON.parse(t.ToJSON());
    var grid = (j.tblGrid || []).map(function (g) { return mm(g.w); });
    into.columns = grid;
    var pr = j.tblPr || {};
    if (pr.tblBorders) {
        var b = {};
        ['top', 'bottom', 'left', 'right', 'insideH', 'insideV'].forEach(function (s) {
            var src = pr.tblBorders[s] || pr.tblBorders[s === 'left' ? 'start' : s === 'right' ? 'end' : s];
            b[s] = src ? border(src) : 'none';
        });
        into.borders = b;
    }
    if (pr.jc) into.align = pr.jc;
    if (pr.tblInd && pr.tblInd.w) into.indent = mm(pr.tblInd.w);
    into.rows = [];
    var rows = j.content || [];
    for (var r = 0; r < rows.length; r++) {
        var row = rows[r], out = { cells: [] };
        if (row.trPr && row.trPr.trHeight && row.trPr.trHeight.val) {
            out.height = mm(row.trPr.trHeight.val);
            if (row.trPr.trHeight.hRule === 'exact') out.exact = true;
        }
        var cells = row.content || [];
        for (var c = 0; c < cells.length; c++) {
            var cell = cells[c], cp = cell.tcPr || {}, o = {};
            if (cp.gridSpan && cp.gridSpan > 1) o.span = cp.gridSpan;
            if (cp.vMerge === 'continue' || cp.vMerge === 2) { out.cells.push({ merged_from_above: true }); continue; }
            if (cp.vMerge === 'restart' || cp.vMerge === 1) o.rowspan_starts_here = true;
            var paras = (cell.content && cell.content.content) || [];
            var ps = [];
            for (var q = 0; q < paras.length; q++) {
                if (paras[q].type !== 'paragraph') continue;
                ps.push(paraLayout(paras[q], { text: '' }));
            }
            // A cell with one paragraph is written as that paragraph.
            if (ps.length === 1) { for (var key in ps[0]) o[key] = ps[0][key]; if (!o.runs && o.text === undefined) o.text = ''; }
            else o.paragraphs = ps;
            if (o.text !== undefined && !o.runs) {
                var tt = '';
                for (var z = 0; z < paras.length; z++) if (paras[z].content) for (var zz = 0; zz < paras[z].content.length; zz++) tt += runText((paras[z].content[zz] || {}).content);
                o.text = tt;
            }
            if (cp.vAlign && cp.vAlign !== 'top') o.valign = cp.vAlign;
            if (cp.shd && cp.shd.color) { var f = hex(cp.shd.color); if (f && f !== '#FFFFFF') o.fill = f; }
            if (cp.tcBorders) {
                var cb = {};
                ['top', 'bottom', 'left', 'right'].forEach(function (s) {
                    var src = cp.tcBorders[s] || cp.tcBorders[s === 'left' ? 'start' : s === 'right' ? 'end' : s];
                    if (src) cb[s] = border(src);
                });
                if (Object.keys(cb).length) o.border = cb;
            }
            out.cells.push(o);
        }
        into.rows.push(out);
    }
    return into;
}

var blocks = [];
for (var i = from; i <= to; i++) {
    var e = doc.GetElement(i);
    var type = e.GetClassType();
    if (type === 'paragraph') {
        var style = '';
        try { var s = e.GetStyle(); style = s ? s.GetName() : ''; } catch (x) { }
        var b = { index: i, type: 'paragraph', text: e.GetText().replace(/\r?\n$/, '') };
        if (layout) {
            try { paraLayout(JSON.parse(e.ToJSON()), b); } catch (x) { b.layout_error = String(x); }
            if (style && style !== 'Normal' && !b.style) b.style = style;
            try { if (e.GetAllDrawingObjects().length) b.pictures = e.GetAllDrawingObjects().length; } catch (x) { }
        } else {
            b.style = style;
            b.fonts = fontsOf(e);
        }
        blocks.push(b);
    } else if (type === 'table') {
        if (layout) {
            var tb = { index: i, type: 'table' };
            try { tableLayout(e, tb); } catch (x) { tb.layout_error = String(x); }
            blocks.push(tb);
        } else {
            var rows = [];
            for (var r = 0; r < e.GetRowsCount(); r++) {
                var row = e.GetRow(r), cells = [];
                for (var c = 0; c < row.GetCellsCount(); c++) cells.push(textOf(row.GetCell(c).GetContent()));
                rows.push(cells);
            }
            blocks.push({ index: i, type: 'table', rows: rows });
        }
    } else {
        blocks.push({ index: i, type: type });
    }
}

var result = { kind: 'document', blocks: count, from: from, to: to };
if (layout) {
    var sec = doc.GetFinalSection();
    result.page = {
        size: [mm(sec.GetPageWidth()), mm(sec.GetPageHeight())],
        margins: [mm(sec.GetPageMarginTop()), mm(sec.GetPageMarginRight()), mm(sec.GetPageMarginBottom()), mm(sec.GetPageMarginLeft())]
    };
    try {
        var dt = doc.GetDefaultTextPr(), normal = null;
        try { normal = doc.GetStyle('Normal'); } catch (x) { }
        var ntp = normal ? normal.GetTextPr() : null;
        var font = (ntp && ntp.GetFontFamily && ntp.GetFontFamily()) || (dt.GetFontFamily && dt.GetFontFamily());
        var size = (ntp && ntp.GetFontSize && ntp.GetFontSize()) || (dt.GetFontSize && dt.GetFontSize());
        result.defaults = { font: font && typeof font === 'object' ? (font.name || font.Name || null) : font, size: size ? size / 2 : null };
    } catch (x) { }
    try { result.pages = doc.GetPageCount(); } catch (x) { }
}
result.empty = count <= 1 && (count === 0 || (doc.GetElement(0).GetClassType() === 'paragraph' && doc.GetElement(0).GetText().replace(/[\r\n]/g, '').length === 0));
result.content = blocks;
return result;
