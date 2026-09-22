// =============================================================================
// NextScan Studio - Studio settings & preset persistence
// Plan ref: MASTER_PLAN section 13.1, 16.
//
// Maintains application preferences, active scanner configuration, crop state,
// tone presets, and export options. Integrates with scan.ini for backwards
// compatibility with Photoshop bridge workflows.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using NextScan.Core;

namespace NextScan.App
{
    public class StudioSettings
    {
        public string DeviceName = "";
        public Transport Transport = Transport.None;
        public int HostBitness = 64;

        /// <summary>
        /// A profile the operator chose for this scanner, or empty.
        ///
        /// Filed with the scanner rather than with the job, because that is what
        /// it describes. It therefore survives a restart and is not carried by
        /// presets: a profile measured from this machine's scanner would be a
        /// lie about anyone else's.
        /// </summary>
        public string ColorProfilePath = "";

        // ---- the assistant ----------------------------------------------------
        //
        // Which model the operator talks to, and how hard it thinks. Filed with
        // the machine rather than with the job, alongside the scanner and its
        // colour profile: a preset that carried a provider would hand the next
        // machine a choice about somebody else's account.
        //
        // Stored as text rather than as NextScan.Ai types on purpose. This file
        // is read and written by the engine's own ini code, and a settings
        // reader that could not run without the provider SDKs present would be
        // a dependency in the one place that has none.
        //
        // The API keys are NOT here. They are secrets, they live encrypted
        // under DPAPI, and this file is copied around as a preset.
        public string AiProvider = "claude";
        public string AiModel = "";
        public string AiThinking = "Medium";

        public int Dpi = 300;
        public ColorMode Mode = ColorMode.Color24;

        // ---- preview ----------------------------------------------------------
        //
        // A preview is a different job from a scan and deserves its own numbers.
        // It is looked at, not kept, and it is what auto crop measures: too
        // coarse and small items are missed, too fine and every preview costs
        // seconds of lamp time for detail nobody will see.
        //
        // 100 dpi is the shipped default because an A4 bed lands at roughly
        // 3 MB there and detection measures an ID card to within a millimetre.
        // 300, matching the scan, because auto crop measures the preview and
        // not the scan: a coarse preview finds fewer small items and places
        // their edges less precisely, and that cost lands on every page. A
        // preview costs seconds; a crop measured a millimetre out costs the
        // page. PreviewDpi() still clamps this to what the device offers.
        public int PreviewDpi = 300;
        public ColorMode PreviewMode = ColorMode.Color24;

        /// <summary>
        /// Use the scan's own resolution for the preview as well.
        ///
        /// Off by default. Someone scanning at 1200 dpi does not want to wait
        /// for a 1200 dpi preview, but someone checking fine detail may.
        /// </summary>
        public bool PreviewMatchesScan;

        /// <summary>
        /// Let the segmentation model check what the preview found.
        ///
        /// On where a model is installed, because what it corrects is the class
        /// of mistake the operator actually sees: a card delivered in pieces, a
        /// card delivered at half its height, a passport page that reads the
        /// same as the glass under it and is never found at all. It costs about
        /// a second and a half of the preview and nothing at scan time, and with
        /// no model installed the detector runs exactly as it did before.
        /// </summary>
        public bool UseModel = true;

        /// <summary>
        /// Preview the whole glass, whatever area is selected.
        ///
        /// On by default, because a preview is how the operator finds out what
        /// is on the bed at all, and one cropped to last time's selection cannot
        /// show them anything new. Off, a preview covers exactly the selection,
        /// which is what you want once the selection is the thing being adjusted.
        /// </summary>
        public PaperSource Source = PaperSource.Flatbed;
        public string PaperSize = "Maximum";
        public bool PaperLandscape = false;
        public double RegionLeftIn;
        public double RegionTopIn;
        public double RegionWidthIn;
        public double RegionHeightIn;
        public int PageCount = 1;
        public bool ShowVendorUi;
        public bool AutoDeskew = true;
        public DeskewProfileKind DeskewProfile = DeskewProfileKind.Standard;
        public bool AutoCrop = true;

        /// <summary>
        /// Several items on one glass become several pages. Only consulted when
        /// AutoCrop is on, because finding one document is a prerequisite for
        /// deciding there are four of them.
        /// </summary>
        public bool MultiRegionCrop = true;
        /// <summary>
        /// Paper polish, 0..100. Not the old "Whitening": that added a flat
        /// amount to every channel above a hard luminance cutoff, which shifted
        /// hue and clipped highlights. See ToneEngine.Polish.
        /// </summary>
        public int Polish = 8;

