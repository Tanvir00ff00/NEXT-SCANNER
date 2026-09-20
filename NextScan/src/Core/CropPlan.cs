// =============================================================================
// NextScan Studio - the approved crop plan, and cutting a scan with it
// Plan ref: MASTER_PLAN section 8.1 (auto crop), 13 (shell); preview decides,
// scan obeys.
//
// This file exists because the plan used to be built inline in the shell and
// applied inline in the shell, which meant a test could only reach it by
// copying the arithmetic. Copied arithmetic is how a suite goes green while the
// field fails: the copy and the original drift, and the suite then measures the
// copy. Preview conversion and scan-time cutting both live here now, so the
// application and the tests run the same code or neither does.
//
// Nothing in this file detects anything. It converts approved geometry between
// coordinate systems and moves pixels.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    /// <summary>
    /// One item the operator approved on the preview, described in inches on
    /// the glass.
    ///
    /// Inches on the glass rather than a fraction of the preview: a scan may
    /// cover a different area than the preview did - a paper size, or a
    /// hand-drawn selection - and inches are the one description both agree on.
    /// </summary>
    public class CropPlanItem
    {
        /// <summary>Axis-aligned envelope, inches from the top-left of the glass.</summary>
        public RectangleF Bounds;

        /// <summary>The four orientation corners, inches on the glass.</summary>
        public PointF[] Corners;

        /// <summary>Free-form stock boundary, inches on the glass, or null.</summary>
        public PointF[] Outline;

        public CropConfidence Confidence;
    }

    public static class CropPlan
    {
        /// <summary>
        /// Converts what the detector found on a preview into a plan in inches
        /// on the glass.
        /// </summary>
        /// <param name="items">Regions from the detector, in preview pixels.</param>
        /// <param name="previewWidth">Preview width in pixels.</param>
        /// <param name="previewHeight">Preview height in pixels.</param>
        /// <param name="bedWidthInches">Width of the area the preview covered.</param>
        /// <param name="bedHeightInches">Height of the area the preview covered.</param>
        public static List<CropPlanItem> FromDetection(IList<CropRegion> items,
                                                       int previewWidth, int previewHeight,
                                                       double bedWidthInches, double bedHeightInches)
        {
            List<CropPlanItem> plan = new List<CropPlanItem>();
            if (items == null || previewWidth <= 0 || previewHeight <= 0 ||
                bedWidthInches <= 0 || bedHeightInches <= 0) return plan;

            double perPixelX = bedWidthInches / previewWidth;
            double perPixelY = bedHeightInches / previewHeight;

            foreach (CropRegion item in items)
            {
                if (item == null || item.Box == null || item.Box.Corners == null ||
                    item.Box.Corners.Length < 4) continue;

                CropPlanItem planned = new CropPlanItem
                {
                    Confidence = item.Confidence,
                    Bounds = new RectangleF(
                        (float)(item.NormRect.X * bedWidthInches),
                        (float)(item.NormRect.Y * bedHeightInches),
                        (float)(item.NormRect.Width * bedWidthInches),
                        (float)(item.NormRect.Height * bedHeightInches)),
                    Corners = ToBed(item.Box.Corners, 4, perPixelX, perPixelY)
                };

                if (item.Outline != null && item.Outline.Length >= 3)
                    planned.Outline = ToBed(item.Outline, item.Outline.Length, perPixelX, perPixelY);

                plan.Add(planned);
            }
            return plan;
        }

        static PointF[] ToBed(PointF[] source, int count, double perPixelX, double perPixelY)
        {
            PointF[] bed = new PointF[count];
            for (int index = 0; index < count; index++)
                bed[index] = new PointF((float)(source[index].X * perPixelX),
                                        (float)(source[index].Y * perPixelY));
            return bed;
        }

        /// <summary>
        /// The outlines to draw on the preview, as fractions of the preview.
        /// Drawn from the same geometry the plan is built from, so the operator
        /// cannot be shown one boundary and given another.
        /// </summary>
        public static List<PointF[]> PreviewOutlines(IList<CropRegion> items,
                                                     int previewWidth, int previewHeight)
        {
            List<PointF[]> outlines = new List<PointF[]>();
            if (items == null || previewWidth <= 0 || previewHeight <= 0) return outlines;

            foreach (CropRegion item in items)
            {
                if (item == null) continue;

                PointF[] shape = item.Outline;
                if (shape == null || shape.Length < 3)
                    shape = (item.Box != null) ? item.Box.Corners : null;

                if (shape != null && shape.Length >= 3)
                {
                    PointF[] normalised = new PointF[shape.Length];
                    for (int vertex = 0; vertex < shape.Length; vertex++)
                        normalised[vertex] = new PointF(shape[vertex].X / previewWidth,
                                                        shape[vertex].Y / previewHeight);
                    outlines.Add(normalised);
                    continue;
                }

                RectangleF box = item.NormRect;
                outlines.Add(new PointF[]
                {
                    new PointF(box.Left, box.Top), new PointF(box.Right, box.Top),
                    new PointF(box.Right, box.Bottom), new PointF(box.Left, box.Bottom)
                });
            }
            return outlines;
        }

        /// <summary>
        /// Cuts one planned item out of a scanned page.
        ///
        /// Three routes, in descending order of faithfulness: the approved
        /// free-form outline, the four approved corners, and finally the plain
        /// axis-aligned envelope. The last keeps the item's own tilt, so it is a
        /// fallback rather than a choice - callers that care should say so
        /// through <paramref name="note"/>.
        /// </summary>
        /// <param name="requestedRegion">The area of the glass this page covers, in inches.</param>
        public static RawImage Cut(RawImage page, CropPlanItem item, RectangleF requestedRegion,
                                   int index, bool deskew, out string note)
        {
            note = "";
            if (page == null || !page.IsValid || item == null) return null;
            if (requestedRegion.Width <= 0.01f || requestedRegion.Height <= 0.01f) return null;

            if (item.Outline != null && item.Outline.Length >= 3)
            {
                RawImage shaped = PolygonCropExtraction.Extract(
                    page, new PointF[][] { item.Outline }, item.Corners, requestedRegion, index, deskew);
                if (shaped != null) { note = "outline"; return shaped; }
                note = "the preview outline could not be applied";
                return null;
            }

            if (deskew && item.Corners != null && item.Corners.Length == 4)
            {
                RawImage straightened = PlannedCropExtraction.Extract(page, item.Corners, requestedRegion, index);
                if (straightened != null) { note = "corners"; return straightened; }
            }

            Rectangle box = EnvelopeInPixels(page, item.Bounds, requestedRegion);
            if (box.Width < 16 || box.Height < 16) { note = "outside the scanned area"; return null; }

            RawImage plain = CutRectangle(page, box);
            note = (plain != null) ? "envelope, not straightened" : "envelope cut failed";
            return plain;
        }

        /// <summary>The item's envelope in pixels of this page, clamped to it.</summary>
        public static Rectangle EnvelopeInPixels(RawImage page, RectangleF boundsInches,
                                                 RectangleF requestedRegion)
        {
            RectangleF norm = new RectangleF(
                (boundsInches.X - requestedRegion.X) / requestedRegion.Width,
                (boundsInches.Y - requestedRegion.Y) / requestedRegion.Height,
                boundsInches.Width / requestedRegion.Width,
                boundsInches.Height / requestedRegion.Height);

            Rectangle box = new Rectangle(
                (int)Math.Round(norm.X * page.Width), (int)Math.Round(norm.Y * page.Height),
                (int)Math.Round(norm.Width * page.Width), (int)Math.Round(norm.Height * page.Height));

            return Rectangle.Intersect(box, new Rectangle(0, 0, page.Width, page.Height));
        }

        /// <summary>Copies a rectangle out of a page, keeping its depth and resolution.</summary>
        public static RawImage CutRectangle(RawImage page, Rectangle box)
        {
            try
            {
                if (page == null || !page.IsValid || page.BitsPerChannel == 1) return null;
                if (box.Width <= 0 || box.Height <= 0) return null;

                int step = page.BitsPerChannel == 16 ? 2 : 1;
                int bytesPerPixel = page.Channels * step;

                RawImage cut = new RawImage
                {
                    Width = box.Width,
                    Height = box.Height,
                    Channels = page.Channels,
                    BitsPerChannel = page.BitsPerChannel,
                    Stride = box.Width * bytesPerPixel,
                    XDpi = page.XDpi,
                    YDpi = page.YDpi
                };
                cut.Pixels = new byte[(long)cut.Stride * cut.Height];

                for (int row = 0; row < box.Height; row++)
                    Array.Copy(page.Pixels, (long)(box.Y + row) * page.Stride + (long)box.X * bytesPerPixel,
                               cut.Pixels, (long)row * cut.Stride, cut.Stride);
                return cut;
            }
            catch { return null; }
        }
    }
}
