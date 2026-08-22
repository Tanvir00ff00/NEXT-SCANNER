# =============================================================================
# NextScan Studio - colour channel order verification (HANDOFF section 8.3)
#
#   .\tests\check_channel_order.ps1 <imageA> <imageB>
#
# Scans the same physical bed region over two transports (TWAIN + WIA) and
# proves the channel ORDER agrees: after aligning the two images on the green
# channel (whose position is invariant under an R/B swap), TWAIN-red must
# correlate with WIA-red, not WIA-blue. Needs some colour on the bed; reports
# "inconclusive" when the scene is effectively grey.
# =============================================================================
param([Parameter(Mandatory=$true)][string]$PathA, [Parameter(Mandatory=$true)][string]$PathB)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;

public static class ChannelOrderCheck
{
    static byte[][] Load(string path, out int w, out int h)
    {
        using (Bitmap b = new Bitmap(path))
        {
            w = b.Width; h = b.Height;
            Rectangle r = new Rectangle(0, 0, w, h);
            BitmapData d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            byte[] px = new byte[d.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
            b.UnlockBits(d);
            byte[] R = new byte[w * h], G = new byte[w * h], B = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * d.Stride + x * 3;   // 24bpp rows are BGR
                    int i = y * w + x;
                    R[i] = px[s + 2]; G[i] = px[s + 1]; B[i] = px[s];
                }
            return new byte[][] { R, G, B };
        }
    }

    // Normalized correlation of a(x,y) vs b(x-dx, y-dy) over the overlap.
    static double Corr(byte[] a, byte[] b, int w, int h, int dx, int dy)
    {
        int xs = Math.Max(0, dx), xe = Math.Min(w, w + dx);
        int ys = Math.Max(0, dy), ye = Math.Min(h, h + dy);
        if (xe - xs < 32 || ye - ys < 32) return -1;
        long n = 0; double sa = 0, sb = 0;
        for (int y = ys; y < ye; y++)
            for (int x = xs; x < xe; x++)
            { sa += a[y * w + x]; sb += b[(y - dy) * w + (x - dx)]; n++; }
        double ma = sa / n, mb = sb / n;
        double num = 0, va = 0, vb = 0;
        for (int y = ys; y < ye; y++)
            for (int x = xs; x < xe; x++)
            {
                double da = a[y * w + x] - ma;
                double db = b[(y - dy) * w + (x - dx)] - mb;
                num += da * db; va += da * da; vb += db * db;
            }
        if (va <= 0 || vb <= 0) return -1;
        return num / Math.Sqrt(va * vb);
    }

    // Returns: colourFrac|dx|dy|greenCorr|RR|RB|BB|BR|GG
    public static string Run(string pa, string pb)
    {
        int wa, ha, wb, hb;
        byte[][] A = Load(pa, out wa, out ha);
        byte[][] B = Load(pb, out wb, out hb);
        if (wa != wb || ha != hb)
            return "SIZE|" + wa + "x" + ha + "|" + wb + "x" + hb;

        long colorful = 0, total = 0;
        for (int i = 0; i < wa * ha; i += 7)
        {
            total++;
            if (Math.Abs(A[0][i] - A[2][i]) > 40) colorful++;
        }
        double colorFrac = (double)colorful / total;

        int bestDx = 0, bestDy = 0; double bestC = -2;
        for (int dy = -8; dy <= 8; dy++)
            for (int dx = -8; dx <= 8; dx++)
            {
                double c = Corr(A[1], B[1], wa, ha, dx, dy);
                if (c > bestC) { bestC = c; bestDx = dx; bestDy = dy; }
            }

        double rr = Corr(A[0], B[0], wa, ha, bestDx, bestDy);
        double rb = Corr(A[0], B[2], wa, ha, bestDx, bestDy);
        double bb = Corr(A[2], B[2], wa, ha, bestDx, bestDy);
        double br = Corr(A[2], B[0], wa, ha, bestDx, bestDy);
        double gg = Corr(A[1], B[1], wa, ha, bestDx, bestDy);

        return colorFrac.ToString("F4") + "|" + bestDx + "|" + bestDy + "|" + bestC.ToString("F4")
             + "|" + rr.ToString("F4") + "|" + rb.ToString("F4")
             + "|" + bb.ToString("F4") + "|" + br.ToString("F4") + "|" + gg.ToString("F4");
    }
}
"@

$r = [ChannelOrderCheck]::Run($PathA, $PathB) -split '\|'
if ($r[0] -eq "SIZE") { Write-Host "FAIL: size mismatch $($r[1]) vs $($r[2])"; exit 1 }

$colorFrac = [double]$r[0]; $dx = $r[1]; $dy = $r[2]; $gAlign = [double]$r[3]
$RR = [double]$r[4]; $RB = [double]$r[5]; $BB = [double]$r[6]; $BR = [double]$r[7]; $GG = [double]$r[8]

Write-Host "channel order check"
Write-Host ("  A: " + $PathA)
Write-Host ("  B: " + $PathB)
Write-Host ("  colour fraction (|R-B|>40) : {0:P2}" -f $colorFrac)
Write-Host ("  best alignment             : dx=$dx dy=$dy (green corr {0:F4})" -f $gAlign)
Write-Host ("  corr A.R vs B.R : {0:F4}    A.R vs B.B : {1:F4}" -f $RR, $RB)
Write-Host ("  corr A.B vs B.B : {0:F4}    A.B vs B.R : {1:F4}" -f $BB, $BR)
Write-Host ("  corr A.G vs B.G : {0:F4}" -f $GG)
Write-Host ""

if ($gAlign -lt 0.80) {
    Write-Host "INCONCLUSIVE: the two scans do not align (green corr < 0.80)." -ForegroundColor Yellow
    exit 2
}
if ($colorFrac -lt 0.005) {
    Write-Host "INCONCLUSIVE: the bed has effectively no colour; place a saturated colour on the glass and rerun." -ForegroundColor Yellow
    exit 2
}
if ($RR -gt $RB + 0.05 -and $BB -gt $BR + 0.05) {
    Write-Host "PASS: red aligns with red and blue with blue - channel order agrees between the two transports." -ForegroundColor Green
    exit 0
}
if ($RB -gt $RR + 0.05 -and $BR -gt $BB + 0.05) {
    Write-Host "FAIL: red aligns with BLUE - one transport has R and B exchanged." -ForegroundColor Red
    exit 1
}
Write-Host "INCONCLUSIVE: correlation margin too small (|RR-RB| < 0.05)." -ForegroundColor Yellow
exit 2
