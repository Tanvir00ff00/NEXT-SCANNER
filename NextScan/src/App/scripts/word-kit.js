// The builder the assistant writes Word documents with (write_document), and
// the helpers its own scripts may call (edit_document gets them as `NS`).
//
// A model is good at saying what a page looks like -- "a 3-column table, the
// first column 60 mm, a dotted line after the label" -- and poor at getting
// forty calls of the editor's API right in a row: units that differ per method
// (twips, half-points, eighths of a point, EMU), argument orders that changed
// between versions, a new table with no borders. So the page is described as
// data, in the units a person measures with (mm and pt), and this file turns
// it into the editor's calls, the same way every time.
//
// Every measurement in a spec: lengths in millimetres, font sizes and spacing
// in points, colours as '#RRGGBB'.
var NS = (function () {
    'use strict';

    function tw(mm) { return Math.round((+mm || 0) * 1440 / 25.4); }     // mm -> twips
    function ptw(pt) { return Math.round((+pt || 0) * 20); }             // pt -> twips
    function emu(mm) { return Math.round((+mm || 0) * 36000); }          // mm -> EMU
    function mmOf(twips) { return Math.round((+twips || 0) * 25.4 / 1440 * 10) / 10; }
    function has(o, k) { return o && o[k] !== undefined && o[k] !== null; }

    function rgb(c) {
        if (!c || c === 'auto' || c === 'none') return null;
        var m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(String(c).trim());
        if (!m) {
            var named = { black: '000000', white: 'FFFFFF', red: 'FF0000', blue: '0000FF', green: '008000', grey: '808080', gray: '808080' };
            var n = named[String(c).toLowerCase()];
            if (!n) return null;
            m = /^(..)(..)(..)$/.exec(n);
        }
        return [parseInt(m[1], 16), parseInt(m[2], 16), parseInt(m[3], 16)];
    }

    // What the builder can tell at once, without a picture, that will look wrong: it says so in the
    // result, so that a mistake of measurement is not left for the eye to find.
    var WARNINGS = [];
    function warn(text) { if (WARNINGS.indexOf(text) < 0) WARNINGS.push(text); }
    function textArea() {
        var sec = Api.GetDocument().GetFinalSection();
        return { width: mmOf(sec.GetPageWidth() - sec.GetPageMarginLeft() - sec.GetPageMarginRight()), page: mmOf(sec.GetPageWidth()), pageHeight: mmOf(sec.GetPageHeight()) };
    }
    function check(what, width, indent) {
        try {
            var a = textArea();
            if (width + (indent || 0) > a.width + 1)
                warn(what + ' is ' + Math.round(width * 10) / 10 + ' mm wide but the text area is ' + a.width + ' mm (the ' + a.page + ' mm page less its side margins): it runs past the right margin and is cut off when printed. Make it narrower, or widen the text area (smaller margins, a wider or landscape page).');
        } catch (e) { }
    }

    var PAGES = { a4: [210, 297], a5: [148, 210], a3: [297, 420], letter: [215.9, 279.4], legal: [215.9, 355.6] };

    // ---- the page ----------------------------------------------------------
    function page(spec, doc) {
        if (!spec) return;
        doc = doc || Api.GetDocument();
        var sec = doc.GetFinalSection();
        var size = spec.size || null;
        var wh = null;
        if (typeof size === 'string' && PAGES[size.toLowerCase()]) wh = PAGES[size.toLowerCase()].slice();
        else if (size && size.length === 2) wh = [+size[0], +size[1]];
        if (has(spec, 'width') && has(spec, 'height')) wh = [+spec.width, +spec.height];
        if (!wh && (spec.orientation === 'landscape' || spec.orientation === 'portrait')) {
            var cw = mmOf(sec.GetPageWidth()), ch = mmOf(sec.GetPageHeight());
            if (cw && ch) wh = [cw, ch];
        }
        if (wh) {
            // A named size is portrait unless told otherwise; given sizes are
            // taken as they are, unless an orientation says to turn them.
            if (spec.orientation === 'landscape' && wh[0] < wh[1]) wh = [wh[1], wh[0]];
            if (spec.orientation === 'portrait' && wh[0] > wh[1]) wh = [wh[1], wh[0]];
            sec.SetPageSize(tw(wh[0]), tw(wh[1]), wh[0] <= wh[1]);
        }
        var m = spec.margins;
        if (m !== undefined && m !== null) {
            if (typeof m === 'number') m = [m, m, m, m];
            // [top, right, bottom, left], as CSS reads them.
            sec.SetPageMargins(tw(m[3]), tw(m[0]), tw(m[1]), tw(m[2]));
        }
        if (has(spec, 'columns')) columns(sec, spec.columns);
        if (has(spec, 'headerDistance')) sec.SetHeaderDistance(tw(spec.headerDistance));
        if (has(spec, 'footerDistance')) sec.SetFooterDistance(tw(spec.footerDistance));
        if (has(spec, 'startNumber')) sec.SetStartPageNumber(+spec.startNumber);
        if (spec.differentFirstPage || has(spec, 'firstHeader') || has(spec, 'firstFooter')) sec.SetTitlePage(true);
        if (spec.differentFirstPage === false) sec.SetTitlePage(false);
        if (has(spec, 'evenHeader') || has(spec, 'evenFooter')) doc.SetEvenAndOddHdrFtr(true);
        var base = spec.text || null;
        if (has(spec, 'header')) headerFooter(sec, true, 'default', spec.header, base);
        if (has(spec, 'footer')) headerFooter(sec, false, 'default', spec.footer, base);
        if (has(spec, 'firstHeader')) headerFooter(sec, true, 'first', spec.firstHeader, base);
        if (has(spec, 'firstFooter')) headerFooter(sec, false, 'first', spec.firstFooter, base);
        if (has(spec, 'evenHeader')) headerFooter(sec, true, 'even', spec.evenHeader, base);
        if (has(spec, 'evenFooter')) headerFooter(sec, false, 'even', spec.evenFooter, base);
        if (has(spec, 'watermark')) {
            var w = spec.watermark;
            if (w === false || w === 'none' || w === null) { try { doc.RemoveWatermark(); } catch (e) { } }
            else doc.InsertWatermark(String(typeof w === 'object' ? w.text : w), !(typeof w === 'object' && w.diagonal === false));
        }
    }

    // Columns: a number, {count, gap (mm)}, or {widths: [mm...], gaps: [mm...]}.
    function columns(sec, c) {
        if (c === undefined || c === null) return;
        if (typeof c === 'number') c = { count: c };
        var gap = has(c, 'gap') ? +c.gap : 12.5;
        if (c.widths && c.widths.length) {
            var gaps = [];
            for (var i = 0; i < c.widths.length - 1; i++) gaps.push(tw(c.gaps && has(c.gaps, i) ? c.gaps[i] : gap));
            sec.SetNotEqualColumns(c.widths.map(tw), gaps);
        } else sec.SetEqualColumns(Math.max(1, +c.count || 1), tw(gap));
    }

    // The running text of a page: a string, a paragraph, a list of them, or {blocks: [...]}.
    function headerFooter(sec, isHeader, which, spec, base) {
        var content = isHeader ? sec.GetHeader(which, true) : sec.GetFooter(which, true);
        var list = spec;
        if (spec && !(spec instanceof Array) && typeof spec === 'object' && spec.blocks) list = spec.blocks;
        if (!(list instanceof Array)) list = [list];
        content.RemoveAllElements();
        var first = true;
        for (var i = 0; i < list.length; i++) {
            var b = list[i];
            if (typeof b === 'string') b = { text: b };
            if (b.type === 'table') { content.Push(table(b, base)); first = false; continue; }
            if (first) { fillParagraph(content.GetElement(0), b, base); first = false; }
            else content.Push(paragraph(b, base));
        }
    }

    // ---- text ----------------------------------------------------------------
    var RUN_KEYS = ['font', 'size', 'bold', 'italic', 'underline', 'strike', 'color', 'caps', 'smallCaps', 'spacing', 'highlight', 'vertAlign', 'shade', 'lang'];

    function inherit(base, over) {
        var o = {};
        var k;
        if (base) for (k = 0; k < RUN_KEYS.length; k++) if (has(base, RUN_KEYS[k])) o[RUN_KEYS[k]] = base[RUN_KEYS[k]];
        if (over) for (k = 0; k < RUN_KEYS.length; k++) if (has(over, RUN_KEYS[k])) o[RUN_KEYS[k]] = over[RUN_KEYS[k]];
        return o;
    }

    // Serves a run, a whole paragraph, a found range or a style's text properties:
    // they all take the same calls, and a method one of them lacks is skipped.
    function styleRun(run, s) {
        function call(name, a, b, c, d) { if (run && typeof run[name] === 'function') run[name](a, b, c, d); }
        if (has(s, 'font')) call('SetFontFamily', String(s.font));
        if (has(s, 'size')) call('SetFontSize', Math.round(+s.size * 2));
        if (has(s, 'bold')) call('SetBold', !!s.bold);
        if (has(s, 'italic')) call('SetItalic', !!s.italic);
        if (has(s, 'underline')) call('SetUnderline', !!s.underline);
        if (has(s, 'strike')) call('SetStrikeout', !!s.strike);
        if (has(s, 'caps')) call('SetCaps', !!s.caps);
        if (has(s, 'smallCaps')) call('SetSmallCaps', !!s.smallCaps);
        if (has(s, 'spacing')) call('SetSpacing', ptw(s.spacing));
        if (has(s, 'vertAlign')) call('SetVertAlign', s.vertAlign);
        if (has(s, 'highlight')) call('SetHighlight', String(s.highlight));
        var c = rgb(s.color);
        if (c) call('SetColor', c[0], c[1], c[2], false);
        var sh = rgb(s.shade);
        if (sh) call('SetShd', 'clear', sh[0], sh[1], sh[2]);
    }

    // Text goes in run by run: a tab and a line break are elements of their
    // own, not characters, so '\t' and '\n' are split out here.
    function addText(paragraph, text, s) {
        var lines = String(text === undefined || text === null ? '' : text).replace(/\r\n?/g, '\n').split('\n');
        for (var l = 0; l < lines.length; l++) {
            var parts = lines[l].split('\t');
            for (var t = 0; t < parts.length; t++) {
                var run = Api.CreateRun();
                if (t > 0) run.AddTabStop();
                if (parts[t].length) run.AddText(parts[t]);
                if (l < lines.length - 1 && t === parts.length - 1) run.AddLineBreak();
                styleRun(run, s);
                paragraph.AddElement(run);
            }
        }
    }

    var ALIGN = { left: 'left', right: 'right', center: 'center', centre: 'center', justify: 'both', both: 'both' };
    var TABALIGN = { left: 'left', right: 'right', center: 'center', centre: 'center', decimal: 'decimal' };
    var BORDER = { single: 'single', solid: 'single', none: 'none', dotted: 'dotted', dashed: 'dashed', double: 'double', thick: 'thick' };

    function borderArgs(b, fallback) {
        // A side: 'none', true, a size in points, or {style, size (pt), color, space (pt)}.
        if (b === undefined || b === null) b = fallback;
        if (b === undefined || b === null) return null;
        if (b === false || b === 'none') return ['none', 0, 0, 0, 0, 0];
        if (b === true) b = {};
        if (typeof b === 'number') b = { size: b };
        if (typeof b === 'string') b = { style: b };
        var c = rgb(b.color) || [0, 0, 0];
        return [BORDER[b.style] || 'single', Math.max(2, Math.round((has(b, 'size') ? +b.size : 0.5) * 8)), ptw(b.space || 0), c[0], c[1], c[2]];
    }

    function styleParagraph(p, s) {
        if (has(s, 'style')) { try { var st = Api.GetDocument().GetStyle(String(s.style)); if (st) p.SetStyle(st); } catch (e) { } }
        if (has(s, 'align') && ALIGN[s.align]) p.SetJc(ALIGN[s.align]);
        if (has(s, 'before')) p.SetSpacingBefore(ptw(s.before));
        if (has(s, 'after')) p.SetSpacingAfter(ptw(s.after));
        if (has(s, 'line')) {
            var line = s.line;
            if (typeof line === 'number') p.SetSpacingLine(Math.round(line * 240), 'auto');
            else if (has(line, 'exact')) p.SetSpacingLine(ptw(line.exact), 'exact');
            else if (has(line, 'atLeast')) p.SetSpacingLine(ptw(line.atLeast), 'atLeast');
        }
        var ind = s.indent;
        if (typeof ind === 'number') ind = { left: ind };
        if (ind) {
            if (has(ind, 'left')) p.SetIndLeft(tw(ind.left));
            if (has(ind, 'right')) p.SetIndRight(tw(ind.right));
            if (has(ind, 'first')) p.SetIndFirstLine(tw(ind.first));
            if (has(ind, 'hanging')) p.SetIndFirstLine(-tw(ind.hanging));
        }
        if (s.tabs && s.tabs.length) {
            var pos = [], kinds = [];
            for (var i = 0; i < s.tabs.length; i++) {
                var t = s.tabs[i];
                if (typeof t === 'number') t = { pos: t };
                pos.push(tw(t.pos));
                kinds.push(TABALIGN[t.align] || 'left');
            }
            p.SetTabs(pos, kinds);
        }
        if (has(s, 'keepNext')) p.SetKeepNext(!!s.keepNext);
        if (has(s, 'keepLines')) p.SetKeepLines(!!s.keepLines);
        if (has(s, 'pageBreakBefore')) p.SetPageBreakBefore(!!s.pageBreakBefore);
        var sh = rgb(s.fill);
        if (sh) p.SetShd('clear', sh[0], sh[1], sh[2]);
        if (s.border) {
            var b = s.border, a;
            if (typeof b !== 'object' || b.style || b.size) b = { top: b, bottom: b, left: b, right: b };
            if ((a = borderArgs(b.top))) p.SetTopBorder.apply(p, a);
            if ((a = borderArgs(b.bottom))) p.SetBottomBorder.apply(p, a);
            if ((a = borderArgs(b.left))) p.SetLeftBorder.apply(p, a);
            if ((a = borderArgs(b.right))) p.SetRightBorder.apply(p, a);
        }
    }

    // Fills an existing paragraph (the one a new table cell already holds),
    // or a new one.
    function fillParagraph(p, s, base) {
        if (typeof s === 'string') s = { text: s };
        s = s || {};
        var own = inherit(base, s);
        styleParagraph(p, s);
        var fields = false;
        if (s.runs && s.runs.length) {
            for (var i = 0; i < s.runs.length; i++) {
                var r = s.runs[i];
                if (typeof r === 'string') r = { text: r };
                if (r.page || r.field === 'page') { p.AddPageNumber(); fields = true; }
                else if (r.pages || r.field === 'pages') { p.AddPagesCount(); fields = true; }
                else if (has(r, 'link')) {
                    // A link is an element of its own; AddHyperlink on the paragraph would replace what is there.
                    var h = Api.CreateHyperlink(String(r.link), String(has(r, 'text') ? r.text : r.link), String(r.tip || ''));
                    p.AddElement(h);
                    try { styleRun(h.GetElement(0), inherit(own, r)); } catch (e) { }
                }
                else addText(p, r.text, inherit(own, r));
            }
        } else if (has(s, 'text')) addText(p, s.text, own);
        // Fields are not runs: the paragraph's own setters reach them.
        if (fields) styleRun(p, own);
        if (has(s, 'footnote')) footnote(p, s.footnote);
        // An empty paragraph still has a height, set by its mark's font.
        if (has(own, 'size') || has(own, 'font')) {
            try { var mark = p.GetParagraphMarkTextPr(); if (has(own, 'size')) mark.SetFontSize(Math.round(+own.size * 2)); if (has(own, 'font')) mark.SetFontFamily(String(own.font)); } catch (e) { }
        }
        return p;
    }

    function paragraph(s, base) { return fillParagraph(Api.CreateParagraph(), s, base); }

    // ---- tables ------------------------------------------------------------
    var VALIGN = { top: 'top', center: 'center', middle: 'center', bottom: 'bottom' };

    function cellSpec(c) {
        if (c === null || c === undefined) return { text: '' };
        if (typeof c === 'string' || typeof c === 'number') return { text: String(c) };
        return c;
    }

    function table(s, base) {
        var rowsIn = s.rows || [];
        var cols = s.columns || [];
        var nRows = rowsIn.length;
        if (!nRows) throw new Error('A table needs rows: [{cells: [...]}, ...]. This one came with only ' + Object.keys(s).join(', ') +
                                    ' -- if rows were given, they were lost on the way: send the whole description as the spec string instead');
        var nCols = cols.length;
        if (!nCols) {
            // No widths: as many equal columns as the widest row needs.
            for (var q = 0; q < nRows; q++) {
                var cs = rowsIn[q].cells || rowsIn[q], w = 0;
                for (var z = 0; z < cs.length; z++) w += +(cellSpec(cs[z]).span || 1);
                nCols = Math.max(nCols, w);
            }
            var usable = s.width || 170;
            for (var e = 0; e < nCols; e++) cols.push(usable / nCols);
        }

        // Which grid cells each given cell covers, allowing for cells above
        // that reach down into this row.
        var taken = [], placed = [];
        for (var r = 0; r < nRows; r++) taken.push([]);
        for (r = 0; r < nRows; r++) {
            var list = rowsIn[r].cells || rowsIn[r];
            var col = 0;
            for (var i = 0; i < list.length; i++) {
                while (col < nCols && taken[r][col]) col++;
                if (col >= nCols) break;
                var c = cellSpec(list[i]);
                var span = Math.max(1, Math.min(nCols - col, +(c.span || c.colspan || 1)));
                var down = Math.max(1, Math.min(nRows - r, +(c.rowspan || 1)));
                for (var rr = r; rr < r + down; rr++) for (var cc = col; cc < col + span; cc++) taken[rr][cc] = true;
                placed.push({ r: r, c: col, span: span, down: down, spec: c });
                col += span;
            }
        }

        var t = Api.CreateTable(nRows, nCols);
        if (t.GetRowsCount() !== nRows) t = Api.CreateTable(nCols, nRows);    // the argument order changed once

        var total = 0;
        for (var k = 0; k < nCols; k++) total += +cols[k];
        check('A table', total, +s.indent || 0);
        t.SetWidth('twips', tw(total));
        t.SetTableLayout('fixed');
        // The grid too, which the builder API has no call for. The editor
        // lays the table out from the cell widths, but a .docx carries the
        // grid, and Word lays out a fixed table from that: left at its 20 mm
        // default, every column came out 20 mm in Word. The internal table
        // takes millimetres.
        try { if (t.Table && t.Table.SetTableGrid) t.Table.SetTableGrid(cols.map(function (w) { return +w; })); } catch (e) { }
        for (r = 0; r < nRows; r++)
            for (k = 0; k < nCols; k++) {
                var pr = Api.CreateTableCellPr();
                pr.SetWidth('twips', tw(cols[k]));
                t.GetCell(r, k).SetCellPr(pr);
            }

        // Borders: 'all' (the default), 'none', 'outer', 'inner', or
        // {top, bottom, left, right, insideH, insideV}; `border` gives the line.
        var line = s.border === undefined ? {} : s.border;
        var which = s.borders === undefined ? 'all' : s.borders;
        var sides = { top: 0, bottom: 0, left: 0, right: 0, insideH: 0, insideV: 0 };
        if (which === 'all' || which === true) sides = { top: 1, bottom: 1, left: 1, right: 1, insideH: 1, insideV: 1 };
        else if (which === 'outer') sides = { top: 1, bottom: 1, left: 1, right: 1, insideH: 0, insideV: 0 };
        else if (which === 'inner') sides = { top: 0, bottom: 0, left: 0, right: 0, insideH: 1, insideV: 1 };
        else if (which === 'horizontal') sides = { top: 1, bottom: 1, left: 0, right: 0, insideH: 1, insideV: 0 };
        else if (typeof which === 'object' && which) {
            for (var key in sides) sides[key] = which[key] === undefined ? 0 : which[key];
        }
        var setters = { top: 'SetTableBorderTop', bottom: 'SetTableBorderBottom', left: 'SetTableBorderLeft', right: 'SetTableBorderRight', insideH: 'SetTableBorderInsideH', insideV: 'SetTableBorderInsideV' };
        for (var side in setters) {
            var v = sides[side];
            var a = borderArgs(v ? (v === 1 || v === true ? line : v) : 'none');
            t[setters[side]].apply(t, a);
        }

        var pad = s.padding;
        if (pad !== undefined && pad !== null) {
            if (typeof pad === 'number') pad = [pad, pad, pad, pad];
            t.SetTableCellMarginTop(tw(pad[0]));
            t.SetTableCellMarginRight(tw(pad[1]));
            t.SetTableCellMarginBottom(tw(pad[2]));
            t.SetTableCellMarginLeft(tw(pad[3]));
        }
        if (has(s, 'align') && ALIGN[s.align]) t.SetJc(ALIGN[s.align] === 'both' ? 'left' : ALIGN[s.align]);
        if (has(s, 'indent')) t.SetTableInd(tw(s.indent));

        for (r = 0; r < nRows; r++) {
            var rowSpec = rowsIn[r];
            var h = rowSpec && !(rowSpec instanceof Array) ? (rowSpec.height || s.rowHeight) : s.rowHeight;
            if (h) t.GetRow(r).SetHeight(rowSpec && rowSpec.exact ? 'exact' : 'atLeast', tw(h));
        }

        // Merge, rightmost first: a merge only moves the cells to its right,
        // and there are none left to place there.
        var merges = placed.filter(function (p) { return p.span > 1 || p.down > 1; });
        merges.sort(function (a, b) { return b.c - a.c || b.r - a.r; });
        for (var m = 0; m < merges.length; m++) {
            var mg = merges[m], group = [];
            for (rr = mg.r; rr < mg.r + mg.down; rr++)
                for (cc = mg.c; cc < mg.c + mg.span; cc++) group.push(t.GetCell(rr, cc));
            t.MergeCells(group);
        }

        // The cell for a grid position, now that some are merged: each merge
        // to its left in the same row took (span - 1) cells away.
        function cellAt(row, gridCol) {
            var index = gridCol;
            for (var j = 0; j < merges.length; j++) {
                var g = merges[j];
                if (row >= g.r && row < g.r + g.down && g.c < gridCol) index -= g.span - 1;
            }
            return t.GetCell(row, index);
        }

        var tableText = inherit(base, s);
        for (var n = 0; n < placed.length; n++) {
            var at = placed[n], spec = at.spec;
            var cell = cellAt(at.r, at.c);
            if (!cell) continue;
            var rowText = rowsIn[at.r] && !(rowsIn[at.r] instanceof Array) ? inherit(tableText, rowsIn[at.r]) : tableText;
            var text = inherit(rowText, spec);
            var content = cell.GetContent();
            var paras = spec.paragraphs || [spec];
            for (var pi = 0; pi < paras.length; pi++) {
                var ps = paras[pi];
                if (typeof ps === 'string') ps = { text: ps };
                var merged = {};
                for (var pk in ps) merged[pk] = ps[pk];
                // What belongs to the cell stays on the cell: its border and
                // fill drawn around the paragraph as well made a box in a box.
                if (ps === spec) { delete merged.border; delete merged.fill; delete merged.span; delete merged.colspan; delete merged.rowspan; delete merged.valign; delete merged.paragraphs; }
                if (!has(merged, 'align') && has(spec, 'align')) merged.align = spec.align;
                if (!has(merged, 'align') && rowsIn[at.r] && has(rowsIn[at.r], 'align')) merged.align = rowsIn[at.r].align;
                if (!has(merged, 'align') && has(s, 'cellAlign')) merged.align = s.cellAlign;
                if (pi === 0) fillParagraph(content.GetElement(0), merged, text);
                else content.Push(paragraph(merged, text));
            }
            var va = VALIGN[spec.valign || (rowsIn[at.r] && rowsIn[at.r].valign) || s.valign || ''];
            if (va) cell.SetVerticalAlign(va);
            var fill = rgb(spec.fill || (rowsIn[at.r] && rowsIn[at.r].fill));
            if (fill) cell.SetShd('clear', fill[0], fill[1], fill[2]);
            if (spec.border) {
                var cb = spec.border, x;
                if (typeof cb !== 'object' || cb.style || cb.size) cb = { top: cb, bottom: cb, left: cb, right: cb };
                if ((x = borderArgs(cb.top))) cell.SetCellBorderTop.apply(cell, x);
                if ((x = borderArgs(cb.bottom))) cell.SetCellBorderBottom.apply(cell, x);
                if ((x = borderArgs(cb.left))) cell.SetCellBorderLeft.apply(cell, x);
                if ((x = borderArgs(cb.right))) cell.SetCellBorderRight.apply(cell, x);
            }
        }
        return t;
    }

    // ---- pictures and boxes ---------------------------------------------------
    function place(drawing, s) {
        if (has(s, 'x') || has(s, 'y')) {
            try {
                var a = textArea();
                if ((+s.x || 0) + (+s.width || 0) > a.page + 0.5 || (+s.y || 0) + (+s.height || 0) > a.pageHeight + 0.5)
                    warn('A floating shape or picture at x ' + (+s.x || 0) + ', y ' + (+s.y || 0) + ' mm with size ' + (+s.width || 0) + ' x ' + (+s.height || 0) + ' mm goes past the edge of the ' + a.page + ' x ' + a.pageHeight + ' mm page.');
            } catch (e) { }
            drawing.SetWrappingStyle(s.wrap || 'inFront');
            drawing.SetHorPosition('page', emu(s.x || 0));
            drawing.SetVerPosition('page', emu(s.y || 0));
        } else if (s.wrap) drawing.SetWrappingStyle(s.wrap);
    }

    function image(s) {
        if (!s.src) throw new Error('An image needs src.');
        if (!has(s, 'x') && !has(s, 'y')) check('A picture', +s.width || 30, 0);
        var img = Api.CreateImage(String(s.src), emu(s.width || 30), emu(s.height || 30));
        place(img, s);
        return img;
    }

    // A text box is a rectangle with text in it; any other outline (ellipse, arrow, star,
    // line, ...) is a shape: `shape` names the outline, `line` (or `border`) its edge.
    function textbox(s, base) {
        var fill = rgb(s.fill);
        var edge = has(s, 'line') ? s.line : s.border;
        var stroke = edge === undefined || edge === 'none' || edge === false ? null : (typeof edge === 'object' ? edge : (typeof edge === 'number' ? { size: edge } : {}));
        var sc = stroke ? rgb(stroke.color) || [0, 0, 0] : null;
        var shape = Api.CreateShape(String(s.shape || s.geometry || 'rect'), emu(s.width || 40), emu(has(s, 'height') ? s.height : 10),
            fill ? Api.CreateSolidFill(Api.CreateRGBColor(fill[0], fill[1], fill[2])) : Api.CreateNoFill(),
            sc ? Api.CreateStroke(Math.round((stroke.size || 0.5) * 12700), Api.CreateSolidFill(Api.CreateRGBColor(sc[0], sc[1], sc[2]))) : Api.CreateStroke(0, Api.CreateNoFill()));
        if (has(s, 'rotation')) try { shape.SetRotation(+s.rotation); } catch (e) { }
        var pad = s.padding === undefined ? 1 : s.padding;
        if (typeof pad === 'number') pad = [pad, pad, pad, pad];
        try { shape.SetPaddings(emu(pad[3]), emu(pad[0]), emu(pad[1]), emu(pad[2])); } catch (e) { }
        if (s.valign && VALIGN[s.valign]) try { shape.SetVerticalTextAlign(VALIGN[s.valign]); } catch (e) { }
        var own = inherit(base, s);
        if (!has(own, 'color')) own.color = '#000000';      // a shape's own default text is white
        if (has(s, 'text') || s.runs || s.paragraphs) {
            var content = shape.GetDocContent();
            var paras = s.paragraphs || [s];
            content.RemoveAllElements();
            for (var i = 0; i < paras.length; i++) {
                var ps = typeof paras[i] === 'string' ? { text: paras[i] } : paras[i];
                if (ps === s) ps = { text: s.text, runs: s.runs, align: s.align };
                content.Push(paragraph(ps, own));
            }
        }
        place(shape, s);
        return shape;
    }

    // ---- charts --------------------------------------------------------------
    var CHARTS = {
        bar: 'bar', column: 'bar', columns: 'bar', barstacked: 'barStacked', columnstacked: 'barStacked', stackedbar: 'barStacked', stackedcolumn: 'barStacked',
        barpercent: 'barStackedPercent', columnpercent: 'barStackedPercent', barstackedpercent: 'barStackedPercent',
        hbar: 'horizontalBar', horizontalbar: 'horizontalBar', barh: 'horizontalBar', hbarstacked: 'horizontalBarStacked', horizontalbarstacked: 'horizontalBarStacked',
        line: 'lineNormal', linenormal: 'lineNormal', linestacked: 'lineStacked', pie: 'pie', pie3d: 'pie3D', doughnut: 'doughnut', donut: 'doughnut',
        area: 'area', areastacked: 'areaStacked', scatter: 'scatter', xy: 'scatter', stock: 'stock', bar3d: 'bar3D', column3d: 'bar3D', line3d: 'line3D'
    };

    // {type: 'chart', chart: 'bar', title, categories: ['Q1', 'Q2'], series: [{name, values: [..]}], width, height (mm),
    //  legend: 'bottom' | 'right' | 'top' | 'left' | false, colors: ['#RRGGBB', ...], dataLabels: true, xTitle, yTitle}
    function chart(s) {
        var key = String(s.chart || s.kind || 'bar').toLowerCase().replace(/[^a-z0-9]/g, '');
        var type = CHARTS[key] || String(s.chart || 'bar');
        var series = s.series || [];
        if (!series.length) throw new Error('A chart needs series: [{name, values: [numbers]}], and categories: [labels].');
        var values = [], names = [];
        for (var i = 0; i < series.length; i++) { values.push((series[i].values || []).map(Number)); names.push(String(series[i].name || ('Series ' + (i + 1)))); }
        var c = Api.CreateChart(type, values, names, (s.categories || []).map(String), emu(s.width || 120), emu(s.height || 70), +s.style || 24);
        if (s.title) c.SetTitle(String(s.title), +s.titleSize || 14);
        if (s.legend === false || s.legend === 'none') c.SetLegendPos('none');
        else c.SetLegendPos(String(s.legend || 'bottom'));
        if (s.xTitle) c.SetHorAxisTitle(String(s.xTitle), 11);
        if (s.yTitle) c.SetVerAxisTitle(String(s.yTitle), 11);
        if (s.dataLabels) c.SetShowDataLabels(false, false, true, false);
        if (s.colors) for (var k = 0; k < s.colors.length; k++) {
            var col = rgb(s.colors[k]);
            if (col) c.SetSeriesFill(Api.CreateSolidFill(Api.CreateRGBColor(col[0], col[1], col[2])), k, false);
        }
        place(c, s);
        return c;
    }

    // ---- lists -----------------------------------------------------------------
    var NUMBER_FORMS = { number: '1.', decimal: '1.', numbers: '1.', paren: '1)', 'upper-roman': 'I.', roman: 'I.', 'lower-roman': 'i.', 'upper-alpha': 'A.', 'upper-letter': 'A.', 'lower-alpha': 'a)', 'lower-letter': 'a)', alpha: 'a)', letter: 'a)' };

    // {type: 'list', kind: 'bullet' | 'number' | 'lower-alpha' | 'upper-roman' ..., bullet: '-', start: 1,
    //  items: ['text' | {text, level: 0-8, ...paragraph}], indent: {left, hanging} (mm)}
    function makeList(s, base, doc) {
        var kind = String(s.kind || s.style || 'bullet').toLowerCase();
        var bullet = kind === 'bullet' || kind === 'bullets';
        var numbering = doc.CreateNumbering(bullet ? 'bullet' : 'numbered');
        var used = {};
        var items = s.items || [];
        var out = [];
        for (var i = 0; i < items.length; i++) {
            var it = typeof items[i] === 'string' ? { text: items[i] } : items[i];
            var lvl = Math.max(0, Math.min(8, +it.level || 0));
            var level = numbering.GetLevel(lvl);
            if (!used[lvl]) {
                used[lvl] = true;
                if (bullet) { if (has(s, 'bullet') && lvl === 0) try { level.SetTemplateType('bullet', String(s.bullet)); } catch (e) { } }
                else {
                    var form = lvl === 0 ? (NUMBER_FORMS[kind] || '1.') : lvl === 1 ? 'a)' : 'i.';
                    try { level.SetTemplateType(form, ''); } catch (e) { }
                    if (lvl === 0 && has(s, 'start')) try { level.SetStart(+s.start); } catch (e) { }
                }
                if (s.indent) {
                    var pr = level.GetParaPr();
                    if (has(s.indent, 'left')) pr.SetIndLeft(tw((+s.indent.left) + lvl * 6));
                    if (has(s.indent, 'hanging')) pr.SetIndFirstLine(-tw(s.indent.hanging));
                }
            }
            var spec = {};
            for (var k in it) if (k !== 'level') spec[k] = it[k];
            var p = paragraph(spec, base);
            p.SetNumbering(level);
            out.push(p);
        }
        return out;
    }

    // ---- maths ---------------------------------------------------------------------
    // The editor reads an equation in the linear (UnicodeMath) form: a/b for a fraction,
    // x^2, x_i, sqrt written as the sign followed by (...), and symbols as themselves.
    // LaTeX, which every model knows, is turned into that here.
    var GREEK = {
        alpha: '\u03B1', beta: '\u03B2', gamma: '\u03B3', delta: '\u03B4', epsilon: '\u03B5', zeta: '\u03B6',
        eta: '\u03B7', theta: '\u03B8', iota: '\u03B9', kappa: '\u03BA', lambda: '\u03BB', mu: '\u03BC',
        nu: '\u03BD', xi: '\u03BE', pi: '\u03C0', rho: '\u03C1', sigma: '\u03C3', tau: '\u03C4',
        upsilon: '\u03C5', phi: '\u03C6', chi: '\u03C7', psi: '\u03C8', omega: '\u03C9', Gamma: '\u0393',
        Delta: '\u0394', Theta: '\u0398', Lambda: '\u039B', Xi: '\u039E', Pi: '\u03A0', Sigma: '\u03A3',
        Phi: '\u03A6', Psi: '\u03A8', Omega: '\u03A9'
    };
    var SYMBOLS = {
        cdot: '\u00B7', times: '\u00D7', div: '\u00F7', pm: '\u00B1', mp: '\u2213', leq: '\u2264',
        le: '\u2264', geq: '\u2265', ge: '\u2265', neq: '\u2260', ne: '\u2260', approx: '\u2248',
        equiv: '\u2261', infty: '\u221E', to: '\u2192', rightarrow: '\u2192', leftarrow: '\u2190', Rightarrow: '\u21D2',
        Leftrightarrow: '\u21D4', sum: '\u2211', prod: '\u220F', int: '\u222B', partial: '\u2202', nabla: '\u2207',
        degree: '\u00B0', circ: '\u00B0', ldots: '\u2026', cdots: '\u22EF', in: '\u2208', notin: '\u2209',
        subset: '\u2282', cup: '\u222A', cap: '\u2229', forall: '\u2200', exists: '\u2203', sqrt: '\u221A',
        angle: '\u2220', propto: '\u221D', sim: '\u223C'
    };

    function latexToLinear(src) {
        var s = String(src);
        if (s.indexOf('\\') < 0 && s.indexOf('{') < 0) return s;
        s = s.replace(/\\left|\\right|\\displaystyle|\\,|\\;|\\!|\\quad/g, ' ').replace(/^\$+|\$+$/g, '');
        function group(str, i) {                     // the {...} starting at str[i]; returns [inner, next]
            var depth = 0, j = i;
            for (; j < str.length; j++) {
                if (str[j] === '{') depth++;
                else if (str[j] === '}') { depth--; if (depth === 0) break; }
            }
            return [str.slice(i + 1, j), j + 1];
        }
        function arg(str, i) {                       // one argument: {group} or a single token
            while (str[i] === ' ') i++;
            if (str[i] === '{') return group(str, i);
            if (str[i] === '\\') { var m = /^\\[a-zA-Z]+/.exec(str.slice(i)); if (m) return [m[0], i + m[0].length]; }
            return [str[i] || '', i + 1];
        }
        function conv(str) {
            var out = '', i = 0;
            while (i < str.length) {
                var ch = str[i];
                if (ch === '\\') {
                    var m = /^\\([a-zA-Z]+)/.exec(str.slice(i));
                    if (!m) { out += str[i + 1] || ''; i += 2; continue; }
                    var name = m[1];
                    i += m[0].length;
                    if (name === 'frac' || name === 'dfrac' || name === 'tfrac') {
                        var a = arg(str, i); i = a[1]; var b = arg(str, i); i = b[1];
                        out += '(' + conv(a[0]) + ')/(' + conv(b[0]) + ')';
                    } else if (name === 'sqrt') {
                        var root = null;
                        if (str[i] === '[') { var e = str.indexOf(']', i); root = str.slice(i + 1, e); i = e + 1; }
                        var r = arg(str, i); i = r[1];
                        out += root ? '\u221A' + '(' + conv(root) + '&' + conv(r[0]) + ')' : '\u221A' + '(' + conv(r[0]) + ')';
                    } else if (name === 'text' || name === 'mathrm' || name === 'textbf' || name === 'mathbf') {
                        var t = arg(str, i); i = t[1];
                        out += (name === 'text' || name === 'mathrm' ? '"' + t[0] + '"' : conv(t[0]));
                    } else if (name === 'overline' || name === 'bar') { var o = arg(str, i); i = o[1]; out += conv(o[0]) + '\u0305'; }
                    else if (GREEK[name]) out += GREEK[name];
                    else if (SYMBOLS[name]) out += SYMBOLS[name];
                    else out += name;
                    continue;
                }
                if (ch === '^' || ch === '_') {
                    var g = arg(str, i + 1); i = g[1];
                    var inner = conv(g[0]);
                    out += ch + (inner.length > 1 ? '(' + inner + ')' : inner);
                    continue;
                }
                if (ch === '{' || ch === '}') { i++; continue; }
                out += ch; i++;
            }
            return out;
        }
        return conv(s).replace(/\s+/g, ' ').trim();
    }

    // ---- what has to wait until the blocks are in the document ------------------------------
    // An equation, a table of contents and a footnote are put in at the cursor, and a
    // section break is made from a paragraph that is already there.
    var LATER = [];
    function later(fn) { LATER.push(fn); }

    function footnote(p, text) {
        later(function () {
            var doc = Api.GetDocument();
            // The cursor goes to the end of the paragraph: selecting it leaves the cursor
            // at the start of the next one, so step forward and back one character --
            // except in the last paragraph of the document, which has no next one.
            p.Select();
            var n = doc.GetElementsCount(), last = doc.GetElement(n - 1), isLast = false;
            try { isLast = last.GetInternalId() === p.GetInternalId(); } catch (e) { }
            if (isLast) doc.MoveCursorToEnd();
            else { doc.MoveCursorRight(1, false, false); doc.MoveCursorLeft(1, false, false); }
            var note = doc.AddFootnote();
            if (note) {
                var first = note.GetElement(0);
                if (first) first.AddText(' ' + String(text));
            }
        });
    }

    // ---- styles: change what Normal, Heading 1, Title ... look like ---------------------------
    // {'Heading 1': {font, size, bold, color, before, after, align, ...}, 'Normal': {...}}
    function styles(map, doc) {
        if (!map) return;
        for (var name in map) {
            var st = null;
            try { st = doc.GetStyle(name); } catch (e) { }
            if (!st) { try { st = doc.CreateStyle(name); } catch (e) { } }
            if (!st) throw new Error('There is no style called ' + name + ' and one could not be made.');
            var spec = map[name];
            styleRun(st.GetTextPr(), spec);
            try { styleParagraph(st.GetParaPr(), spec); } catch (e) { }
        }
    }

    // ---- a whole spec ------------------------------------------------------
    function blocksOf(list, base, doc) {
        var out = [];
        var lastParagraph = null;
        for (var i = 0; i < list.length; i++) {
            var b = list[i];
            if (typeof b === 'string') b = { type: 'paragraph', text: b };
            var type = b.type || (b.rows ? 'table' : 'paragraph');
            if (type === 'paragraph' || type === 'heading') {
                if (type === 'heading' && !b.style) b.style = 'Heading ' + (b.level || 1);
                lastParagraph = paragraph(b, base);
                out.push(lastParagraph);
            } else if (type === 'table') {
                out.push(table(b, base));
                lastParagraph = null;
            } else if (type === 'page_break') {
                var pb = Api.CreateParagraph();
                pb.AddPageBreak();
                out.push(pb);
                lastParagraph = pb;
            } else if (type === 'spacer') {
                var sp = Api.CreateParagraph();
                sp.SetSpacingBefore(0); sp.SetSpacingAfter(0);
                sp.SetSpacingLine(Math.max(20, tw(b.height || 5)), 'exact');
                out.push(sp);
                lastParagraph = sp;
            } else if (type === 'list') {
                var items = makeList(b, base, doc);
                for (var li = 0; li < items.length; li++) out.push(items[li]);
                lastParagraph = items.length ? items[items.length - 1] : lastParagraph;
            } else if (type === 'column_break') {
                var cb = Api.CreateParagraph();
                cb.AddColumnBreak();
                out.push(cb);
                lastParagraph = cb;
            } else if (type === 'equation' || type === 'math') {
                var eq = Api.CreateParagraph();
                eq.AddText('=');
                out.push(eq);
                lastParagraph = eq;
                (function (par, text) {
                    later(function () { par.Select(); doc.AddMathEquation(latexToLinear(text), 'unicode'); });
                })(eq, b.text || b.latex || b.equation || '');
            } else if (type === 'toc') {
                var tc = Api.CreateParagraph();
                tc.AddText('Table of contents');
                out.push(tc);
                lastParagraph = tc;
                (function (par, spec) {
                    later(function () {
                        par.Select();
                        doc.AddTableOfContents({
                            ShowPageNums: spec.pageNumbers !== false, RightAlgn: spec.pageNumbers !== false, LeaderType: spec.dots === false ? 'none' : 'dot',
                            FormatAsLinks: spec.links !== false, BuildFrom: { OutlineLvls: +spec.levels || 3 }, TocStyle: 'standard'
                        });
                    });
                })(tc, b);
            } else if (type === 'section_break' || type === 'section') {
                // The paragraph the section ends with: the one before, or a small empty one.
                var holder = lastParagraph;
                if (!holder) {
                    holder = Api.CreateParagraph();
                    holder.SetSpacingBefore(0); holder.SetSpacingAfter(0); holder.SetSpacingLine(20, 'exact');
                    out.push(holder);
                }
                lastParagraph = null;
                (function (par, spec) {
                    later(function () {
                        var ended = doc.CreateSection(par);         // what came before keeps the settings it had
                        var now = doc.GetFinalSection();            // what follows takes the new ones
                        var kind = { next: 'nextPage', nextpage: 'nextPage', page: 'nextPage', continuous: 'continuous', odd: 'oddPage', oddpage: 'oddPage', even: 'evenPage', evenpage: 'evenPage' }[String(spec.kind || 'nextPage').toLowerCase()];
                        if (kind) now.SetType(kind);
                        if (spec.page) page(spec.page, doc);
                        if (has(spec, 'columns')) columns(now, spec.columns);
                    });
                })(holder, b);
            } else if (type === 'image' || type === 'textbox' || type === 'shape' || type === 'chart') {
                var drawing = type === 'image' ? image(b) : type === 'chart' ? chart(b) : textbox(b, base);
                var floating = has(b, 'x') || has(b, 'y');
                // A floating one hangs on the paragraph before it; an inline
                // one sits in a paragraph of its own, aligned as asked.
                if (floating && lastParagraph) lastParagraph.AddDrawing(drawing);
                else {
                    var holder = Api.CreateParagraph();
                    if (b.align && ALIGN[b.align]) holder.SetJc(ALIGN[b.align]);
                    holder.AddDrawing(drawing);
                    out.push(holder);
                    lastParagraph = holder;
                }
            } else throw new Error('Unknown block type ' + type + ' (use paragraph, heading, list, table, image, textbox, shape, chart, equation, toc, section_break, column_break, spacer, page_break).');
        }
        return out;
    }

    // Plain body text, for a new document: the editor's own Normal has 10 pt
    // after every paragraph and 1.15 lines, which is not what a copied form has.
    function defaults(d, doc) {
        if (!d) return;
        var textPr = doc.GetDefaultTextPr(), paraPr = doc.GetDefaultParaPr();
        var normal = null;
        try { normal = doc.GetStyle('Normal'); } catch (e) { }
        var targets = [[textPr, paraPr]];
        if (normal) targets.push([normal.GetTextPr(), normal.GetParaPr()]);
        for (var i = 0; i < targets.length; i++) {
            var tp = targets[i][0], pp = targets[i][1];
            if (has(d, 'font')) tp.SetFontFamily(String(d.font));
            if (has(d, 'size')) tp.SetFontSize(Math.round(+d.size * 2));
            var c = rgb(d.color);
            if (c) tp.SetColor(c[0], c[1], c[2], false);
            if (has(d, 'after')) pp.SetSpacingAfter(ptw(d.after));
            if (has(d, 'before')) pp.SetSpacingBefore(ptw(d.before));
            if (has(d, 'line')) pp.SetSpacingLine(Math.round(+d.line * 240), 'auto');
        }
    }

    function write(spec) {
        var doc = Api.GetDocument();
        var mode = spec.mode || 'append';
        var before = doc.GetElementsCount();
        if (mode === 'replace') {
            doc.RemoveAllElements();
        }
        LATER = [];
        WARNINGS = [];
        page(spec.page, doc);
        defaults(spec.defaults, doc);
        styles(spec.styles, doc);
        headingNumbers(spec.headingNumbers, doc);
        var made = blocksOf(spec.blocks || [], spec.defaults || null, doc);
        var at = null;
        if (mode === 'insert' || mode === 'replace_range') {
            at = Math.max(0, Math.min(+spec.at || 0, doc.GetElementsCount()));
            if (mode === 'replace_range') {
                var to = Math.min(has(spec, 'to') ? +spec.to : at, doc.GetElementsCount() - 1);
                for (var r = to; r >= at; r--) doc.RemoveElement(r);
            }
            for (var i = 0; i < made.length; i++) doc.AddElement(at + i, made[i]);
        } else {
            for (var j = 0; j < made.length; j++) doc.Push(made[j]);
        }
        var pending = LATER;
        LATER = [];
        for (var q = 0; q < pending.length; q++) pending[q]();
        var hadToc = false;
        for (var z = 0; z < (spec.blocks || []).length; z++) if ((spec.blocks[z] || {}).type === 'toc') hadToc = true;
        if (hadToc) { try { doc.UpdateAllTOC(); } catch (e) { } }

        // The editor puts an empty paragraph after every table it is given,
        // and clearing a document leaves one behind: gaps nobody asked for,
        // which on a copied form move everything below them down a line.
        // They go again -- except the one a document must end with.
        var ours = {};
        for (var o = 0; o < made.length; o++) { try { ours[made[o].GetInternalId()] = true; } catch (e) { } }
        var from = mode === 'replace' ? 0 : Math.max(0, (at === null ? before : at) - 1);
        var removed = 0;
        for (var x = doc.GetElementsCount() - 1; x >= from; x--) {
            var el = doc.GetElement(x);
            if (!el || el.GetClassType() !== 'paragraph') continue;
            var id = null;
            try { id = el.GetInternalId(); } catch (e) { }
            if (id && ours[id]) continue;
            if (el.GetText().replace(/[\r\n]/g, '').length > 0) continue;
            try { if (el.GetAllDrawingObjects().length > 0) continue; } catch (e) { }
            if (x === doc.GetElementsCount() - 1) {
                var prev = x > 0 ? doc.GetElement(x - 1) : null;
                if (!prev || prev.GetClassType() !== 'paragraph') continue;   // the paragraph a table-ended document needs
            }
            if (mode !== 'replace' && x < (at === null ? before : at) - 1) continue;
            try { doc.RemoveElement(x); removed++; } catch (e) { }
        }
        var first = at === null ? (mode === 'replace' ? 0 : Math.max(0, before - (removed > 0 && before > 0 ? 1 : 0))) : at;
        var answer = { ok: true, mode: mode, written: made.length, first_index: first, blocks_now: doc.GetElementsCount() };
        if (WARNINGS.length) answer.warnings = WARNINGS;
        return answer;
    }

    // ---- numbered headings: 1. / 1.1. / 1.1.1. ------------------------------------------------------
    // headingNumbers: true | {levels: 1-6 (default 3), indent: mm (default 10)}
    function headingNumbers(o, doc) {
        if (!o) return;
        if (o === true) o = {};
        var levels = Math.max(1, Math.min(6, +o.levels || 3));
        var gap = has(o, 'indent') ? +o.indent : 10;
        var numbering = doc.CreateNumbering('numbered');
        for (var i = 0; i < levels; i++) {
            var level = numbering.GetLevel(i);
            var text = '';
            for (var k = 1; k <= i + 1; k++) text += '%' + k + '.';
            level.SetCustomType('decimal', text, 'left');
            var pr = level.GetParaPr();
            pr.SetIndLeft(tw(gap + i * 2));
            pr.SetIndFirstLine(-tw(gap + i * 2));
            var style = doc.GetStyle('Heading ' + (i + 1));
            if (style) level.LinkWithStyle(style);
        }
    }

    // ---- change what is already there ---------------------------------------------------------
    function paragraphsOfTable(t, out) {
        for (var r = 0; r < t.GetRowsCount(); r++) {
            var row = t.GetRow(r);
            for (var c = 0; c < row.GetCellsCount(); c++) {
                var content = row.GetCell(c).GetContent();
                for (var k = 0; k < content.GetElementsCount(); k++) {
                    var el = content.GetElement(k), type = el.GetClassType();
                    if (type === 'paragraph') out.push(el);
                    else if (type === 'table') paragraphsOfTable(el, out);
                }
            }
        }
    }

    function paragraphsIn(doc, from, to) {
        var out = [];
        var n = doc.GetElementsCount();
        for (var i = Math.max(0, from); i <= Math.min(n - 1, to); i++) {
            var el = doc.GetElement(i), type = el.GetClassType();
            if (type === 'paragraph') out.push(el);
            else if (type === 'table') paragraphsOfTable(el, out);
        }
        return out;
    }

    // {target: {all: true} | {blocks: [from, to]} | {style: 'Heading 1'} | {tables: true} | {text: 'find', matchCase},
    //  set: {font, size, bold, italic, underline, color, highlight, align, before, after, line, indent, style, fill, ...},
    //  replace: {find, with, matchCase}, page, defaults, styles}
    function format(spec) {
        var doc = Api.GetDocument();
        var result = { ok: true };
        page(spec.page, doc);
        defaults(spec.defaults, doc);
        styles(spec.styles, doc);
        headingNumbers(spec.headingNumbers, doc);

        if (spec.replace) {
            var rep = spec.replace;
            var found = doc.Search(String(rep.find), !!rep.matchCase);
            result.replaced = found ? found.length : 0;
            if (result.replaced) doc.SearchAndReplace({ searchString: String(rep.find), replaceString: String(has(rep, 'with') ? rep['with'] : ''), matchCase: !!rep.matchCase });
        }

        var set = spec.set;
        if (set) {
            var t = spec.target || { all: true };
            if (has(t, 'text')) {
                var ranges = doc.Search(String(t.text), !!t.matchCase) || [];
                for (var i = 0; i < ranges.length; i++) styleRun(ranges[i], set);
                result.matches = ranges.length;
            } else {
                var paras;
                var last = doc.GetElementsCount() - 1;
                if (t.blocks && t.blocks.length) paras = paragraphsIn(doc, +t.blocks[0], has(t.blocks, 1) ? +t.blocks[1] : +t.blocks[0]);
                else if (t.tables) { paras = []; var tables = doc.GetAllTables() || []; for (var k = 0; k < tables.length; k++) paragraphsOfTable(tables[k], paras); }
                else paras = paragraphsIn(doc, 0, last);
                if (t.style) {
                    var wanted = String(t.style).toLowerCase();
                    paras = paras.filter(function (p) { try { var st = p.GetStyle(); return st && String(st.GetName()).toLowerCase() === wanted; } catch (e) { return false; } });
                }
                for (var j = 0; j < paras.length; j++) { styleParagraph(paras[j], set); styleRun(paras[j], set); }
                result.paragraphs = paras.length;
            }
        }
        return result;
    }

    return {
        tw: tw, ptw: ptw, emu: emu, mmOf: mmOf, rgb: rgb,
        page: page, defaults: defaults, columns: columns, styles: styles,
        paragraph: paragraph, fillParagraph: fillParagraph, table: table, image: image, textbox: textbox, shape: textbox, chart: chart, list: makeList,
        latex: latexToLinear,
        blocks: blocksOf, write: write, format: format
    };
})();
