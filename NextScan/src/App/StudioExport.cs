// =============================================================================
// NextScan Studio - export shim
//
// The writing itself moved to NextScan.Core.PageWriter so it could be tested
// without starting the UI. This keeps the call sites in the shell unchanged and
// is the one place that decides how the shell's settings map onto an ExportPlan.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using NextScan.Core;

namespace NextScan.App
{
    public static class StudioExport
    {
        public static Action<string> Log
        {
            get { return PageWriter.Log; }
            set { PageWriter.Log = value; }
        }

        public static bool SaveSingle(RawImage img, string outPath, string format, long jpegQuality = 92L)
        {
            return PageWriter.SaveSingle(img, outPath, format, jpegQuality);
        }

        public static bool SaveBitmap(Bitmap bmp, string path, string format, long jpegQuality = 92L)
        {
            return PageWriter.SaveBitmap(bmp, path, format, jpegQuality);
        }

        public static bool SavePdf(List<RawImage> pages, string outPath, long jpegQuality = 92L)
        {
            return PageWriter.SavePdf(pages, outPath, jpegQuality);
        }

        public static bool SaveMultiPageTiff(List<RawImage> pages, string outPath)
        {
            return PageWriter.SaveMultiPageTiff(pages, outPath);
        }

        /// <summary>
        /// Saves a run of pages under one name pattern. Kept for the older call
        /// sites that pass a concrete path rather than a pattern; new code should
        /// build an ExportPlan and call PageWriter.Write.
        /// </summary>
        public static List<string> SaveBatch(List<RawImage> pages, string outPathOrPattern,
                                             string format, long jpegQuality = 92L)
        {
            ExportPlan plan = new ExportPlan
            {
                Directory = System.IO.Path.GetDirectoryName(outPathOrPattern) ?? "",
                Pattern = System.IO.Path.GetFileNameWithoutExtension(outPathOrPattern) + "_{ppp}",
                Format = format,
                JpegQuality = jpegQuality,
                MultiPage = false
            };
            if (pages != null && pages.Count == 1) plan.Pattern =
                System.IO.Path.GetFileNameWithoutExtension(outPathOrPattern);

            return PageWriter.Write(pages, plan);
        }
    }
}
