// =============================================================================
// NextScan Studio - what the assistant can do in the document workspace
// Plan ref: docs/DOCUMENT_WORKSPACE.md, docs/AI_LAYER.md
//
// The assistant sees and works on the open documents through these tools, and
// only through them. Each is small and does one thing the operator could do by
// hand -- look at what is open, open a file, start a new one, read one, change
// one, save, export, close -- and each change goes through the editor, so it is
// on screen, undoable with Ctrl+Z, and marks the tab unsaved like any other.
//
// Changing a document is done with a script for ONLYOFFICE's document API: the
// Office JavaScript API its plugins and Document Builder use, which models have
// seen widely and which reaches everything the editor can do. Reading is done by
// fixed scripts here (scripts\read-*.js), so what the model is shown of a
// document is decided by this application, not improvised each time.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NextScan.Ai;
using Docs = NextScan.Docs;

namespace NextScan.App
{
    public class StudioDocTools
    {
        readonly StudioWorkspace _space;

        /// <summary>Makes a PDF of the session's scanned pages and returns its path, or null.</summary>
        public Func<string> PdfFromPages;

        /// <summary>Has Gemini read handwriting on a picture (JPEG, a note for it); null where there is no Gemini.</summary>
        public Func<byte[], string, Task<string>> HandwritingReader;

        /// <summary>True when the reader is there and worth offering: the assistant is not already Gemini.</summary>
        public Func<bool> HandwritingAvailable;

        /// <summary>Where exports go when the assistant names no folder.</summary>
        public Func<string> OutputFolder;

        public StudioDocTools(StudioWorkspace space) { _space = space; }

        // =====================================================================
        // What the model is told exists
        // =====================================================================
        public List<AiTool> Tools()
        {
            List<AiTool> tools = WorkspaceTools();

            // Jev, when there is a key for it: typed judgements about text,
            // fast and cheap, with probabilities (JevClient).
            if (JevClient.Ready)
                tools.Add(Tool("jev_ask",
                    "Asks Jev (TypeSafe's System One model) typed questions about a text and returns typed answers " +
                    "with probabilities and a confidence. Much faster and cheaper than thinking it through yourself, " +
                    "and calibrated: use it to classify, check, route or score text -- for example which of these " +
                    "documents are bills, whether a letter asks for payment, how urgent something is. Jev reads " +
                    "text only. Give either text, or document to use an open document's content. Question types: " +
                    "noul (yes/no; answer is noul, the probability of yes), choice (criteria maps each option to a " +
                    "description; answer is choice, probabilities, confidence), score (criteria is a list of 2 to 10 " +
                    "level descriptions, lowest first; answer is score, probabilities, confidence).",
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"text\":{\"type\":\"string\",\"description\":\"The text to judge.\"}," +
                    "\"document\":{\"type\":\"string\",\"description\":\"Instead of text: an open document's id, whose content is judged.\"}," +
                    "\"questions\":{\"type\":\"string\",\"description\":\"The questions as a JSON object, exactly as Jev takes them, e.g. " +
                    "{\\\"kind\\\": {\\\"type\\\": \\\"choice\\\", \\\"instructions\\\": \\\"What kind of document is this?\\\", " +
                    "\\\"criteria\\\": {\\\"bill\\\": \\\"A bill or invoice\\\", \\\"letter\\\": \\\"A letter\\\"}}, " +
                    "\\\"paid\\\": {\\\"type\\\": \\\"noul\\\", \\\"instructions\\\": \\\"Is it marked as paid?\\\"}}\"}}," +
                    "\"required\":[\"questions\"]}"));
            if (HandwritingReader != null && HandwritingAvailable != null && HandwritingAvailable())
                tools.Add(Tool("transcribe_scan",
                    "Has Gemini, the best reader of handwriting there is, read the handwriting on a scanned page and give you what it says, word for word, " +
                    "line by line, in the script it is written in, with a [?] after a word it is not sure of and [illegible] where nothing can be read. " +
                    "Use it for any page that has handwriting you are to read or copy -- a handwritten letter or note, the filled-in parts of a printed form, " +
                    "figures written by hand -- instead of reading the handwriting yourself, which you do less well. Printed text is not what it is for: you read " +
                    "that. For a form give a region (mm) for each handwritten area, or the whole page and say in note which part you want. Then use the " +
                    "transcription as the text you type into the document, and say where it was uncertain.",
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"page\":{\"type\":\"integer\",\"description\":\"Which scanned page, from 1. Leave out for the selected page or the preview.\"}," +
                    "\"region\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"[x, y, width, height] in mm from the page's top-left corner: only that part is read, at full resolution.\"}," +
                    "\"note\":{\"type\":\"string\",\"description\":\"What to read or what it is, e.g. 'the handwritten amount and name', 'Bengali, a letter to a customer'.\"}}}"));
            return tools;
        }

        List<AiTool> WorkspaceTools()
        {
            const string doc = "\"document\":{\"type\":\"string\",\"description\":\"The document's id from list_documents, e.g. doc1. Leave out for the one in front.\"}";
            return new List<AiTool>
            {
                Tool("list_documents",
                     "Lists the documents open in NextScan's workspace (id, name, kind, file, unsaved, which is in front) and the files opened recently. Call this first whenever you are not sure what is open.",
                     "{\"type\":\"object\",\"properties\":{}}"),

                Tool("open_document",
                     "Opens a Word, Excel, PowerPoint or PDF file from disk in a new tab and waits until it is ready. Returns its id.",
                     "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Full path of the file.\"}},\"required\":[\"path\"]}"),

                Tool("create_document",
                     "Starts a new, empty document in a new tab and waits until it is ready. It has no file until it is saved. Returns its id.",
                     "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"document\",\"workbook\",\"presentation\"],\"description\":\"document = Word, workbook = Excel, presentation = PowerPoint.\"}},\"required\":[\"kind\"]}"),

                Tool("read_document",
                     "Reads a document's content. Word: each block in order with its index (paragraphs with style, fonts and text; tables as rows of cells), optionally only blocks from..to, and whether the document is empty. With detail='layout' a Word document is read with its formatting in exactly the form write_document takes (page size and margins in mm, alignment, spacing in pt, indents and tab stops in mm, runs with font, size, bold, colour; tables with column widths in mm, spans, merged cells, borders, fills) -- use that before changing the look of anything, and to check your work. Excel: each sheet's used cells with values and formulas, optionally one sheet and range. PowerPoint: each slide's text. PDF: the text of the file. Read before you change anything, and again afterwards to check.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"detail\":{\"type\":\"string\",\"enum\":[\"text\",\"layout\"],\"description\":\"Word: text (default) or layout.\"},\"from\":{\"type\":\"integer\",\"description\":\"Word: first block index.\"},\"to\":{\"type\":\"integer\",\"description\":\"Word: last block index.\"},\"sheet\":{\"type\":\"string\",\"description\":\"Excel: sheet name.\"},\"range\":{\"type\":\"string\",\"description\":\"Excel: an address such as A1:F30.\"}}}"),

                Tool("write_document", WriteHelp,
                     "{\"type\":\"object\",\"properties\":{" + doc + "," +
                     "\"mode\":{\"type\":\"string\",\"enum\":[\"replace\",\"append\",\"insert\",\"replace_range\"],\"description\":\"replace: the whole document becomes these blocks. append: after the last block. insert: before block index at. replace_range: blocks at..to are replaced.\"}," +
                     "\"at\":{\"type\":\"integer\",\"description\":\"insert / replace_range: the first block index.\"}," +
                     "\"to\":{\"type\":\"integer\",\"description\":\"replace_range: the last block index replaced.\"}," +
                     "\"page\":{\"type\":\"object\",\"description\":\"Optional page setup: {size: 'A4'|'Letter'|'Legal'|'A5'|[width_mm, height_mm], orientation: 'portrait'|'landscape', margins: [top, right, bottom, left] mm}.\",\"properties\":{\"size\":{\"type\":\"string\"},\"orientation\":{\"type\":\"string\"},\"width\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},\"margins\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}}}," +
                     "\"defaults\":{\"type\":\"object\",\"description\":\"Optional body text for the whole document: {font, size (pt), after (pt), line (multiple)}.\",\"properties\":{\"font\":{\"type\":\"string\"},\"size\":{\"type\":\"number\"},\"after\":{\"type\":\"number\"},\"before\":{\"type\":\"number\"},\"line\":{\"type\":\"number\"}}}," +
                     "\"blocks\":{\"type\":\"array\",\"description\":\"The blocks, in order, as described above.\",\"items\":{\"type\":\"object\",\"additionalProperties\":true," +
                     "\"properties\":{\"type\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"},\"runs\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":true}}," +
                     "\"columns\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}},\"rows\":{\"type\":\"array\",\"items\":{}}}}}," +
                     "\"spec\":{\"type\":\"string\",\"description\":\"Instead of the fields above: the whole description as one JSON string {mode, at, to, page, defaults, blocks}. Use this if your blocks would otherwise lose fields.\"}," +
                     "\"look\":{\"type\":\"boolean\",\"description\":\"The pages are shown to you after the write (default true). Set false only for the first of several writes in a row, and look at the end.\"},\"summary\":{\"type\":\"string\",\"description\":\"A few words for the operator saying what this writes, in their language.\"}},\"required\":[\"summary\"]}"),