        /// <summary>How the page is rendered. Original colour is deliberately faithful.</summary>
        public TonePreset Tone = TonePreset.OriginalColour;
        public int Brightness;
        public int Contrast;
        public int BwThreshold = 128;
        public bool AdaptiveThreshold = true;

        // ---- the advanced colour controls, 1.2 ----
        // Zero and Off throughout, so a settings file written before these
        // existed loads as the behaviour it had.
        public AutoTone AutoTone = AutoTone.Off;
        public int Saturation;
        public int Vibrance;
        public int Temperature;
        public int Tint;
        public int Highlights;
        public int Shadows;

        // ---- the clean-up pass ----
        public int DescreenLpi;
        public int Sharpen;
        public int BackgroundClean;
        public int Despeckle;
        public bool OpenInPhotoshop = true;
        public bool KeepFiles = true;

        public string OutputFormat = "jpg"; // jpg, png, tif, pdf
        public string OutputDirectory = @"C:\PS_Fix\scans";

        /// <summary>
        /// Output name template. See NameTemplate for the tokens; the default
        /// carries a counter so repeated scans never land on one name.
        /// </summary>
        public string OutputNamePattern = "scan_{date}_{nnn}";
        public int JpegQuality = 92;

        /// <summary>PDF and TIFF only: one file for the whole session.</summary>
        public bool MultiPageFile = true;

        // ---- batch -----------------------------------------------------------
        public SeparationRule BatchSeparation = SeparationRule.None;
        public int PagesPerDocument = 1;
        public bool DropBlankPages;

        /// <summary>
        /// Keep feeding until the ADF is empty rather than stopping after
        /// PageCount pages. Meaningless on a flatbed.
        /// </summary>
        public bool BatchUntilEmpty;

        // ---- hot folder ------------------------------------------------------
        /// <summary>Folder to watch; empty means the feature is not configured.</summary>
        public string HotFolderPath = "";

        /// <summary>Files that land together become one document.</summary>
        public bool HotFolderGroupPerBatch;

        /// <summary>What happens to an input file once it has been processed.</summary>
        public SourceDisposition HotFolderDisposition = SourceDisposition.MoveToSubfolder;

        // Normalized crop rectangle: (0,0,1,1) = full bed
        public RectangleF CropNorm = new RectangleF(0f, 0f, 1f, 1f);

        // Spline curve control points: normalized [0, 255]
        public List<PointF> CurvePointsRGB = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
        public List<PointF> CurvePointsR = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
        public List<PointF> CurvePointsG = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
        public List<PointF> CurvePointsB = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };

        public static string DefaultIniPath
        {
            get { return @"C:\PS_Fix\scan.ini"; }
        }

