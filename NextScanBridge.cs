// =============================================================================
// NextScan native bridge for the existing Photoshop scanner helper
// Plan ref: NEXTSCAN_STUDIO_MASTER_PLAN.md section 3.2 / 5.1 / 7.
//
// Drop-in replacements for the three places scanhelper.cs shelled out to
// NAPS2.Console.exe. Everything here goes through NextScan.Engine.dll, which
// talks to the scanner over native TWAIN / WIA in a separate host process.
//
// Nothing in this file loads a scanner driver into the Photoshop helper process:
// that is the whole point of the host architecture, and it is why a misbehaving
// vendor driver can no longer take the UI down with it.
//
// The existing NAPS2 code paths are left intact and are still selectable with
// "engine=naps2" in scan.ini, so this can be reverted without a rebuild.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using NextScan.Core;

public static class NextScanBridge
{
    /// <summary>Set by the host application so bridge activity lands in scan_log.txt.</summary>
    public static Action<string> Log = delegate { };

    static DeviceBroker _broker;
    static List<ScannerEntry> _cachedScanners;

    static DeviceBroker Broker
    {
        get
        {
            if (_broker == null)
            {
                _broker = new DeviceBroker();
                _broker.Log = delegate (string m) { Log("[engine] " + m); };
                Log("native engine host directory: " + _broker.HostDirectory);
            }
            return _broker;
        }
    }

    /// <summary>True when the host executables are present and the engine can be used.</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return File.Exists(Broker.Host32Path) || File.Exists(Broker.Host64Path); }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ devices
    /// <summary>
    /// Enumerates scanners as "driver|name" pairs, matching the shape the existing
    /// device cache already uses.
    /// </summary>
    public static List<string> ListDevices()
    {
        List<string> result = new List<string>();
        try
        {
            _cachedScanners = Broker.Probe();
            foreach (ScannerEntry e in _cachedScanners)
            {
                foreach (DeviceDescriptor d in e.Connections)
                {
                    // The old UI only understands these two words, and a device
                    // reachable both ways should still appear once per transport so
                    // the user can pick.
                    string driver = (d.Transport == Transport.Twain) ? "twain" : "wia";
                    string line = driver + "|" + d.FriendlyName;
                    if (!result.Contains(line)) result.Add(line);
                }
            }
            Log("native engine found " + result.Count + " device connection(s)");
        }
        catch (Exception ex) { Log("native device enumeration failed: " + ex.Message); }
        return result;
    }