                Tool("format_document",
                     "Changes the look of what is already in a Word document, in place, without rewriting it -- the way to change a font, a size, a colour, an alignment, spacing, a style, or to replace a word everywhere. target says what: {all:true} (default), {blocks:[from,to]} (block indexes as read_document gives them), {style:'Heading 1'} (every paragraph in that style), {tables:true} (everything in tables), or {text:'find', matchCase} (just those words). set says how: font, size (pt), bold, italic, underline, strike, color, highlight, caps, spacing, vertAlign, align, before, after, line, indent, style, fill, border, keepNext, pageBreakBefore. replace: {find, with, matchCase} replaces text everywhere. page, defaults, styles and headingNumbers are the same as in write_document (page setup, columns, headers and footers, watermark; the body text; the definition of Normal, Heading 1...; numbered headings). Everything else in Word -- shapes, charts, equations, sections, footnotes -- is in write_document's blocks, and anything beyond that is edit_document with the API (ask word_api). Shows you the pages afterwards.",
                     "{\"type\":\"object\",\"properties\":{" + doc + "," +
                     "\"target\":{\"type\":\"object\",\"additionalProperties\":true,\"description\":\"What to change: {all:true} | {blocks:[from,to]} | {style:'Heading 1'} | {tables:true} | {text:'find', matchCase:true}.\"}," +
                     "\"set\":{\"type\":\"object\",\"additionalProperties\":true,\"description\":\"The formatting: {font, size, bold, italic, underline, color, highlight, align, before, after, line, indent, style, fill, ...}.\"}," +
                     "\"replace\":{\"type\":\"object\",\"additionalProperties\":true,\"description\":\"{find, with, matchCase}: replace this text everywhere.\"}," +
                     "\"page\":{\"type\":\"object\",\"additionalProperties\":true,\"description\":\"Page setup, as in write_document.\"}," +
                     "\"defaults\":{\"type\":\"object\",\"additionalProperties\":true,\"description\":\"Body text defaults, as in write_document.\"}," +
                     "\"styles\":{\"type\":\"object\",\"additionalProperties\":true,\"description\":\"{'Heading 1': {font, size, bold, color, before, after, ...}, 'Normal': {...}}.\"}," +
                     "\"headingNumbers\":{\"description\":\"true, or {levels: 3}: number the headings 1. / 1.1. / 1.1.1.\"}," +
                     "\"look\":{\"type\":\"boolean\",\"description\":\"Show the pages afterwards (default true).\"}," +
                     "\"summary\":{\"type\":\"string\",\"description\":\"A few words for the operator saying what this changes, in their language.\"}},\"required\":[\"summary\"]}"),

                Tool("word_api",
                     "What to know about Word's document API, when write_document and format_document do not reach what you need and you are going to write a script for edit_document. topic: 'overview' (units and how the API works), 'write' (everything write_document and format_document can describe), or a subject -- fonts, page, columns, sections, headers_footers, headings, styles, lists, shapes, charts, math, footnotes, links, watermark, images, find, tables, tabs, comments, forms, direction -- each a tested recipe; or a class name (Document, Section, Paragraph, Run, Range, Table, Cell, Row, Style, Shape, Chart, Image, Hyperlink, Numbering, NumberingLevel, DocumentContent, TextPr, ParaPr, Api...) for that class's method names taken from this very editor; or any word (column, border, footnote) to find the methods with it in their name. Leave topic out for the list. Method names are case sensitive and are the only reliable way to know a call exists.",
                     "{\"type\":\"object\",\"properties\":{\"topic\":{\"type\":\"string\",\"description\":\"A subject, a class name, or a word to search the method names for.\"}}}"),

                Tool("look_at_document",
                     "Shows you a document as pictures: a Word document or PDF laid out exactly as it will print, an Excel workbook as its sheets (colours, borders, column widths, merged cells, cell addresses), a PowerPoint as its slides. This is how you see what you made: reading text tells you what is in a document, only looking tells you whether it is right -- alignment, spacing, sizes, what runs onto a second page, what overlaps. Look at what you wrote as a person would, judge it against what you were asked for (or against the scan or the file you are copying, side by side with look_at_scan or the attached pictures) and fix what differs, then look again. The picture says how large the page is and how many pixels a millimetre is, so you can measure positions and gaps in it. With grid: true a blue grid is drawn over a Word page or slide (a line every 10 mm from the top-left corner, numbered every 50) to read positions straight off it. With region: [x, y, width, height] in mm you get a close-up of that part of the page, drawn larger -- use it to check small things: a dotted line, a border, the exact size of a letter. Returns the page count; the pictures follow.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"page\":{\"type\":\"integer\",\"description\":\"Which page (or sheet picture, or slide), from 1. Leave out for the first two.\"},\"grid\":{\"type\":\"boolean\",\"description\":\"Draw a millimetre grid over the page.\"},\"region\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"[x, y, width, height] in mm from the page's top-left corner: a close-up of that part. Give page with it.\"}}}"),

                Tool("edit_document",
                     "Changes a document by running a script for ONLYOFFICE's document API (the Office JavaScript API of ONLYOFFICE Document Builder and plugins). The script is the body of a function given Api; whatever it returns comes back to you. The change is shown at once and can be undone by the operator. Not available for PDF. In Word scripts the helpers NS are there too: NS.paragraph(spec), NS.table(spec) and NS.textbox(spec) make blocks from the same specs write_document takes, NS.fillParagraph(existingParagraph, spec), NS.page({...}), and NS.tw(mm) / NS.ptw(pt) convert to twips. Prefer write_document for new content and for layout; use scripts for targeted changes (find and replace text, format one run, delete a block).",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"script\":{\"type\":\"string\",\"description\":\"JavaScript using Api, e.g. var d = Api.GetDocument(); var p = Api.CreateParagraph(); p.AddText('Hello'); d.Push(p); return d.GetElementsCount();\"},\"look\":{\"type\":\"boolean\",\"description\":\"The document is shown to you after the change (default true). Set false only for the first of several changes in a row.\"},\"summary\":{\"type\":\"string\",\"description\":\"A few words for the operator saying what this changes, in their language.\"}},\"required\":[\"script\",\"summary\"]}"),

                Tool("get_selection",
                     "Returns the text the operator has selected in a document, for requests like 'rewrite this' or 'translate the selected part'.",
                     "{\"type\":\"object\",\"properties\":{" + doc + "}}"),

                Tool("save_document",
                     "Saves a document to its own file. A new document has no file: give a full path ending in .docx, .xlsx or .pptx, or leave it out and the operator is asked where. Only save when the operator asked for it.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"path\":{\"type\":\"string\",\"description\":\"Full path, only for a document that has no file yet.\"}}}"),

                Tool("export_document",
                     "Writes a copy of a document in another format without changing the open one: pdf, docx, odt, rtf, txt, xlsx, ods, csv, pptx, odp. The format is the path's extension. Without a folder the copy goes to NextScan's output folder. Returns the path written.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"path\":{\"type\":\"string\",\"description\":\"A full path, or just a file name such as bill.pdf.\"},\"overwrite\":{\"type\":\"boolean\",\"description\":\"Replace a file that is already there. Only if the operator agreed.\"}},\"required\":[\"path\"]}"),

                Tool("show_document",
                     "Brings a document's tab to the front so the operator sees it.",
                     "{\"type\":\"object\",\"properties\":{" + doc + "},\"required\":[\"document\"]}"),

                Tool("close_document",
                     "Closes a document's tab. One with unsaved changes is not closed unless discard_changes is true, which you must only set when the operator said to throw the changes away.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"discard_changes\":{\"type\":\"boolean\"}},\"required\":[\"document\"]}"),

                Tool("pdf_from_scanned_pages",
                     "Makes a PDF of the pages scanned in this session, saves it in the output folder and opens it in a new tab. Returns its id and path.",
                     "{\"type\":\"object\",\"properties\":{}}"),

                Tool("look_at_scan",
                     "Shows you a page scanned on the scanner as a picture (not a file the operator attached: those pictures are already in their message): one of the pages scanned in this session (page, from 1), or with no page the one the operator has selected or, failing that, the preview on the scanner glass. Use it to read or copy a scanned document -- to type out handwriting, or to rebuild a form as a Word document -- and to compare your document with it. With grid: true a blue millimetre grid is drawn over the page (a line every 10 mm from the top-left corner, numbered every 50) so you can read positions and sizes off it instead of estimating. With region: [x, y, width, height] in mm you get a close-up cut from the scan at its full resolution -- use it to read small print, to see whether a line is dotted or dashed, to measure a box, to see how a letter is shaped. Says how many pages there are and how big the page is; the picture follows.",
                     "{\"type\":\"object\",\"properties\":{\"page\":{\"type\":\"integer\",\"description\":\"Which scanned page, from 1. Leave out for the selected page or the preview.\"},\"grid\":{\"type\":\"boolean\",\"description\":\"Draw a millimetre grid over the page.\"},\"region\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"[x, y, width, height] in mm from the page's top-left corner: a close-up of that part.\"}}}"),

                Tool("find_scan_pictures",
                     "Finds the pictures on a scanned page -- a logo, an emblem, a stamp, a photograph -- as opposed to printed words, which are text to type. Returns each one's place and size in mm, and a picture of the page with each boxed and numbered so you can check. The application finds them by analysing the page (ink that is coloured or solid in a block too big to be a letter), which is more exact than estimating from a picture; but they are candidates: confirm each, and use crop_scan with a region of your own if one is missed or boxed wrongly. Use it whenever a scan you are copying has anything that is not type or lines.",
                     "{\"type\":\"object\",\"properties\":{\"page\":{\"type\":\"integer\",\"description\":\"Which scanned page, from 1. Leave out for the selected page or the preview.\"}}}"),

                Tool("crop_scan",
                     "Takes a picture out of a scanned page and saves it as a PNG file, to be placed in a Word document with write_document's image block. Give picture (a number from find_scan_pictures) or region [x, y, width, height] in mm. By default the white margin round it is trimmed off (trim: false keeps the region as given); transparent: true makes the white see-through, for a logo that will sit on shading or over other things. Returns the file's path and its real size in mm on the scan -- use that width and height, and the position you measured, so it comes out the size and place it has on the original -- and shows you the picture taken so you can check its edges.",
                     "{\"type\":\"object\",\"properties\":{\"picture\":{\"type\":\"integer\",\"description\":\"A picture number from find_scan_pictures.\"},\"region\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"description\":\"[x, y, width, height] in mm from the page's top-left corner. Instead of picture.\"},\"page\":{\"type\":\"integer\",\"description\":\"Which scanned page, for a region. Leave out for the selected page or the preview.\"},\"trim\":{\"type\":\"boolean\",\"description\":\"Cut off the white margin (default true).\"},\"transparent\":{\"type\":\"boolean\",\"description\":\"Make the white see-through.\"},\"name\":{\"type\":\"string\",\"description\":\"A short name for the file, e.g. bank-logo.\"}}}"),
            };
        }