        public static StudioSettings Load(string iniPath = null)
        {
            StudioSettings s = new StudioSettings();
            string path = iniPath ?? DefaultIniPath;
            if (!File.Exists(path)) return s;

            try
            {
                string[] lines = File.ReadAllLines(path);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "device": s.DeviceName = val; break;
                        case "driver":
                            if (val.Equals("wia", StringComparison.OrdinalIgnoreCase)) s.Transport = Transport.Wia;
                            else if (val.Equals("escl", StringComparison.OrdinalIgnoreCase)) s.Transport = Transport.Escl;
                            else s.Transport = Transport.Twain;
                            break;
                        case "dpi":
                            int d;
                            if (int.TryParse(val, out d) && d > 0) s.Dpi = d;
                            break;
                        case "previewdpi":
                            int pd;
                            if (int.TryParse(val, out pd) && pd > 0) s.PreviewDpi = pd;
                            break;
                        case "autotone":
                            try { s.AutoTone = (AutoTone)Enum.Parse(typeof(AutoTone), val, true); }
                            catch { s.AutoTone = AutoTone.Off; }
                            break;
                        case "saturation": { int v; if (int.TryParse(val, out v) && v >= -100 && v <= 100) s.Saturation = v; break; }
                        case "vibrance": { int v; if (int.TryParse(val, out v) && v >= -100 && v <= 100) s.Vibrance = v; break; }
                        case "temperature": { int v; if (int.TryParse(val, out v) && v >= -100 && v <= 100) s.Temperature = v; break; }
                        case "tint": { int v; if (int.TryParse(val, out v) && v >= -100 && v <= 100) s.Tint = v; break; }
                        case "highlights": { int v; if (int.TryParse(val, out v) && v >= -100 && v <= 100) s.Highlights = v; break; }
                        case "shadows": { int v; if (int.TryParse(val, out v) && v >= -100 && v <= 100) s.Shadows = v; break; }
                        case "descreenlpi": { int v; if (int.TryParse(val, out v) && v >= 0 && v <= 400) s.DescreenLpi = v; break; }
                        case "sharpen": { int v; if (int.TryParse(val, out v) && v >= 0 && v <= 100) s.Sharpen = v; break; }
                        case "backgroundclean": { int v; if (int.TryParse(val, out v) && v >= 0 && v <= 100) s.BackgroundClean = v; break; }
                        case "despeckle": { int v; if (int.TryParse(val, out v) && v >= 0 && v <= 100) s.Despeckle = v; break; }
                        case "colorprofile": s.ColorProfilePath = val; break;
                        case "aiprovider": if (val.Length > 0) s.AiProvider = val.ToLowerInvariant(); break;
                        case "aimodel": s.AiModel = val; break;
                        case "aithinking": if (val.Length > 0) s.AiThinking = val; break;
                        case "usemodel":
                            s.UseModel = !val.Equals("off", StringComparison.OrdinalIgnoreCase)
                                && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "previewmatchesscan":
                            s.PreviewMatchesScan = val.Equals("on", StringComparison.OrdinalIgnoreCase)
                                || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "previewmode":
                            try { s.PreviewMode = (ColorMode)Enum.Parse(typeof(ColorMode), val, true); }
                            catch
                            {
                                if (val.StartsWith("gray", StringComparison.OrdinalIgnoreCase)) s.PreviewMode = ColorMode.Gray8;
                                else s.PreviewMode = ColorMode.Color24;
                            }
                            break;
                        case "colormode":
                        case "bitdepth":
                            try { s.Mode = (ColorMode)Enum.Parse(typeof(ColorMode), val, true); }
                            catch
                            {
                                if (val.StartsWith("gray", StringComparison.OrdinalIgnoreCase)) s.Mode = ColorMode.Gray8;
                                else if (val.StartsWith("bw", StringComparison.OrdinalIgnoreCase)) s.Mode = ColorMode.BlackWhite1;
                                else s.Mode = ColorMode.Color24;
                            }
                            break;
                        case "source":
                            try { s.Source = (PaperSource)Enum.Parse(typeof(PaperSource), val, true); }
                            catch
                            {
                                if (val.Equals("feeder", StringComparison.OrdinalIgnoreCase)) s.Source = PaperSource.Feeder;
                                else if (val.Equals("duplex", StringComparison.OrdinalIgnoreCase)) s.Source = PaperSource.FeederDuplex;
                                else s.Source = PaperSource.Flatbed;
                            }
                            break;
                        case "format": s.OutputFormat = val.ToLowerInvariant(); break;
                        case "outdir": if (val.Length > 0) s.OutputDirectory = val; break;
                        case "namepattern": if (val.Length > 0) s.OutputNamePattern = val; break;
                        case "jpegquality": { int q; if (int.TryParse(val, out q) && q >= 1 && q <= 100) s.JpegQuality = q; break; }
                        case "multipage": s.MultiPageFile = IsOn(val); break;
                        case "dropblank": s.DropBlankPages = IsOn(val); break;
                        case "batchuntilempty": s.BatchUntilEmpty = IsOn(val); break;
                        case "hotfolder": s.HotFolderPath = val; break;
                        case "hotgroup": s.HotFolderGroupPerBatch = IsOn(val); break;
                        case "hotsource":
                            if (val.Equals("delete", StringComparison.OrdinalIgnoreCase)) s.HotFolderDisposition = SourceDisposition.Delete;
                            else if (val.Equals("leave", StringComparison.OrdinalIgnoreCase)) s.HotFolderDisposition = SourceDisposition.LeaveInPlace;
                            else s.HotFolderDisposition = SourceDisposition.MoveToSubfolder;
                            break;
                        case "pagesperdoc": { int n; if (int.TryParse(val, out n) && n > 0) s.PagesPerDocument = n; break; }
                        case "separation":
                            if (val.Equals("count", StringComparison.OrdinalIgnoreCase)) s.BatchSeparation = SeparationRule.FixedPageCount;
                            else if (val.Equals("blank", StringComparison.OrdinalIgnoreCase)) s.BatchSeparation = SeparationRule.BlankPage;
                            else s.BatchSeparation = SeparationRule.None;
                            break;
                        case "papersize":
                        case "paper_size": s.PaperSize = val; break;
                        case "paperlandscape":
                        case "paper_landscape": s.PaperLandscape = val.Equals("on", StringComparison.OrdinalIgnoreCase) || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "openinps": s.OpenInPhotoshop = val.Equals("on", StringComparison.OrdinalIgnoreCase) || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "keepfiles": s.KeepFiles = val.Equals("on", StringComparison.OrdinalIgnoreCase) || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "deskew": s.AutoDeskew = val.Equals("on", StringComparison.OrdinalIgnoreCase) || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        // "whitening" from an older build is deliberately not
                        // read: the number meant something else on the additive
                        // algorithm it belonged to, and its old default of 40 is
                        // the setting that was blowing out highlights.
                        case "autocrop": s.AutoCrop = IsOn(val); break;
                        case "multicrop": s.MultiRegionCrop = IsOn(val); break;
                        case "polish": { int v; if (int.TryParse(val, out v) && v >= 0 && v <= 100) s.Polish = v; break; }
                        case "brightness": { int v; if (int.TryParse(val, out v) && v >= -100 && v <= 100) s.Brightness = v; break; }
                        case "contrast": { int v; if (int.TryParse(val, out v) && v >= -100 && v <= 100) s.Contrast = v; break; }
                        case "bwthreshold": { int v; if (int.TryParse(val, out v) && v >= 0 && v <= 255) s.BwThreshold = v; break; }
                        case "adaptivethreshold": s.AdaptiveThreshold = IsOn(val); break;
                        case "tone":
                            try { s.Tone = (TonePreset)Enum.Parse(typeof(TonePreset), val, true); }
                            catch { s.Tone = TonePreset.OriginalColour; }
                            break;
                        case "crop_x": { float v; if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) s.CropNorm = new RectangleF(v, s.CropNorm.Y, s.CropNorm.Width, s.CropNorm.Height); break; }
                        case "crop_y": { float v; if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) s.CropNorm = new RectangleF(s.CropNorm.X, v, s.CropNorm.Width, s.CropNorm.Height); break; }
                        case "crop_w": { float v; if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) s.CropNorm = new RectangleF(s.CropNorm.X, s.CropNorm.Y, v, s.CropNorm.Height); break; }
                        case "crop_h": { float v; if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) s.CropNorm = new RectangleF(s.CropNorm.X, s.CropNorm.Y, s.CropNorm.Width, v); break; }
                    }
                }
            }
            catch { }

            // Ensure valid crop bounds
            s.CropNorm = ClampCrop(s.CropNorm);
            return s;
        }

        public void Save(string iniPath = null)
        {
            string path = iniPath ?? DefaultIniPath;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                List<string> lines = new List<string>();
                lines.Add("# NextScan Studio Configuration (Auto-saved)");
                lines.Add("engine=native");
                lines.Add("device=" + DeviceName);
                lines.Add("driver=" + (Transport == Transport.Wia ? "wia" : (Transport == Transport.Escl ? "escl" : "twain")));
                lines.Add("dpi=" + Dpi);
                lines.Add("colormode=" + Mode);
                lines.Add("previewdpi=" + PreviewDpi.ToString(CultureInfo.InvariantCulture));
                lines.Add("previewmode=" + PreviewMode);
                lines.Add("previewmatchesscan=" + (PreviewMatchesScan ? "on" : "off"));
                lines.Add("usemodel=" + (UseModel ? "on" : "off"));
                lines.Add("colorprofile=" + ColorProfilePath);
                lines.Add("aiprovider=" + AiProvider);
                lines.Add("aimodel=" + AiModel);
                lines.Add("aithinking=" + AiThinking);
                lines.Add("autotone=" + AutoTone);
                lines.Add("saturation=" + Saturation.ToString(CultureInfo.InvariantCulture));
                lines.Add("vibrance=" + Vibrance.ToString(CultureInfo.InvariantCulture));
                lines.Add("temperature=" + Temperature.ToString(CultureInfo.InvariantCulture));
                lines.Add("tint=" + Tint.ToString(CultureInfo.InvariantCulture));
                lines.Add("highlights=" + Highlights.ToString(CultureInfo.InvariantCulture));
                lines.Add("shadows=" + Shadows.ToString(CultureInfo.InvariantCulture));
                lines.Add("descreenlpi=" + DescreenLpi.ToString(CultureInfo.InvariantCulture));
                lines.Add("sharpen=" + Sharpen.ToString(CultureInfo.InvariantCulture));
                lines.Add("backgroundclean=" + BackgroundClean.ToString(CultureInfo.InvariantCulture));
                lines.Add("despeckle=" + Despeckle.ToString(CultureInfo.InvariantCulture));
                lines.Add("source=" + Source.ToString().ToLowerInvariant());
                lines.Add("format=" + OutputFormat);
                lines.Add("outdir=" + OutputDirectory);
                lines.Add("namepattern=" + OutputNamePattern);
                lines.Add("jpegquality=" + JpegQuality.ToString(CultureInfo.InvariantCulture));
                lines.Add("multipage=" + (MultiPageFile ? "on" : "off"));
                lines.Add("separation=" + (BatchSeparation == SeparationRule.FixedPageCount ? "count"
                                        : BatchSeparation == SeparationRule.BlankPage ? "blank" : "none"));
                lines.Add("pagesperdoc=" + PagesPerDocument.ToString(CultureInfo.InvariantCulture));
                lines.Add("dropblank=" + (DropBlankPages ? "on" : "off"));
                lines.Add("batchuntilempty=" + (BatchUntilEmpty ? "on" : "off"));
                lines.Add("hotfolder=" + HotFolderPath);
                lines.Add("hotgroup=" + (HotFolderGroupPerBatch ? "on" : "off"));
                lines.Add("hotsource=" + (HotFolderDisposition == SourceDisposition.Delete ? "delete"
                                       : HotFolderDisposition == SourceDisposition.LeaveInPlace ? "leave" : "move"));
                lines.Add("paper_size=" + PaperSize);
                lines.Add("paper_landscape=" + (PaperLandscape ? "on" : "off"));
                lines.Add("openinps=" + (OpenInPhotoshop ? "on" : "off"));
                lines.Add("keepfiles=" + (KeepFiles ? "on" : "off"));
                lines.Add("deskew=" + (AutoDeskew ? "on" : "off"));
                lines.Add("autocrop=" + (AutoCrop ? "on" : "off"));
                lines.Add("multicrop=" + (MultiRegionCrop ? "on" : "off"));
                lines.Add("tone=" + Tone);
                lines.Add("polish=" + Polish.ToString(CultureInfo.InvariantCulture));
                lines.Add("brightness=" + Brightness.ToString(CultureInfo.InvariantCulture));
                lines.Add("contrast=" + Contrast.ToString(CultureInfo.InvariantCulture));
                lines.Add("bwthreshold=" + BwThreshold.ToString(CultureInfo.InvariantCulture));
                lines.Add("adaptivethreshold=" + (AdaptiveThreshold ? "on" : "off"));
                lines.Add("detect=engine");
                lines.Add("crop_x=" + CropNorm.X.ToString("0.####", CultureInfo.InvariantCulture));
                lines.Add("crop_y=" + CropNorm.Y.ToString("0.####", CultureInfo.InvariantCulture));
                lines.Add("crop_w=" + CropNorm.Width.ToString("0.####", CultureInfo.InvariantCulture));
                lines.Add("crop_h=" + CropNorm.Height.ToString("0.####", CultureInfo.InvariantCulture));

                File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// Accepts every spelling this file has ever been written with, so a
        /// scan.ini from an older build still reads correctly.
        /// </summary>
        static bool IsOn(string val)
        {
            return val.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                   val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   val.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   val.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public static RectangleF ClampCrop(RectangleF r)
        {
            float x = Math.Max(0f, Math.Min(0.99f, r.X));
            float y = Math.Max(0f, Math.Min(0.99f, r.Y));
            float w = Math.Max(0.01f, Math.Min(1f - x, r.Width <= 0 ? 1f : r.Width));
            float h = Math.Max(0.01f, Math.Min(1f - y, r.Height <= 0 ? 1f : r.Height));
            return new RectangleF(x, y, w, h);
        }
    }
}
