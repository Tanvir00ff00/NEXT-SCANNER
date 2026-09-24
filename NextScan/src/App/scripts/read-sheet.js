// Reads a workbook for the assistant: every sheet's used area as rows of
// cells, each cell with its shown value and, where there is one, its formula.
// Capped so a large sheet does not flood the conversation; the assistant can
// ask for a part by sheet and address.
// Arguments: args.sheet (name, optional), args.range ("A1:D20", optional).
var MAXROWS = 200, MAXCOLS = 40;

function letters(col) {
    var s = '';
    for (col++; col > 0; col = Math.floor((col - 1) / 26)) s = String.fromCharCode(65 + (col - 1) % 26) + s;
    return s;
}

function bounds(address) {
    // "$A$1:$D$4" or "A1:D4" or "A1"
    var m = /\$?([A-Z]+)\$?(\d+)(?::\$?([A-Z]+)\$?(\d+))?/.exec(String(address).toUpperCase());
    if (!m) return null;
    function col(s) { var n = 0; for (var i = 0; i < s.length; i++) n = n * 26 + (s.charCodeAt(i) - 64); return n - 1; }
    return { r1: +m[2] - 1, c1: col(m[1]), r2: +(m[4] || m[2]) - 1, c2: col(m[3] || m[1]) };
}

var sheets = Api.GetSheets();
var out = [];
for (var s = 0; s < sheets.length; s++) {
    var ws = sheets[s];
    var name = ws.GetName();
    if (args.sheet && args.sheet !== name) continue;

    var area = args.range ? bounds(args.range) : bounds(ws.GetUsedRange().GetAddress(false, false, 'xlA1', false));
    var rows = [];
    var truncated = false;
    if (area) {
        var r2 = Math.min(area.r2, area.r1 + MAXROWS - 1), c2 = Math.min(area.c2, area.c1 + MAXCOLS - 1);
        truncated = r2 < area.r2 || c2 < area.c2;
        for (var r = area.r1; r <= r2; r++) {
            var cells = [];
            for (var c = area.c1; c <= c2; c++) {
                var cell = ws.GetRangeByNumber(r, c);
                var value = cell.GetText ? cell.GetText() : cell.GetValue();
                var formula = '';
                try { formula = cell.GetFormula(); } catch (e) { }
                if (formula && String(formula).charAt(0) === '=') cells.push({ at: letters(c) + (r + 1), value: value, formula: formula });
                else if (value !== '' && value !== null && value !== undefined) cells.push({ at: letters(c) + (r + 1), value: value });
            }
            if (cells.length) rows.push(cells);
        }
    }
    out.push({
        sheet: name,
        area: area ? letters(area.c1) + (area.r1 + 1) + ':' + letters(area.c2) + (area.r2 + 1) : '',
        truncated: truncated,
        rows: rows
    });
}
return { kind: 'workbook', active: Api.GetActiveSheet().GetName(), sheets: out };