        // ---- what the shell hands over about scans ---------------------------

        /// <summary>How many pages this session has scanned.</summary>
        public Func<int> ScanCount;

        /// <summary>A scanned page by index from 0.</summary>
        public Func<int, NextScan.Core.RawImage> ScanPage;

        /// <summary>The page the operator is looking at (selected, or the preview), with a name for it; null when there is none.</summary>
        public Func<Tuple<NextScan.Core.RawImage, string>> CurrentScan;

        /// <summary>
        /// The whole vocabulary of write_document, in the tool's own
        /// description so every provider sees it the same way. Kept in step
        /// with scripts\word-kit.js, which is what reads it.
        /// </summary>
        const string WriteHelp =
            "Writes Word content from a description of it: the way to make a new document, rebuild a scanned one, or " +
            "replace part of one. Exact and repeatable -- measurements are in the units on a ruler, and the editor's " +
            "own units are worked out for you. Lengths in mm, font sizes and spacing in pt, colours '#RRGGBB'.\n" +
            "Blocks (a plain string is a paragraph):\n" +
            "- paragraph: {type:'paragraph', text, align:'left'|'center'|'right'|'justify', before, after (pt), " +
            "line: 1.0 (multiple) | {exact: pt} | {atLeast: pt}, indent: {left, right, first, hanging} (mm), " +
            "tabs: [{pos (mm), align:'left'|'right'|'center'|'decimal'}], border: {top|bottom|left|right: size_pt | " +
            "{size, style:'single'|'dotted'|'dashed'|'double', color}}, fill, style:'Heading 1', keepNext, pageBreakBefore, " +
            "plus text formatting for the whole paragraph: font, size, bold, italic, underline, strike, caps, smallCaps, " +
            "spacing (pt between letters), color, highlight. Mixed formatting: runs: [{text, bold, size, font, color, ...}] " +
            "instead of text. '\\t' in text is a tab, '\\n' a line break.\n" +
            "- heading: {type:'heading', level: 1-3, text}.\n" +
            "- table: {type:'table', columns: [mm, mm, ...] (the exact width of each column), rows: [{cells: [...], " +
            "height (mm, at least), exact: true (height fixed), fill, align, valign} or just [cell, ...]], borders: " +
            "'all' (default) | 'none' | 'outer' | 'inner' | 'horizontal' | {top, bottom, left, right, insideH, insideV: " +
            "true|false|size_pt}, border: {size (pt, default 0.5), style, color} for the lines, padding: mm | [top, right, " +
            "bottom, left], align:'left'|'center'|'right', indent (mm), cellAlign, valign, and text formatting for the " +
            "whole table (font, size, bold...)}. A cell is a string, or {text | runs | paragraphs: [paragraph, ...], " +
            "span (columns it covers), rowspan (rows it covers), align, valign:'top'|'center'|'bottom', fill, border: " +
            "size_pt | {top, bottom, left, right: size_pt | 'none' | {size, style, color}}, and text formatting}. A cell " +
            "covered by a rowspan from above is left out of its row. Tables are the way to put things side by side: a " +
            "form's label and its dotted line, a heading at the left and a number at the right, a row of boxes to write " +
            "digits in (narrow columns, each cell border: 0.5).\n" +
            "- image: {type:'image', src: a file path or data: URL, width, height (mm), align, or x, y (mm from the " +
            "page's top-left corner) to float it}.\n" +
            "- textbox: {type:'textbox', x, y, width, height (mm), text | runs | paragraphs, border: size_pt | 'none', " +
            "fill, padding (mm), valign} -- floats at that place on the page; only for things that truly sit apart.\n" +
            "- spacer: {type:'spacer', height (mm)} -- an exact gap. page_break: {type:'page_break'}.\n" +
            "page: {size:'A4' | [width, height] (mm), orientation, margins: [top, right, bottom, left] (mm)}; defaults: " +
            "{font, size, after, line} for the body text. A new document is Letter with 10 pt after every paragraph " +
            "until you say otherwise: give page and defaults when you make one.\n" +
            "More (all in the same units): page: {columns, header, footer, firstHeader, firstFooter, differentFirstPage, watermark, startNumber}; " +
            "runs {page:true} (page number), {pages:true}, {text, link:'https://..'} (hyperlink); paragraph footnote:'text'; blocks list {kind:'bullet'|'number'|'lower-alpha'|'upper-roman', items}, " +
            "shape {shape:'ellipse'|'roundRect'|'star5'|'rightArrow'|'line'..., width, height, fill, line, text, x, y, rotation}, chart {chart:'column'|'line'|'pie'..., title, categories, series:[{name, values}]}, " +
            "equation {text: LaTeX}, toc, section_break {kind, page, columns}, column_break; styles {'Heading 1': {...}}; headingNumbers:true. " +
            "word_api topic 'write' says all of it; word_api can also tell you how to do anything the editor can do beyond that, by script.\n" +
            "Returns the index of the first block written and how many blocks the document now has, and shows you the pages as they now look: look at them.";

        static AiTool Tool(string name, string description, string schema)
        {
            return new AiTool { Name = name, Description = description, ParametersJson = schema };
        }

        // =====================================================================
        // Doing it
        // =====================================================================
        public async Task<AiToolResult> Run(AiToolCall call)
        {
            var result = new AiToolResult { CallId = call.Id, Name = call.Name };
            Dictionary<string, object> args;
            try { args = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) ?? new Dictionary<string, object>(); }
            catch { args = new Dictionary<string, object>(); }