    /// <summary>Resolves a UI device name + driver word back to a concrete connection.</summary>
    static DeviceDescriptor Resolve(string deviceName, string driver)
    {
        try
        {
            if (_cachedScanners == null) _cachedScanners = Broker.Probe();

            Transport want = string.Equals(driver, "wia", StringComparison.OrdinalIgnoreCase)
                           ? Transport.Wia : Transport.Twain;

            DeviceDescriptor exact = null;
            DeviceDescriptor anyTransport = null;

            foreach (ScannerEntry e in _cachedScanners)
            {
                bool nameMatch = !string.IsNullOrEmpty(deviceName) &&
                    e.DisplayName.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!nameMatch) continue;

                foreach (DeviceDescriptor d in e.Connections)
                {
                    if (anyTransport == null) anyTransport = d;
                    if (d.Transport == want && exact == null) exact = d;
                }
            }

            if (exact != null) return exact;
            if (anyTransport != null)
            {
                Log("'" + deviceName + "' is not reachable over " + want +
                    "; falling back to " + anyTransport.Transport);
                return anyTransport;
            }

            // Nothing matched by name - fall back to the first scanner we can see so
            // a stale device name in scan.ini does not dead-end the user.
            foreach (ScannerEntry e in _cachedScanners)
            {
                if (e.Preferred != null)
                {
                    Log("no device matched '" + deviceName + "'; using " + e.DisplayName);
                    return e.Preferred;
                }
            }
        }
        catch (Exception ex) { Log("device resolution failed: " + ex.Message); }
        return null;
    }

    /// <summary>Forces the next call to re-enumerate rather than use the cache.</summary>
    public static void InvalidateCache() { _cachedScanners = null; }

    // ------------------------------------------------------------------ preview
    /// <summary>
    /// Low-resolution full-bed preview, written to previewPath as JPEG.
    /// Replaces the NAPS2 "--dpi 100 ... -f" invocation.
    /// </summary>
    public static bool PreviewScan(string deviceName, string driver, string previewPath, int dpi)
    {
        DeviceDescriptor dev = Resolve(deviceName, driver);
        if (dev == null) { Log("preview: no usable device"); return false; }

        ScanSettings s = new ScanSettings();
        s.Dpi = (dpi > 0) ? dpi : 100;
        s.Mode = NextScan.Core.ColorMode.Color24;   // always colour: the preview drives auto-detect
        s.Source = PaperSource.Flatbed;
        s.PageCount = 1;
        s.IsPreview = true;

        bool saved = false;
        try
        {
            NsResult r = Broker.Scan(dev, s, delegate (RawImage img)
            {
                saved = SaveImage(img, previewPath, "jpg");
                return false;   // preview is a single page
            }, null);

            if (!r.Ok) { Log("preview failed: " + r); return false; }
        }
        catch (Exception ex) { Log("preview threw: " + ex.Message); return false; }

        return saved && File.Exists(previewPath) && new FileInfo(previewPath).Length > 500;
    }

    // ------------------------------------------------------------------ full scan
    /// <summary>
    /// Final acquisition. Returns the files written, in page order.
    ///
    /// regionWidthIn/regionHeightIn of 0 mean "whole bed". Passing a region lets
    /// the scanner shorten its carriage travel, which is where most of the time
    /// saving on a partial-page scan comes from.
    /// </summary>
    public static List<string> FullScan(string deviceName, string driver, string outPath,
                                        int dpi, string bitDepth,
                                        double regionLeftIn, double regionTopIn,
                                        double regionWidthIn, double regionHeightIn,
                                        int pageCount, Action<string, int> onProgress)
    {
        List<string> written = new List<string>();

        DeviceDescriptor dev = Resolve(deviceName, driver);
        if (dev == null) { Log("scan: no usable device"); return written; }

        ScanSettings s = new ScanSettings();
        s.Dpi = (dpi > 0) ? dpi : 300;
        s.Mode = ParseMode(bitDepth);
        s.Source = PaperSource.Flatbed;
        s.PageCount = (pageCount > 0) ? pageCount : 1;
        s.RegionLeftIn = regionLeftIn;
        s.RegionTopIn = regionTopIn;
        s.RegionWidthIn = regionWidthIn;
        s.RegionHeightIn = regionHeightIn;

        string ext = Path.GetExtension(outPath);
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        ext = ext.TrimStart('.').ToLowerInvariant();

        Log(string.Format(CultureInfo.InvariantCulture,
            "native scan: {0} over {1} ({2}-bit host), {3} dpi, {4}, region {5:0.##}x{6:0.##} in",
            dev.FriendlyName, dev.Transport, dev.HostBitness, s.Dpi, s.Mode, regionWidthIn, regionHeightIn));

        if (onProgress != null) onProgress("Initializing " + dev.FriendlyName + "...", 15);

        try
        {
            NsResult r = Broker.Scan(dev, s, delegate (RawImage img)
            {
                string path = outPath;
                if (written.Count > 0)
                {
                    string dir = Path.GetDirectoryName(outPath);
                    string stem = Path.GetFileNameWithoutExtension(outPath);
                    path = Path.Combine(dir, stem + "_" +
                        (written.Count + 1).ToString("000", CultureInfo.InvariantCulture) + "." + ext);
                }

                if (SaveImage(img, path, ext))
                {
                    written.Add(path);
                    Log("acquired page " + (img.PageIndex + 1) + ": " + img + " -> " + path);
                }
                return true;
            },
            onProgress);

            if (!r.Ok)
            {
                Log("native scan failed: " + r);
                LastError = r;
            }
            else LastError = null;
        }
        catch (Exception ex)
        {
            Log("native scan threw: " + ex.Message);
            LastError = NsResult.Fail(NsError.Unknown, ex.Message, "");
        }

        return written;
    }

    /// <summary>Details of the most recent failure, for showing the user a real message.</summary>
    public static NsResult LastError;

    /// <summary>Message plus suggested remedy from the last failure, or "".</summary>
    public static string LastErrorText
    {
        get
        {
            NsResult e = LastError;
            if (e == null || e.Ok) return "";
            return e.Message + (string.IsNullOrEmpty(e.Remedy) ? "" : "\r\n\r\n" + e.Remedy);
        }
    }

    // ------------------------------------------------------------------ helpers
    static NextScan.Core.ColorMode ParseMode(string bitDepth)
    {
        string b = (bitDepth ?? "color").Trim().ToLowerInvariant();
        if (b.StartsWith("gray") || b.StartsWith("grey")) return NextScan.Core.ColorMode.Gray8;
        if (b.StartsWith("bw") || b.StartsWith("black") || b.Contains("mono")) return NextScan.Core.ColorMode.BlackWhite1;
        return NextScan.Core.ColorMode.Color24;
    }

    static bool SaveImage(RawImage img, string path, string ext)
    {
        if (img == null || !img.IsValid) { Log("refusing to save an invalid image buffer"); return false; }

        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using (Bitmap bmp = img.ToBitmap())
            {
                if (bmp == null) return false;

                switch ((ext ?? "jpg").ToLowerInvariant())
                {
                    case "png":
                        bmp.Save(path, ImageFormat.Png);
                        break;
                    case "tif":
                    case "tiff":
                        bmp.Save(path, ImageFormat.Tiff);
                        break;
                    case "bmp":
                        bmp.Save(path, ImageFormat.Bmp);
                        break;
                    default:
                        SaveJpeg(bmp, path, 92L);
                        break;
                }
            }
            return true;
        }
        catch (Exception ex) { Log("saving " + path + " failed: " + ex.Message); return false; }
    }

    static void SaveJpeg(Bitmap bmp, string path, long quality)
    {
        ImageCodecInfo enc = null;
        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) { enc = c; break; }

        if (enc == null) { bmp.Save(path, ImageFormat.Jpeg); return; }

        using (EncoderParameters ep = new EncoderParameters(1))
        {
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            bmp.Save(path, enc, ep);
        }
    }
}
