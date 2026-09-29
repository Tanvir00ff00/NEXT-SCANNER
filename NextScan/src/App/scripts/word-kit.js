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

    function styleRun(run, s) {
        if (has(s, 'font')) run.SetFontFamily(String(s.font));
        if (has(s, 'size')) run.SetFontSize(Math.round(+s.size * 2));
        if (has(s, 'bold')) run.SetBold(!!s.bold);
        if (has(s, 'italic')) run.SetItalic(!!s.italic);
        if (has(s, 'underline')) run.SetUnderline(!!s.underline);
        if (has(s, 'strike')) run.SetStrikeout(!!s.strike);
        if (has(s, 'caps')) run.SetCaps(!!s.caps);
        if (has(s, 'smallCaps')) run.SetSmallCaps(!!s.smallCaps);
        if (has(s, 'spacing')) run.SetSpacing(ptw(s.spacing));
        if (has(s, 'vertAlign')) run.SetVertAlign(s.vertAlign);
        if (has(s, 'highlight')) run.SetHighlight(String(s.highlight));
        var c = rgb(s.color);
        if (c) run.SetColor(c[0], c[1], c[2], false);
        var sh = rgb(s.shade);
        if (sh) run.SetShd('clear', sh[0], sh[1], sh[2]);
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
        if (s.runs && s.runs.length) {
            for (var i = 0; i < s.runs.length; i++) {
                var r = s.runs[i];
                if (typeof r === 'string') r = { text: r };
                addText(p, r.text, inherit(own, r));
            }
        } else if (has(s, 'text')) addText(p, s.text, own);
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
            drawing.SetWrappingStyle(s.wrap || 'inFront');
            drawing.SetHorPosition('page', emu(s.x || 0));
            drawing.SetVerPosition('page', emu(s.y || 0));
        } else if (s.wrap) drawing.SetWrappingStyle(s.wrap);
    }

    function image(s) {
        if (!s.src) throw new Error('An image needs src.');
        var img = Api.CreateImage(String(s.src), emu(s.width || 30), emu(s.height || 30));
        place(img, s);
        return img;
    }

    function textbox(s, base) {
        var fill = rgb(s.fill);
        var stroke = s.border === undefined || s.border === 'none' || s.border === false ? null : (typeof s.border === 'object' ? s.border : {});
        var sc = stroke ? rgb(stroke.color) || [0, 0, 0] : null;
        var shape = Api.CreateShape('rect', emu(s.width || 40), emu(s.height || 10),
            fill ? Api.CreateSolidFill(Api.CreateRGBColor(fill[0], fill[1], fill[2])) : Api.CreateNoFill(),
            sc ? Api.CreateStroke(Math.round((stroke.size || 0.5) * 12700), Api.CreateSolidFill(Api.CreateRGBColor(sc[0], sc[1], sc[2]))) : Api.CreateStroke(0, Api.CreateNoFill()));
        var pad = s.padding === undefined ? 1 : s.padding;
        if (typeof pad === 'number') pad = [pad, pad, pad, pad];
        try { shape.SetPaddings(emu(pad[3]), emu(pad[0]), emu(pad[1]), emu(pad[2])); } catch (e) { }
        if (s.valign && VALIGN[s.valign]) try { shape.SetVerticalTextAlign(VALIGN[s.valign]); } catch (e) { }
        var content = shape.GetDocContent();
        var paras = s.paragraphs || [s];
        var own = inherit(base, s);
        content.RemoveAllElements();
        for (var i = 0; i < paras.length; i++) {
            var ps = typeof paras[i] === 'string' ? { text: paras[i] } : paras[i];
            if (ps === s) ps = { text: s.text, runs: s.runs, align: s.align };
            content.Push(paragraph(ps, own));
        }
        place(shape, s);
        return shape;
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
            } else if (type === 'image' || type === 'textbox') {
                var drawing = type === 'image' ? image(b) : textbox(b, base);
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
            } else throw new Error('Unknown block type ' + type + ' (use paragraph, heading, table, image, textbox, spacer, page_break).');
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
        page(spec.page, doc);
        defaults(spec.defaults, doc);
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
        return { ok: true, mode: mode, written: made.length, first_index: first, blocks_now: doc.GetElementsCount() };
    }

    return {
        tw: tw, ptw: ptw, emu: emu, mmOf: mmOf, rgb: rgb,
        page: page, defaults: defaults,
        paragraph: paragraph, fillParagraph: fillParagraph, table: table, image: image, textbox: textbox,
        blocks: blocksOf, write: write
    };
})();
