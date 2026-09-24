// Reads a Word document for the assistant: each block in order, with its
// style and the fonts its text is set in, and tables as rows of cells.
// The fonts matter here more than anywhere: a paragraph set in SutonnyMJ or
// another Bijoy font holds ANSI letters that only look Bengali in that font,
// and read as text they are nonsense unless the reader knows.
// Arguments: args.from, args.to (block indexes, optional).
var doc = Api.GetDocument();
var count = doc.GetElementsCount();
var from = Math.max(0, args.from || 0);
var to = Math.min(count - 1, args.to === undefined || args.to === null ? count - 1 : args.to);

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

var blocks = [];
for (var i = from; i <= to; i++) {
    var e = doc.GetElement(i);
    var type = e.GetClassType();
    if (type === 'paragraph') {
        var style = '';
        try { var s = e.GetStyle(); style = s ? s.GetName() : ''; } catch (x) { }
        blocks.push({ index: i, type: 'paragraph', style: style, fonts: fontsOf(e), text: e.GetText().replace(/\r?\n$/, '') });
    } else if (type === 'table') {
        var rows = [];
        for (var r = 0; r < e.GetRowsCount(); r++) {
            var row = e.GetRow(r), cells = [];
            for (var c = 0; c < row.GetCellsCount(); c++) cells.push(textOf(row.GetCell(c).GetContent()));
            rows.push(cells);
        }
        blocks.push({ index: i, type: 'table', rows: rows });
    } else {
        blocks.push({ index: i, type: type });
    }
}
return { kind: 'document', blocks: count, from: from, to: to, content: blocks };
