# The document workspace — research before building it

Research done 2026-09-23, before any code. The owner's brief: when **Assist** is
chosen, the whole window becomes a document workspace, like Microsoft Word, with
no scanning or cropping controls. New files, opening files, and **accurate**
support for Word, Excel and PDF in one environment. The assistant's document
work comes after this, on top of it. The UI must be quick, tidy and friendly.

Measured on this machine: Microsoft 365 (x64, 16.0.20430), Acrobat, the WebView2
runtime 153, and Windows' own PDF API (`Windows.Data.Pdf`) are all present.

---

## 1. Microsoft Word cannot be put inside our window

The obvious answer — host Word itself — is not available. Microsoft's own
container for this, **DSOFramer**, was withdrawn because of compatibility
problems with current Office, and Windows Forms has no OLE container to replace
it ([MSDN](https://social.msdn.microsoft.com/Forums/windows/en-US/4f25181e-3c4c-4fac-93f3-b4255b51afa5/is-it-possible-to-add-word-editor-to-winforms-application?forum=winforms)).
What remains is re-parenting Word's window into ours, which is unsupported and
breaks focus, the ribbon, and multiple documents; or a paid third-party control
([Edraw](https://edraw.wondershare.com/dso-framer.html)).

**Office is still used, for what it is best at**: exact conversions. Word and
Excel export their own files to PDF exactly (`ExportAsFixedFormat`), Word opens
a PDF as an editable document (PDF Reflow), and both read the legacy `.doc` and
`.xls`. Where Office is not installed, these become unavailable rather than
wrong.

## 2. A native editor of our own is not a real option

Word's layout engine is the product of thirty years. A docx renderer written
here would be the "made-up" thing the owner has said not to build. Every serious
editor found in this research is either Office itself or a web engine.

## 3. The engines that exist

| engine | docx | xlsx | pdf | runs | license | size |
| --- | --- | --- | --- | --- | --- | --- |
| **ONLYOFFICE** in the browser ([ranuts/document](https://github.com/ranuts/document)) | edit | edit | annotate | fully offline, WASM converter | **AGPL-3.0** + keep ONLYOFFICE logo | ~460 MB as shipped |
| [docx-editor](https://www.docx-editor.dev/) (eigenpal) | edit | — | — | offline | Apache-2.0 core; comments/track changes paid | small |
| [Univer](https://docs.univer.ai/guides/sheets/features/import-export) | — | edit | — | offline | Apache-2.0, but **xlsx import/export is a paid Pro feature** | medium |
| [GenOffice](https://github.com/genspark-ai/genoffice) | edit | edit | edit | Electron only | Apache-2.0 | — |
| [PDF.js](https://github.com/mozilla/pdf.js/releases) | — | — | view, annotate, sign, merge, reorder | offline | Apache-2.0 | small |

ONLYOFFICE is the one engine built from the start to be compatible with Office
Open XML, with a Word-style ribbon, and ranuts/document already runs it with no
server at all: the converter is WebAssembly, and a host drives it over
`postMessage` (`document:open-buffer`, `document:save` returning the file,
read-only mode; [embed API](https://github.com/ranuts/document/blob/main/docs/embed-api.md)).
It also exposes `get_document_text` and similar tools meant for AI agents, which
is exactly what the assistant will need next.

GenOffice was the most interesting find — Apache-licensed, docs, sheets and PDF —
but its engines are tied to Electron, so they cannot be placed in our window.

## 4. How a web engine goes inside a Windows application

**WebView2**, the Edge engine Windows ships. It maps a local folder to an https
name (`SetVirtualHostNameToFolderMapping`), so a web application runs from the
install folder with no web server and no network
([Microsoft](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping)).
That is the one thing ranuts/document needs: it will not run from `file://`.

The WebView2 SDK is a NuGet package. It would live in its own project beside
`src/Ai`, the same exception the AI layer already has; the engine and the app
stay free of NuGet.

## 5. Bengali — the part no foreign tool will get right for us

- **Unicode Bengali:** ONLYOFFICE added Bengali shaping in 7.2. A spreadsheet
  bug with conjuncts ([#1322](https://github.com/ONLYOFFICE/DesktopEditors/issues/1322))
  is marked fixed. The bundle is 9.3. This must be checked on real bills, not
  assumed.
- **SutonnyMJ and the other Bijoy fonts:** ONLYOFFICE in a browser does **not**
  use Windows' fonts. It uses its own catalog, and a font has to be added in
  four places (files, `AllFonts.js`, a selection table, and picker thumbnails —
  [fonts.md](https://github.com/ranuts/document/blob/main/docs/fonts.md)). The
  shop's bills are SutonnyMJ ANSI, so the fonts installed on the machine have to
  be added to that catalog on the machine, at first run. SutonnyMJ is a plain
  glyph-position font and needs no shaping, so once it is in the catalog it will
  draw. The font files are never shipped by us.
- The catalog as shipped is 193 MB, much of it Chinese. Most of that can go.

## 6. How the workspace is laid out

Every product looked at uses the same shape. Word opens on **Home** — new blank
document, templates, recent files — and returns there from the File tab
([Microsoft](https://support.microsoft.com/en-us/office/collab-files/start-backstage-with-the-file-tab)).
WPS Office, the suite that handles Word, Excel and PDF in one window, adds a
**tab strip of open documents**, like a browser, with Home as the first tab
([WPS](https://help.wps.com/articles/wps-office-tabbed-viewing-feature)).

So, in NextScan, when Assist is chosen:

    [rail] | [ Home | bill.docx x | rates.xlsx x | form.pdf x | + ] | [assistant]
           |                                                          |
           |   Home: New — Document, Workbook, Presentation,          |
           |         PDF from scanned pages                           |
           |         Open…   (or drop a file here)                    |
           |         Recent — name, kind, folder, when                |
           |                                                          |
           |   a document tab: the engine's own ribbon and page view  |

The capture controls, the canvas and the crop tools are gone while Assist is
chosen, and come back exactly as they were when another section is chosen.

---

## What needs the owner's decision

1. **Which engine.** ONLYOFFICE (most faithful; AGPL; roughly 250–450 MB added to
   the installer) or the lighter permissive editors (smaller, Apache/MIT, but
   visibly less faithful to Word and with no free xlsx engine of Univer's
   quality).
2. **The license that follows from it.** NextScan has no license file yet.
   Shipping ONLYOFFICE means shipping under the AGPL, keeping its logo.

## Decided (2026-09-23)

ONLYOFFICE. The owner's terms: no ONLYOFFICE mark on NextScan's own screens,
full credit in About. That is what ONLYOFFICE's licence asks since **9.4**
(May 2026): the logo requirement was replaced by an About section naming
ONLYOFFICE as the original developer, saying the program was modified, and
linking the licence
([ONLYOFFICE](https://www.onlyoffice.com/blog/2026/05/onlyoffice-license-and-trademark-policy)).
It also rules out ranuts/document as a base: it ships 9.3, whose terms still
require the logo. The engine is taken from ONLYOFFICE's own 9.4.0 releases.

## What was found while building it

- **Two official packages, not one.** The desktop package
  (`DesktopEditors_x64.zip`) has the Windows converter, `x2t.exe`, and its
  libraries. Its editors do not run in a plain browser: the Word editor has no
  `_offline_` path and expects the desktop application's native
  `AscDesktopEditor` object, which is used through 96 different methods. The
  Document Server package has the same 9.4 editors built with the offline mode.
  So the editors come from one and the converter from the other; both are 9.4.0
  and read the same `DOCY;v10` binary. `tools/get_onlyoffice.ps1` fetches both
  and checks the SHA-256 GitHub publishes.
- **The converter works natively.** `x2t new.docx Editor.bin` in 2.3 s cold.
  No WebAssembly build of it is needed, as it is in every browser-only project.
- **Fonts are the machine's own.** `graphics.dll` exports ONLYOFFICE's font
  worker (`CApplicationFontsWorker`); `nsfonts` (src/Docs/native) is their
  `allfontsgen`, reduced, and builds the catalog from Windows' fonts in 12 s:
  1,110 faces here, SutonnyMJ with its bold and italic, Nikosh, Kalpurush,
  Nirmala UI, Vrinda.
- **No font is ever copied.** allfontsgen's "web" output was 622 MB. Compared
  byte for byte, its web catalog is the desktop one with paths replaced by
  numbers, and every web copy is the original with its first 32 bytes XORed with
  one fixed 16-byte key. Both are produced on request instead.
- **A Document Server's jobs, answered in-process.** WebView2's request
  handler serves the editors, the catalog, the fonts, the converted document and
  the save; a shim injected before the editor's own scripts supplies the
  document, the permissions, local image upload and the save hooks. The approach
  was worked out by office-web (wasmtools, AGPL), which runs the 9.4 frontend
  offline in a browser.

## Order of work, and where it stands

1. **Done.** The workspace shell: Assist replaces the canvas with Home and a
   tab strip; the other sections restore it untouched (checked in the app).
2. **Done.** WebView2 layer and the engine served from the install folder. A
   .docx, a .xlsx and a .pdf open in their editors in 3–4.5 s; a .docx and a new
   .xlsx save back through our own Save, and Word and Excel read the result
   (same text, tables, page count, fonts, formulas).
3. **Done for the fonts on this machine.** SutonnyMJ ANSI and Unicode Bengali
   with conjuncts render as Word renders them (compared against Word's own PDF
   of the same file). Still to check on a real shop bill.
4. **Done without Office.** The editor's own Print, File > Download As and
   Save Copy, Review > Compare and Combine, and Insert > Text from File all
   call a Document Server; each is answered in-process (DocView.Server.cs) with
   x2t doing the conversion. Print goes through Windows' Print dialog. Office
   where present (exact PDF export, .doc/.xls) is still a later option.
5. **Done.** The assistant works on the open documents (below).

## The assistant in the workspace (2026-09-24)

The Assist panel's model is given eleven tools (src/App/StudioDocTools.cs):
list, open, create, read, edit, get the selection, save, export, show, close,
and make a PDF of the scanned pages. They work on the same tabs the operator
sees, through the same workspace, so everything the assistant does is on
screen, undoable with Ctrl+Z, and marks the tab unsaved.

- **Reading** is done by fixed scripts compiled into the application
  (src/App/scripts/read-*.js): Word as blocks (paragraphs with style and fonts,
  tables as rows), Excel as used cells with values and formulas, PowerPoint as
  text per slide, PDF as x2t's text. The fonts are there so a SutonnyMJ
  paragraph can be recognised as Bijoy text rather than taken for English.
- **Changing** is a script for ONLYOFFICE's document API, run the way its
  plugins are run (one undo point, recalculated). A failing script comes back to
  the model as its error line and a note that what ran before the error stays.
- **9.4 changed `Api.CreateTable` to (rows, cols).** Models know the old
  order; the instruction says so, and a new table has no borders unless given
  them, which the instruction also says.
- Tool calls go over all three providers' own tool formats. Each round is
  shown in the transcript as a small step line ("✓ Opened bill.docx"). A turn
  stops at 24 rounds, and a failed or stopped turn is taken out of the history
  whole, so the next request is never left with a call and no result.
- Checked off-screen against real editors: every tool, and a real Gemini Flash
  loop given a Bengali request ("make a Word document with a heading and a
  table … then a PDF") did it in four calls; the PDF has the Bengali heading
  and a bordered table.