            try
            {
                switch (call.Name)
                {
                    case "list_documents": await List(result); break;
                    case "write_document": await Write(Pick(args), args, result); break;
                    case "look_at_document": await LookAtDocument(Pick(args), args, result); break;
                    case "format_document": await Format(Pick(args), args, result); break;
                    case "word_api": WordApi(Str(args, "topic"), result); break;
                    case "look_at_scan": await LookAtScan(args, result); break;
                    case "find_scan_pictures": await FindScanPictures(args, result); break;
                    case "crop_scan": CropScan(args, result); break;
                    case "transcribe_scan": await TranscribeScan(args, result); break;
                    case "open_document": await Open(Str(args, "path"), result); break;
                    case "create_document": await Create(Str(args, "kind"), result); break;
                    case "read_document": await Read(Pick(args), args, result); break;
                    case "edit_document": await Edit(Pick(args), Str(args, "script"), Str(args, "summary"), args, result); break;
                    case "get_selection": await Selection(Pick(args), result); break;
                    case "save_document": await Save(Pick(args), Str(args, "path"), result); break;
                    case "export_document": await Export(Pick(args), Str(args, "path"), Bool(args, "overwrite"), result); break;
                    case "show_document": Show(Pick(args), result); break;
                    case "close_document": Close(Pick(args), Bool(args, "discard_changes"), result); break;
                    case "pdf_from_scanned_pages": await FromScans(result); break;
                    case "jev_ask": await JevAsk(args, result); break;
                    default: throw new ToolTrouble("There is no tool called " + call.Name + ".");
                }
            }
            catch (ToolTrouble ex) { Fail(result, ex.Message); }
            catch (Docs.DocsTrouble ex) { Fail(result, ex.Message); }
            catch (Exception ex) { Fail(result, ex.GetType().Name + ": " + ex.Message); }
            return result;
        }

        static void Fail(AiToolResult result, string message)
        {
            result.IsError = true;
            result.Content = message;
            if (result.Display.Length == 0) result.Display = message;
            else result.Display += " — " + message;
        }

        // ---- the documents -------------------------------------------------

        async Task List(AiToolResult result)
        {
            var open = new List<object>();
            foreach (DocTab tab in _space.Tabs)
            {
                var view = tab.Surface as Docs.DocView;
                var row = new Dictionary<string, object>
                {
                    { "id", tab.Id }, { "name", tab.Title }, { "kind", KindName(tab.Kind) },
                    { "file", tab.Path.Length > 0 ? tab.Path : null },
                    { "unsaved", tab.Dirty }, { "in_front", tab == _space.Current },
                    { "ready", view != null && view.Ready },
                };
                if (view != null && view.Ready && tab.Kind == DocKind.Word)
                {
                    // Enough to know whether there is anything in it without
                    // reading it: an empty document is a different request.
                    try
                    {
                        string glance = await view.RunScriptAsync(Glance, false);
                        var seen = new JavaScriptSerializer().DeserializeObject(glance) as Dictionary<string, object>;
                        if (seen != null) foreach (var kv in seen) row[kv.Key] = kv.Value;
                    }
                    catch { }
                }
                open.Add(row);
            }
            var recent = new List<string>();
            foreach (string path in DocRecent.Read()) { if (recent.Count >= 12) break; recent.Add(path); }

            result.Content = Json(new Dictionary<string, object>
            {
                { "open", open }, { "home_in_front", _space.Current == null }, { "recent_files", recent },
            });
            result.Display = open.Count == 0 ? "Looked at the workspace: nothing open"
                           : "Looked at the open documents (" + open.Count + ")";
        }

        /// <summary>A Word document at a glance: how many blocks and pages, whether it is empty, how it starts.</summary>
        internal const string Glance =
            "var d = Api.GetDocument(), n = d.GetElementsCount(), text = '';" +
            "for (var i = 0; i < n && text.length < 80; i++) { var e = d.GetElement(i); if (e.GetText) text += e.GetText().replace(/\\s+/g, ' '); }" +
            "var pages = 0; try { pages = d.GetPageCount(); } catch (x) { }" +
            "var s = d.GetFinalSection();" +
            "return { blocks: n, pages: pages, empty: text.replace(/\\s/g, '').length === 0, starts_with: text.slice(0, 80).trim()," +
            " page_mm: [Math.round(s.GetPageWidth() * 25.4 / 1440), Math.round(s.GetPageHeight() * 25.4 / 1440)] };";

        async Task Open(string path, AiToolResult result)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ToolTrouble("Give the full path of the file to open.");
            if (!File.Exists(path)) throw new ToolTrouble("There is no file at " + path + ".");
            if (!DocKinds.CanOpen(path)) throw new ToolTrouble("NextScan does not open " + Path.GetExtension(path) + " files.");

            result.Display = "Opening " + Path.GetFileName(path);
            DocTab tab = _space.Open(path);
            if (tab == null) throw new ToolTrouble("The file could not be opened.");
            await Ready(tab);
            result.Content = Json(new Dictionary<string, object> { { "id", tab.Id }, { "name", tab.Title }, { "kind", KindName(tab.Kind) } });
            result.Display = "Opened " + tab.Title;
        }

        async Task Create(string kind, AiToolResult result)
        {
            DocKind k = kind == "workbook" ? DocKind.Sheet : kind == "presentation" ? DocKind.Slides : DocKind.Word;
            DocTab tab = _space.New(k);
            if (tab == null) throw new ToolTrouble("The new document could not be started.");
            await Ready(tab);
            result.Content = Json(new Dictionary<string, object> { { "id", tab.Id }, { "name", tab.Title }, { "kind", KindName(tab.Kind) } });
            result.Display = "Started " + tab.Title;
        }

        async Task Read(DocTab tab, Dictionary<string, object> args, AiToolResult result)
        {
            Docs.DocView view = await Ready(tab);
            result.Display = "Read " + tab.Title;

            if (tab.Kind == DocKind.Pdf)
            {
                string text = await view.PdfTextAsync();
                if (text.Length > 60000) text = text.Substring(0, 60000) + "\n[... the rest is not shown]";
                result.Content = Json(new Dictionary<string, object> { { "kind", "pdf" }, { "text", text } });
                return;
            }

            string script = tab.Kind == DocKind.Sheet ? Script("read-sheet.js")
                          : tab.Kind == DocKind.Slides ? Script("read-slides.js")
                          : Script("read-word.js");
            var passed = new Dictionary<string, object>();
            foreach (string key in new[] { "from", "to", "sheet", "range", "detail" })
            {
                object v;
                if (args.TryGetValue(key, out v) && v != null) passed[key] = v;
            }
            string json = await view.RunScriptAsync("var args = " + Json(passed) + ";\n" + script, false);
            if (json.Length > 80000)
                json = json.Substring(0, 80000) + " [... cut short; ask for a smaller part with from/to or sheet/range]";
            result.Content = json;
        }

        async Task Edit(DocTab tab, string script, string summary, Dictionary<string, object> args, AiToolResult result)
        {
            if (string.IsNullOrWhiteSpace(script)) throw new ToolTrouble("The script is empty.");
            if (tab.Kind == DocKind.Pdf) throw new ToolTrouble("A PDF cannot be changed by script. Export it to docx and open that instead.");
            Docs.DocView view = await Ready(tab);
            result.Display = (string.IsNullOrWhiteSpace(summary) ? "Changed" : summary.Trim()) + " — " + tab.Title;
            _space.Select(tab);
            // The kit rides along in Word, as NS: the same builder write_document
            // uses, for a script that wants one table or one paragraph made
            // the way the rest were.
            if (tab.Kind == DocKind.Word) script = Script("word-kit.js") + "\n" + script;
            string answer;
            try { answer = await view.RunScriptAsync(script, true); }
            catch (Docs.DocsTrouble ex)
            {
                // The error, not the editor's call stack: the stack is the same
                // few frames of NextScan's own plumbing every time.
                string why = ex.Message.Split('\n')[0].Trim();
                throw new ToolTrouble(why + ". " + ApiHint(why) + "Whatever the script did before this line is still in the document " +
                                      "(the operator can undo it): read the document before trying again.");
            }
            result.Content = Json(new Dictionary<string, object> { { "ok", true }, { "returned", answer } });
            await AutoLook(tab, args, result, false);
        }

        void WordApi(string topic, AiToolResult result)
        {
            result.Content = StudioWordApi.Answer(topic);
            result.Display = "Looked up Word's API" + (topic.Trim().Length > 0 ? ": " + topic.Trim() : "");
        }

        async Task Format(DocTab tab, Dictionary<string, object> args, AiToolResult result)
        {
            if (tab.Kind != DocKind.Word) throw new ToolTrouble("format_document changes Word documents. Use edit_document for " + KindName(tab.Kind) + "s.");
            var picked = new Dictionary<string, object>();
            foreach (string key in new[] { "target", "set", "replace", "page", "defaults", "styles", "headingNumbers" })
            {
                object v;
                if (args.TryGetValue(key, out v) && v != null)
                {
                    // Some models send an object as a string of JSON.
                    if (v is string && ((string)v).TrimStart().StartsWith("{", StringComparison.Ordinal)) v = ParseLoose((string)v, key);
                    picked[key] = v;
                }
            }
            if (picked.Count == 0) throw new ToolTrouble("Nothing to change: give set (with target), replace, page, defaults, styles or headingNumbers.");
            if (picked.ContainsKey("set") && !picked.ContainsKey("target")) picked["target"] = new Dictionary<string, object> { { "all", true } };

            Docs.DocView view = await Ready(tab);
            string summary = Str(args, "summary");
            result.Display = (summary.Trim().Length > 0 ? summary.Trim() : "Changed the formatting") + " " + (char)8212 + " " + tab.Title;
            _space.Select(tab);
            string answer;
            try { answer = await view.RunScriptAsync(Script("word-kit.js") + "\nreturn NS.format(" + Json(picked) + ");", true); }
            catch (Docs.DocsTrouble ex)
            {
                string why = ex.Message.Split('\n')[0].Trim();
                throw new ToolTrouble(why + ". " + ApiHint(why) + "What was done before the problem is in the document (the operator can undo): read it before trying again.");
            }
            result.Content = answer;
            await AutoLook(tab, args, result, false);
        }

        async Task Write(DocTab tab, Dictionary<string, object> args, AiToolResult result)
        {
            if (tab.Kind != DocKind.Word) throw new ToolTrouble("write_document writes Word documents. Use edit_document for " + KindName(tab.Kind) + "s.");

            // One spec, however it came: as fields, or as one JSON string.
            Dictionary<string, object> spec;
            string packed = Str(args, "spec");
            if (packed.Trim().Length > 0)
            {
                spec = ParseLoose(packed, "spec") as Dictionary<string, object>;
                if (spec == null) throw new ToolTrouble("spec must be a JSON object {mode, page, defaults, blocks}.");
            }
            else
            {
                var picked = new Dictionary<string, object>();
                foreach (string key in new[] { "mode", "at", "to", "page", "defaults", "styles", "blocks" })
                {
                    object v;
                    if (args.TryGetValue(key, out v) && v != null) picked[key] = v;
                }
                // Through JSON once more: the arguments were read into
                // ArrayLists, and everything below works on object[].
                spec = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(Json(picked));
                // Some models send the list itself as a string of JSON.
                object listed;
                if (spec.TryGetValue("blocks", out listed) && listed is string)
                {
                    spec["blocks"] = ParseLoose((string)listed, "blocks");
                }
            }
            object blocks;
            if (!spec.TryGetValue("blocks", out blocks) || !(blocks is object[]) || ((object[])blocks).Length == 0)
            {
                if (!spec.ContainsKey("page") && !spec.ContainsKey("defaults") && !spec.ContainsKey("styles"))
                    throw new ToolTrouble("There is nothing to write: give blocks (or page, defaults or styles to change only those).");
                spec["blocks"] = new object[0];
            }
            if (!spec.ContainsKey("mode")) spec["mode"] = "append";

            // Pictures named by a file become data: URLs here, since the
            // editor cannot read the disk.
            Inline((object[])spec["blocks"]);

            Docs.DocView view = await Ready(tab);
            string summary = Str(args, "summary");
            result.Display = (summary.Trim().Length > 0 ? summary.Trim() : "Wrote") + " — " + tab.Title;
            _space.Select(tab);
            string answer;
            try { answer = await view.RunScriptAsync(Script("word-kit.js") + "\nreturn NS.write(" + Json(spec) + ");", true); }
            catch (Docs.DocsTrouble ex)
            {
                string why = ex.Message.Split('\n')[0].Trim();
                throw new ToolTrouble(why + ". Blocks written before the problem may be in the document (the operator can undo): " +
                                      "read it before trying again.");
            }
            result.Content = answer;
            await AutoLook(tab, args, result, SpecMode(spec) == "append");
        }

        /// <summary>
        /// Reads JSON a model wrote, forgivingly. A model writing seven kilobytes
        /// of it in one go slips: a trailing comma, a raw line break inside a
        /// string, a closing bracket lost where its output was cut. Those are
        /// mended, and read again. What still cannot be read is reported with
        /// the text around the fault, since "expected ':'" and a position
        /// number tell a model nothing about where in seven kilobytes to look.
        /// </summary>
        static object ParseLoose(string text, string what)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
            try { return serializer.DeserializeObject(text); }
            catch (Exception first)
            {
                try { return serializer.DeserializeObject(RepairJson(text)); }
                catch
                {
                    string why = first.Message;
                    int paren = why.IndexOf(" (", StringComparison.Ordinal);
                    if (paren > 0) why = why.Substring(0, paren);
                    var at = System.Text.RegularExpressions.Regex.Match(first.Message, @"\((\d+)\)");
                    int pos;
                    string near = "";
                    if (at.Success && int.TryParse(at.Groups[1].Value, out pos) && pos >= 0 && pos <= text.Length)
                    {
                        int from = Math.Max(0, pos - 70), to = Math.Min(text.Length, pos + 40);
                        near = " Near character " + pos + " of " + text.Length + ": ..." + text.Substring(from, pos - from) + " <<HERE>> " + text.Substring(pos, to - pos) + "...";
                    }
                    throw new ToolTrouble(what + " is not valid JSON (" + why.TrimEnd('.') + ")." + near.Replace("\r", " ").Replace("\n", " ") +
                                          " Send the description again with that fixed; keep each string on one line.");
                }
            }
        }

        static string RepairJson(string s)
        {
            // An empty table cell written as {""} or {"""}: braces round nothing.
            // A cell is a string, and an empty one is "".
            s = System.Text.RegularExpressions.Regex.Replace(s, "\\{\\s*\"{1,3}\\s*\\}", "\"\"");
            var o = new StringBuilder(s.Length + 16);
            var closers = new Stack<char>();
            bool inString = false, escaped = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escaped) { o.Append(c); escaped = false; continue; }
                    if (c == '\\') { o.Append(c); escaped = true; continue; }
                    if (c == '"') { inString = false; o.Append(c); continue; }
                    if (c == '\n') { o.Append("\\n"); continue; }
                    if (c == '\r') continue;
                    if (c == '\t') { o.Append("\\t"); continue; }
                    o.Append(c);
                    continue;
                }
                if (c == '"') { inString = true; o.Append(c); continue; }
                if (c == '{') closers.Push('}');
                else if (c == '[') closers.Push(']');
                else if ((c == '}' || c == ']') && closers.Count > 0) closers.Pop();
                else if (c == ',')
                {
                    int j = i + 1;
                    while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                    if (j >= s.Length || s[j] == '}' || s[j] == ']') continue;    // a trailing comma
                }
                o.Append(c);
            }
            if (inString) o.Append('"');
            string mended = o.ToString().TrimEnd();
            if (mended.EndsWith(",", StringComparison.Ordinal)) mended = mended.Substring(0, mended.Length - 1);
            while (closers.Count > 0) mended += closers.Pop();
            return mended;
        }
        /// <summary>
        /// What to say when a script calls a method that is not there. The
        /// commonest slips are names from other APIs, and "x is not a function"
        /// does not tell a model which name is the right one here.
        /// </summary>
        static string ApiHint(string why)
        {
            var m = System.Text.RegularExpressions.Regex.Match(why ?? "", @"\.?(\w+) is not a function");
            if (!m.Success) return "";
            string name = m.Groups[1].Value;
            string fix;
            switch (name)
            {
                case "GetType": case "getType": case "GetKind": case "GetTypeName": fix = "GetClassType() (returns 'paragraph', 'table', 'run'...)"; break;
                case "GetChildCount": case "GetCount": case "GetLength": case "GetBlocksCount": fix = "GetElementsCount()"; break;
                case "GetChildren": case "GetBlocks": case "GetParagraphs": fix = "GetElement(i) with GetElementsCount(), or GetAllParagraphs() / GetAllTables()"; break;
                case "GetContents": case "GetInnerText": case "GetTextContent": fix = "GetText()"; break;
                case "AppendChild": case "Append": case "Add": fix = "Push(element) on the document, or AddElement(element) on a paragraph or cell"; break;
                case "GetRowCount": fix = "GetRowsCount()"; break;
                case "GetCellCount": fix = "GetCellsCount() on a row"; break;
                default: fix = "another name (they are case-sensitive: GetClassType, GetElementsCount, GetElement, GetText, Push, AddElement, GetRowsCount, GetCellsCount)"; break;
            }
            return name + " does not exist in this editor's API; use " + fix + ". ";
        }
        static void Inline(object[] blocks)
        {
            if (blocks == null) return;
            foreach (object b in blocks)
            {
                var block = b as Dictionary<string, object>;
                if (block == null) continue;
                object src;
                if (block.TryGetValue("src", out src) && src is string)
                {
                    string path = (string)src;
                    if (!path.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(path)) throw new ToolTrouble("There is no picture at " + path + ".");
                        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                        string mime = ext == "png" ? "image/png" : ext == "gif" ? "image/gif" : ext == "bmp" ? "image/bmp" : "image/jpeg";
                        block["src"] = "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path));
                    }
                }
                object rows;
                if (block.TryGetValue("rows", out rows) && rows is object[])
                    foreach (object row in (object[])rows)
                    {
                        var r = row as Dictionary<string, object>;
                        object cells;
                        if (r != null && r.TryGetValue("cells", out cells)) Inline(cells as object[]);
                        else Inline(row as object[]);
                    }
            }
        }

        // ---- seeing a document -------------------------------------------------

        /// <summary>What was drawn of a document: some of its pages (or sheets, or slides) and what to tell the model.</summary>
        class Look
        {
            public List<PageShot> Shots = new List<PageShot>();
            public int Total;
            public string Notes = "";
            public double PageWidthMm, PageHeightMm;
            public string Unit = "page";
        }

        /// <summary>
        /// Draws a document as it is now: Word and PDF by the document engine (how they will
        /// print), Excel and PowerPoint from a copy of the file the editor writes (drawn by
        /// Sight). Up to <paramref name="need"/> pictures from the first; the caller picks.
        /// </summary>
        async Task<Look> Draw(DocTab tab, int width, int need)
        {
            Docs.DocView view = await Ready(tab);
            var look = new Look();
            switch (tab.Kind)
            {
                case DocKind.Word:
                    {
                        List<string> pages = await view.RenderPagesAsync(width);
                        look.Total = pages.Count;
                        for (int i = 0; i < pages.Count && i < need; i++)
                            using (var raw = new System.Drawing.Bitmap(pages[i]))
                                look.Shots.Add(new PageShot { Image = new System.Drawing.Bitmap(raw), Caption = "page " + (i + 1) + " of " + pages.Count });
                        try
                        {
                            var seen = new JavaScriptSerializer().DeserializeObject(await view.RunScriptAsync(Glance, false)) as Dictionary<string, object>;
                            object mm;
                            if (seen != null && seen.TryGetValue("page_mm", out mm) && mm is object[] && ((object[])mm).Length == 2)
                            {
                                look.PageWidthMm = Convert.ToDouble(((object[])mm)[0], CultureInfo.InvariantCulture);
                                look.PageHeightMm = Convert.ToDouble(((object[])mm)[1], CultureInfo.InvariantCulture);
                            }
                        }
                        catch { }
                        if (look.PageWidthMm > 0) foreach (PageShot s in look.Shots) s.PxPerMm = s.Image.Width / look.PageWidthMm;
                        break;
                    }
                case DocKind.Pdf:
                    {
                        string source = view.SourcePdf;
                        var drawn = await Task.Run(() => { int t; List<PageShot> s = Sight.EnginePages(source, width, need, out t); return new Tuple<List<PageShot>, int>(s, t); });
                        look.Shots = drawn.Item1;
                        look.Total = drawn.Item2;
                        break;
                    }
                case DocKind.Sheet:
                case DocKind.Slides:
                    {
                        bool sheet = tab.Kind == DocKind.Sheet;
                        string temp = Path.Combine(Path.GetTempPath(), "NextScan", "sight");
                        Directory.CreateDirectory(temp);
                        temp = Path.Combine(temp, Guid.NewGuid().ToString("N") + (sheet ? ".xlsx" : ".pptx"));
                        try
                        {
                            await view.ExportAsync(temp);
                            var drawn = await Task.Run(() =>
                            {
                                int count; string notes;
                                List<PageShot> s = sheet ? Sight.Sheets(temp, need, 45, out count, out notes) : Sight.Slides(temp, width, need, out count, out notes);
                                return new Tuple<List<PageShot>, int, string>(s, count, notes);
                            });
                            look.Shots = drawn.Item1;
                            look.Total = sheet ? Math.Max(drawn.Item2, drawn.Item1.Count) : drawn.Item2;
                            look.Notes = drawn.Item3;
                        }
                        finally { try { File.Delete(temp); } catch { } }
                        look.Unit = sheet ? "sheet picture" : "slide";
                        break;
                    }
            }
            return look;
        }

        /// <summary>The picture, and the note that goes with it: which page, how big the page is, what a millimetre is in pixels.</summary>
        static void Give(AiToolResult result, DocTab tab, Look look, PageShot shot, bool grid)
        {
            var note = new StringBuilder();
            note.Append(tab.Title).Append(", ").Append(shot.Caption);
            if (tab.Kind == DocKind.Word) note.Append(", as it will print");
            if (look.PageWidthMm > 0) note.Append(" — page ").Append(Math.Round(look.PageWidthMm)).Append(" × ").Append(Math.Round(look.PageHeightMm)).Append(" mm");
            if (shot.PxPerMm > 0) note.Append("; 1 mm = ").Append(Math.Round(shot.PxPerMm, 2).ToString(CultureInfo.InvariantCulture)).Append(" px");
            if (grid) note.Append(" (blue grid over the page: a line every 10 mm from its top-left corner, numbered every 50)");
            result.Images.Add(Sight.Png(shot.Image));
            result.ImageNotes.Add(note.ToString());
        }

        /// <summary>
        /// What a change looks like: the document's pages, drawn again and handed to the
        /// model with the result of the change. A model that has only the text of what it
        /// did cannot tell a page that is right from one that is a mess; looking is how it
        /// notices. Never fails the change: a page that cannot be drawn is said so.
        /// </summary>
        async Task AutoLook(DocTab tab, Dictionary<string, object> args, AiToolResult result, bool fromEnd)
        {
            object wants;
            if (args.TryGetValue("look", out wants) && wants is bool && !(bool)wants) return;
            try
            {
                Look drawn = await Draw(tab, 1100, fromEnd ? 12 : 2);
                if (drawn.Shots.Count == 0) return;
                int take = Math.Min(2, drawn.Shots.Count);
                int start = fromEnd && drawn.Total > take ? Math.Min(drawn.Shots.Count, drawn.Total) - take : 0;
                for (int i = start; i < start + take && i < drawn.Shots.Count; i++) Give(result, tab, drawn, drawn.Shots[i], false);
                foreach (PageShot s in drawn.Shots) s.Dispose();
                string what = tab.Kind == DocKind.Sheet ? "the sheet as it looks" : tab.Kind == DocKind.Slides ? "the slides as they look" : "the pages as they look";
                result.Content = (result.Content ?? "") + "\n[Attached: " + what + " now (" + take + " of " + Math.Max(drawn.Total, drawn.Shots.Count) +
                                 "). Look at them before you go on: is it what you meant? If something is off, fix it, then look again. " +
                                 "look_at_document shows other pages, a mm grid, or a close-up of a region.]";
            }
            catch (Exception ex)
            {
                result.Content = (result.Content ?? "") + "\n[The pages could not be drawn to check them: " + ex.Message.Split('\n')[0] + "]";
            }
        }

        async Task LookAtDocument(DocTab tab, Dictionary<string, object> args, AiToolResult result)
        {
            result.Display = "Looking at " + tab.Title;

            int wanted = 0;
            object p;
            if (args.TryGetValue("page", out p) && p != null) wanted = Convert.ToInt32(p, CultureInfo.InvariantCulture);
            bool grid = Bool(args, "grid");

            // A close-up: a rectangle of the page in millimetres, drawn larger.
            double[] region = null;
            object r;
            if (args.TryGetValue("region", out r) && r is System.Collections.IList && ((System.Collections.IList)r).Count == 4)
            {
                region = new double[4];
                for (int i = 0; i < 4; i++) region[i] = Convert.ToDouble(((System.Collections.IList)r)[i], CultureInfo.InvariantCulture);
                if (region[2] <= 2 || region[3] <= 2) throw new ToolTrouble("region is [x, y, width, height] in mm from the page's top-left corner, at least a few mm wide.");
            }

            int width = 1100;
            if (region != null)
            {
                // Big enough that the rectangle fills a picture a model reads well.
                double pageW = 210;
                if (tab.Kind == DocKind.Word)
                {
                    Look probe = await Draw(tab, 300, 1);
                    if (probe.PageWidthMm > 0) pageW = probe.PageWidthMm;
                    foreach (PageShot s in probe.Shots) s.Dispose();
                }
                width = (int)Math.Max(1100, Math.Min(3600, pageW * 1400 / region[2]));
            }

            Look look = await Draw(tab, width, Math.Max(wanted, 2));
            if (look.Shots.Count == 0) throw new ToolTrouble("Nothing could be drawn of " + tab.Title + (tab.Kind == DocKind.Word ? " (it may be empty)." : "."));

            var shown = new List<int>();
            if (wanted > 0)
            {
                if (wanted > Math.Max(look.Total, look.Shots.Count)) throw new ToolTrouble(tab.Title + " has " + look.Total + " " + look.Unit + (look.Total == 1 ? "." : "s."));
                if (wanted > look.Shots.Count) throw new ToolTrouble("That " + look.Unit + " could not be drawn.");
                shown.Add(wanted);
            }
            else for (int i = 1; i <= Math.Min(2, look.Shots.Count); i++) shown.Add(i);

            foreach (int n in shown)
            {
                PageShot shot = look.Shots[n - 1];
                bool gridDrawn = false;
                if (grid && shot.PxPerMm > 0) { Sight.Grid(shot.Image, shot.PxPerMm); gridDrawn = true; }
                if (region != null)
                {
                    if (shot.PxPerMm <= 0) throw new ToolTrouble("A region can only be given for a Word page or a slide.");
                    System.Drawing.Bitmap cut;
                    try { cut = Sight.Cut(shot.Image, shot.PxPerMm, region[0], region[1], region[2], region[3]); }
                    catch (ArgumentException ex) { throw new ToolTrouble(ex.Message); }
                    shot.Image.Dispose();
                    shot.Image = cut;
                    shot.Caption += ", close-up of x " + region[0] + ", y " + region[1] + ", " + region[2] + " × " + region[3] + " mm";
                }
                Give(result, tab, look, shot, gridDrawn);
            }
            foreach (PageShot s in look.Shots) s.Dispose();

            result.Content = Json(new Dictionary<string, object>
            {
                { look.Unit.Replace(" picture", "") + "s", look.Total }, { "shown", shown },
                { "page_mm", look.PageWidthMm > 0 ? new object[] { Math.Round(look.PageWidthMm), Math.Round(look.PageHeightMm) } : null },
                { "note", look.Notes },
            });
            result.Display = "Looked at " + tab.Title + (shown.Count == 1 ? ", " + look.Unit + " " + shown[0] : "");
        }

        // ---- scanned pages -------------------------------------------------

        /// <summary>The scanned page asked for: by number, or the one the operator has selected or the preview.</summary>
        NextScan.Core.RawImage ScanFor(Dictionary<string, object> args, out string name, out int count)
        {
            if (ScanPage == null || ScanCount == null) throw new ToolTrouble("Scanned pages are not available here.");
            count = ScanCount();
            object p;
            if (args.TryGetValue("page", out p) && p != null)
            {
                int n = Convert.ToInt32(p, CultureInfo.InvariantCulture);
                if (n < 1 || n > count)
                    throw new ToolTrouble(count == 0 ? "Nothing has been scanned in this session." : "There are " + count + " scanned pages; ask for 1 to " + count + ".");
                name = "Scanned page " + n + " of " + count;
                return ScanPage(n - 1);
            }
            var current = CurrentScan == null ? null : CurrentScan();
            if (current == null || current.Item1 == null)
                throw new ToolTrouble("Nothing has been scanned or previewed yet. Ask the operator to scan the page.");
            name = current.Item2;
            return current.Item1;
        }

        /// <summary>[x, y, width, height] in mm from a tool's arguments, or null.</summary>
        static double[] RegionOf(Dictionary<string, object> args)
        {
            object r;
            if (!args.TryGetValue("region", out r) || !(r is System.Collections.IList) || ((System.Collections.IList)r).Count != 4) return null;
            var region = new double[4];
            for (int i = 0; i < 4; i++) region[i] = Convert.ToDouble(((System.Collections.IList)r)[i], CultureInfo.InvariantCulture);
            if (region[2] <= 2 || region[3] <= 2) throw new ToolTrouble("region is [x, y, width, height] in mm from the page's top-left corner, at least a few mm wide.");
            return region;
        }

        async Task LookAtScan(Dictionary<string, object> args, AiToolResult result)
        {
            string name; int count;
            NextScan.Core.RawImage page = ScanFor(args, out name, out count);
            if (page == null || !page.IsValid) throw new ToolTrouble("That page could not be prepared.");

            double[] region = RegionOf(args);
            bool grid = Bool(args, "grid");
            double pxPerMm;
            System.Drawing.Bitmap view;
            try { view = ScanSight.View(page, region, grid, 1568, out pxPerMm); }
            catch (ArgumentException ex) { throw new ToolTrouble(ex.Message); }
            using (view) result.Images.Add(AttachReader.Jpeg(view, 1568));

            double wmm = ScanSight.PageWidthMm(page), hmm = ScanSight.PageHeightMm(page);
            var note = new StringBuilder(name);
            if (region != null) note.Append(", close-up of x ").Append(region[0]).Append(", y ").Append(region[1]).Append(", ").Append(region[2]).Append(" x ").Append(region[3]).Append(" mm");
            note.Append(" -- page ").Append(Math.Round(wmm, 1)).Append(" x ").Append(Math.Round(hmm, 1)).Append(" mm; 1 mm = ").Append(Math.Round(pxPerMm, 2).ToString(CultureInfo.InvariantCulture)).Append(" px");
            if (grid) note.Append(" (blue grid in the page's millimetres: a line every 10 mm from its top-left corner, numbered every 50)");
            result.ImageNotes.Add(note.ToString());
            var content = new Dictionary<string, object>
            {
                { "shown", name }, { "scanned_pages", count },
                { "size_mm", new object[] { Math.Round(wmm, 1), Math.Round(hmm, 1) } },
                { "dpi", Math.Round(page.XDpi) },
            };

            // Looking at the whole page also finds the pictures on it, because where a logo is cannot be
            // read reliably off a picture shrunk to fit a model, and a rebuild that puts the logo in the
            // wrong place is wrong however good the rest is.
            if (region == null)
            {
                try
                {
                    List<ScanPicture> found = await Task.Run(() => ScanSight.FindPictures(page));
                    _found = found; _foundPage = page;
                    var list = new List<object>();
                    foreach (ScanPicture pic in found)
                        list.Add(new Dictionary<string, object> { { "number", pic.Number }, { "kind", pic.Kind }, { "region_mm", new object[] { pic.X, pic.Y, pic.Width, pic.Height } } });
                    content["pictures_found"] = list;
                    content["pictures_note"] = found.Count == 0
                        ? "The application found nothing on this page that looks like artwork (a logo, an emblem, a stamp, a photograph)."
                        : "The application found these places that look like artwork, measured on the scan (candidates: a heavy heading can be mistaken for one). Take one with crop_scan picture:N -- do not estimate a logo's position from the picture; find_scan_pictures shows them boxed.";
                }
                catch { }
            }
            result.Content = Json(content);
            result.Display = "Looked at " + name.ToLowerInvariant() + (region != null ? " (close-up)" : "");
        }

        async Task TranscribeScan(Dictionary<string, object> args, AiToolResult result)
        {
            if (HandwritingReader == null) throw new ToolTrouble("There is no handwriting reader set up (it needs a Gemini key).");
            string name; int count;
            NextScan.Core.RawImage page = ScanFor(args, out name, out count);
            if (page == null || !page.IsValid) throw new ToolTrouble("That page could not be prepared.");

            double[] region = RegionOf(args);
            byte[] jpeg;
            double pxPerMm;
            try
            {
                // Handwriting is read at more than the usual size: thin pen strokes vanish when a page is shrunk.
                using (System.Drawing.Bitmap view = ScanSight.View(page, region, false, 2400, out pxPerMm)) jpeg = AttachReader.Jpeg(view, 2400);
            }
            catch (ArgumentException ex) { throw new ToolTrouble(ex.Message); }

            result.Display = "Gemini is reading the handwriting";
            string text;
            try { text = await HandwritingReader(jpeg, Str(args, "note")); }
            catch (Exception ex) { throw new ToolTrouble("The handwriting could not be read: " + ex.GetBaseException().Message.Split('\n')[0]); }
            if (text.Length == 0) text = "[illegible]";

            result.Content = Json(new Dictionary<string, object>
            {
                { "read", name + (region != null ? ", region " + region[0] + ", " + region[1] + ", " + region[2] + " x " + region[3] + " mm" : "") },
                { "transcription", text },
                { "note", "Gemini's reading. [?] marks a word it was not sure of, [illegible] one it could not read: keep those marks visible to the operator or ask them, do not fill them in." },
            });
            result.Display = "Gemini read the handwriting (" + text.Length + " characters)";
        }

        // What find_scan_pictures last found, for crop_scan to take by number.
        List<ScanPicture> _found;
        NextScan.Core.RawImage _foundPage;

        async Task FindScanPictures(Dictionary<string, object> args, AiToolResult result)
        {
            string name; int count;
            NextScan.Core.RawImage page = ScanFor(args, out name, out count);
            if (page == null || !page.IsValid) throw new ToolTrouble("That page could not be prepared.");
            List<ScanPicture> found = await Task.Run(() => ScanSight.FindPictures(page));
            _found = found; _foundPage = page;

            double pxPerMm;
            using (System.Drawing.Bitmap outline = ScanSight.Outline(page, found, 1568, out pxPerMm))
                result.Images.Add(AttachReader.Jpeg(outline, 1568));
            result.ImageNotes.Add(name + " -- the pictures found, boxed and numbered");

            var list = new List<object>();
            foreach (ScanPicture p in found)
                list.Add(new Dictionary<string, object>
                {
                    { "number", p.Number }, { "kind", p.Kind }, { "region_mm", new object[] { p.X, p.Y, p.Width, p.Height } }, { "ink_fill", p.Fill },
                });
            result.Content = Json(new Dictionary<string, object>
            {
                { "pictures", list },
                { "page_mm", new object[] { Math.Round(ScanSight.PageWidthMm(page), 1), Math.Round(ScanSight.PageHeightMm(page), 1) } },
                { "note", found.Count == 0
                    ? "Nothing that looks like artwork was found (a logo that is very faint or very small can be missed): look at the scan, and give crop_scan a region yourself if there is one."
                    : "Candidates only: the picture shows each boxed. A heavy heading can be boxed as if it were artwork (type it as text instead); a logo can be boxed in two parts or too tight (give crop_scan a region of your own then). Words are text, to be typed, not cropped." },
            });
            result.Display = found.Count == 0 ? "Looked for pictures on the scan: none" : "Found " + found.Count + (found.Count == 1 ? " picture" : " pictures") + " on the scan";
        }

        void CropScan(Dictionary<string, object> args, AiToolResult result)
        {
            string name; int count;
            NextScan.Core.RawImage page;
            double[] region = RegionOf(args);
            object pick;
            if (region == null && args.TryGetValue("picture", out pick) && pick != null)
            {
                int n = Convert.ToInt32(pick, CultureInfo.InvariantCulture);
                if (_found == null || _foundPage == null) throw new ToolTrouble("Call find_scan_pictures first, or give a region.");
                ScanPicture hit = _found.Find(p => p.Number == n);
                if (hit == null) throw new ToolTrouble("There is no picture " + n + " in what find_scan_pictures found.");
                region = new[] { hit.X, hit.Y, hit.Width, hit.Height };
                page = _foundPage;
                name = "the scan";
            }
            else
            {
                if (region == null) throw new ToolTrouble("Give region [x, y, width, height] in mm, or picture (a number from find_scan_pictures).");
                page = ScanFor(args, out name, out count);
            }
            if (page == null || !page.IsValid) throw new ToolTrouble("That page could not be prepared.");

            bool trim = !args.ContainsKey("trim") || Bool(args, "trim");
            bool transparent = Bool(args, "transparent");
            double wmm, hmm;
            System.Drawing.Bitmap taken;
            try { taken = ScanSight.Take(page, region, trim, transparent, 1400, out wmm, out hmm); }
            catch (ArgumentException ex) { throw new ToolTrouble(ex.Message); }

            string label = Str(args, "name");
            foreach (char bad in Path.GetInvalidFileNameChars()) label = label.Replace(bad, '_');
            if (label.Trim().Length == 0) label = "scan-picture";
            string folder = Path.Combine(Path.GetTempPath(), "NextScan", "crops");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, label.Trim() + "-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".png");
            using (taken)
            {
                taken.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                // What was taken, shown on white so the model can check the edges.
                using (var flat = new System.Drawing.Bitmap(taken.Width, taken.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(flat)) { g.Clear(System.Drawing.Color.White); g.DrawImage(taken, 0, 0, taken.Width, taken.Height); }
                    result.Images.Add(AttachReader.Jpeg(flat, 900));
                }
            }
            result.ImageNotes.Add("The picture taken from " + name + ": " + Math.Round(wmm, 1) + " x " + Math.Round(hmm, 1) + " mm");
            result.Content = Json(new Dictionary<string, object>
            {
                { "path", path }, { "width_mm", Math.Round(wmm, 1) }, { "height_mm", Math.Round(hmm, 1) },
                { "use", "In write_document: {type:'image', src:'" + path.Replace('\\', '/') + "', width:" + Math.Round(wmm, 1).ToString(CultureInfo.InvariantCulture) + ", height:" + Math.Round(hmm, 1).ToString(CultureInfo.InvariantCulture) + ", x:.., y:..} (x and y in mm from the page's corner place it exactly; without them it sits in the text). Its real size on the scan is that width and height." },
            });
            result.Display = "Took a picture from the scan (" + Math.Round(wmm) + " x " + Math.Round(hmm) + " mm)";
        }

        async Task Selection(DocTab tab, AiToolResult result)
        {
            Docs.DocView view = await Ready(tab);
            string text = await view.SelectedTextAsync();
            result.Content = Json(new Dictionary<string, object> { { "selected", new JavaScriptSerializer().Deserialize<object>(text) } });
            result.Display = "Read the selection in " + tab.Title;
        }

        async Task Save(DocTab tab, string path, AiToolResult result)
        {
            Docs.DocView view = await Ready(tab);
            result.Display = "Saving " + tab.Title;

            var saved = new TaskCompletionSource<bool>();
            EventHandler dirty = null;
            EventHandler<Docs.DocsMessageEventArgs> trouble = null;
            dirty = delegate { if (!view.Dirty) saved.TrySetResult(true); };
            trouble = delegate (object s, Docs.DocsMessageEventArgs e) { saved.TrySetException(new ToolTrouble(e.Message)); };
            view.DirtyChanged += dirty;
            view.Trouble += trouble;
            try
            {
                string before = view.FilePath;
                if (!string.IsNullOrWhiteSpace(path) && string.IsNullOrEmpty(before))
                {
                    if (!Path.IsPathRooted(path)) path = Path.Combine(Folder(), path);
                    view.SaveTo(path);
                }
                else view.Save();

                Task finished = await Task.WhenAny(saved.Task, Task.Delay(120000));
                if (finished != saved.Task) throw new ToolTrouble("The save did not finish (the operator may have cancelled choosing a place).");
                await saved.Task;
            }
            finally { view.DirtyChanged -= dirty; view.Trouble -= trouble; }

            tab.Path = view.FilePath;
            result.Content = Json(new Dictionary<string, object> { { "saved", true }, { "file", view.FilePath } });
            result.Display = "Saved " + Path.GetFileName(view.FilePath);
        }

        async Task Export(DocTab tab, string path, bool overwrite, AiToolResult result)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ToolTrouble("Give a file name or path for the copy, ending in the format wanted, e.g. bill.pdf.");
            if (!Path.IsPathRooted(path)) path = Path.Combine(Folder(), path);
            if (File.Exists(path) && !overwrite)
                throw new ToolTrouble("A file is already there: " + path + ". Choose another name, or ask the operator whether to replace it.");

            Docs.DocView view = await Ready(tab);
            result.Display = "Exporting " + Path.GetFileName(path);
            string written = await view.ExportAsync(path);
            result.Content = Json(new Dictionary<string, object> { { "written", written } });
            result.Display = "Wrote " + Path.GetFileName(written);
        }

        void Show(DocTab tab, AiToolResult result)
        {
            _space.Select(tab);
            result.Content = "{\"shown\":\"" + tab.Id + "\"}";
            result.Display = "Showed " + tab.Title;
        }

        void Close(DocTab tab, bool discard, AiToolResult result)
        {
            if (tab.Dirty && !discard)
                throw new ToolTrouble(tab.Title + " has unsaved changes. Save it first, or ask the operator whether to discard them.");
            if (discard) tab.Dirty = false;
            string title = tab.Title;
            _space.Close(tab);
            result.Content = "{\"closed\":true}";
            result.Display = "Closed " + title;
        }

        async Task FromScans(AiToolResult result)
        {
            if (PdfFromPages == null) throw new ToolTrouble("Scanned pages are not available here.");
            string path = PdfFromPages();
            if (string.IsNullOrEmpty(path)) throw new ToolTrouble("There are no scanned pages in this session, or the PDF could not be written.");
            DocTab tab = _space.Open(path);
            if (tab == null) throw new ToolTrouble("The PDF was written to " + path + " but could not be opened.");
            await Ready(tab);
            result.Content = Json(new Dictionary<string, object> { { "id", tab.Id }, { "file", path } });
            result.Display = "Made a PDF of the scanned pages";
        }

        async Task JevAsk(Dictionary<string, object> args, AiToolResult result)
        {
            if (!JevClient.Ready) throw new ToolTrouble("Jev has no key on this computer.");

            string questions = Str(args, "questions");
            object parsed;
            try { parsed = new JavaScriptSerializer().DeserializeObject(questions); }
            catch { throw new ToolTrouble("questions is not valid JSON."); }
            var map = parsed as Dictionary<string, object>;
            if (map == null || map.Count == 0) throw new ToolTrouble("questions must be a JSON object of named questions.");

            // What is judged: the text given, or a document's content as this
            // application reads it (Jev takes JSON as well as plain text).
            string state;
            string text = Str(args, "text");
            string what;
            if (text.Length > 0) { state = Json(text); what = "the text"; }
            else
            {
                DocTab tab = Pick(args);
                var inner = new AiToolResult();
                await Read(tab, new Dictionary<string, object>(), inner);
                string content = inner.Content ?? "";
                state = content.TrimStart().StartsWith("{", StringComparison.Ordinal) ? content : Json(content);
                what = tab.Title;
            }
            if (state.Length > 200000) throw new ToolTrouble("That is too much text for one question; pass a part of it as text.");

            result.Display = "Asking Jev about " + what;
            var cancel = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
            string answer = await Task.Run(() => JevClient.Ask(state, questions, cancel.Token));
            result.Content = answer;
            result.Display = "Jev answered " + map.Count + (map.Count == 1 ? " question" : " questions") + " about " + what;
        }

        // ---- helpers -------------------------------------------------------

        DocTab Pick(Dictionary<string, object> args)
        {
            string id = Str(args, "document");
            if (!string.IsNullOrEmpty(id))
            {
                DocTab tab = _space.ById(id);
                if (tab == null) throw new ToolTrouble("No open document has the id " + id + ". Call list_documents.");
                return tab;
            }
            if (_space.Current != null) return _space.Current;
            if (_space.Tabs.Count == 1) return _space.Tabs[0];
            throw new ToolTrouble(_space.Tabs.Count == 0 ? "No document is open." : "Several documents are open and none is in front: say which one.");
        }

        static async Task<Docs.DocView> Ready(DocTab tab)
        {
            var view = tab.Surface as Docs.DocView;
            if (view == null) throw new ToolTrouble(tab.Title + " has no editor.");
            Task ready = view.WhenReadyAsync();
            Task finished = await Task.WhenAny(ready, Task.Delay(90000));
            if (finished != ready) throw new ToolTrouble(tab.Title + " did not finish opening.");
            await ready;
            return view;
        }

        string Folder()
        {
            string folder = OutputFolder != null ? OutputFolder() : "";
            if (string.IsNullOrEmpty(folder))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NextScan");
            Directory.CreateDirectory(folder);
            return folder;
        }

        static string KindName(DocKind kind)
        {
            switch (kind)
            {
                case DocKind.Word: return "document";
                case DocKind.Sheet: return "workbook";
                case DocKind.Slides: return "presentation";
                case DocKind.Pdf: return "pdf";
                default: return "other";
            }
        }

        static string Str(Dictionary<string, object> args, string key)
        {
            object v;
            return args.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        static string SpecMode(Dictionary<string, object> spec)
        {
            object v;
            return spec.TryGetValue("mode", out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        static bool Bool(Dictionary<string, object> args, string key)
        {
            object v;
            return args.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static string Json(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value);
        }

        static readonly Dictionary<string, string> Scripts = new Dictionary<string, string>();

        /// <summary>One of the reading scripts, compiled into the application (scripts\*.js).</summary>
        static string Script(string name)
        {
            lock (Scripts)
            {
                string text;
                if (Scripts.TryGetValue(name, out text)) return text;
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NextScan.App.scripts." + name))
                {
                    if (s == null) throw new ToolTrouble("The reading script " + name + " is missing from this build.");
                    using (var reader = new StreamReader(s, Encoding.UTF8)) text = reader.ReadToEnd();
                }
                Scripts[name] = text;
                return text;
            }
        }

        class ToolTrouble : Exception
        {
            public ToolTrouble(string message) : base(message) { }
        }
    }
}
