# =============================================================================
# NextScan Studio - TWAIN simulator golden-image + behaviour harness
# Plan ref: MASTER_PLAN section 18.2/18.3, ADR-0002.
#
#   .\tests\run_golden.ps1              verify against committed references
#   .\tests\run_golden.ps1 -Generate    (re)create the references from the
#                                       simulator's current output
#   .\tests\run_golden.ps1 -WithHang    also run the 10-minute watchdog proof
#
# Every case pins the fake DSM through NEXTSCAN_TWAIN_DSM and runs the REAL
# production path: nsprobe -> DeviceBroker -> host process -> TWAIN session ->
# simulator DLL. References are compared pixel-exactly (decoded 24bpp rows,
# width*3 bytes each, plus dimensions and DPI); the PNG container itself is not
# compared byte-for-byte because GDI+ encoder output may vary between
# framework versions - the pixels may not.
# =============================================================================
param(
    [switch]$Generate,
    [switch]$WithHang
)

$ErrorActionPreference = "Stop"
$root  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$bin   = Join-Path $root "bin"
$refDir = Join-Path $root "tests\golden"
$outDir = Join-Path $root "out\golden"
$nsprobe = Join-Path $bin "nsprobe.exe"
$simX86  = Join-Path $bin "sim\x86\TWAINDSM.DLL"
$simX64  = Join-Path $bin "sim\x64\TWAINDSM.DLL"

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $nsprobe)) { throw "nsprobe.exe not built. Run .\build.ps1 first." }
if (-not (Test-Path $simX86))  { throw "simulator not built. Run .\sim\build_sim.ps1 first." }
if (-not (Test-Path $refDir))  { New-Item -ItemType Directory -Path $refDir -Force | Out-Null }
if (-not (Test-Path $outDir))  { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# ---------------------------------------------------------------- image compare
function Compare-BitmapPixels {
    param([string]$PathA, [string]$PathB)
    $ba = New-Object System.Drawing.Bitmap($PathA)
    $bb = New-Object System.Drawing.Bitmap($PathB)
    try {
        if ($ba.Width -ne $bb.Width -or $ba.Height -ne $bb.Height) {
            return "size $($ba.Width)x$($ba.Height) vs $($bb.Width)x$($bb.Height)"
        }
        # DPI is stored as single precision in PNG; allow rounding noise.
        $dpiBad = ([math]::Abs($ba.HorizontalResolution - $bb.HorizontalResolution) -gt 1.5) -or
                  ([math]::Abs($ba.VerticalResolution   - $bb.VerticalResolution)   -gt 1.5)
        if ($dpiBad) {
            return "dpi $($ba.HorizontalResolution)x$($ba.VerticalResolution) vs $($bb.HorizontalResolution)x$($bb.VerticalResolution)"
        }

        $ra = $ba.LockBits((New-Object System.Drawing.Rectangle(0,0,$ba.Width,$ba.Height)), 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $rb = $bb.LockBits((New-Object System.Drawing.Rectangle(0,0,$bb.Width,$bb.Height)), 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        try {
            $rowBytes = $ba.Width * 3
            $rowA = New-Object byte[] $ra.Stride
            $rowB = New-Object byte[] $rb.Stride
            for ($y = 0; $y -lt $ba.Height; $y++) {
                [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($ra.Scan0.ToInt64() + $y * $ra.Stride), $rowA, 0, $ra.Stride)
                [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($rb.Scan0.ToInt64() + $y * $rb.Stride), $rowB, 0, $rb.Stride)
                for ($i = 0; $i -lt $rowBytes; $i++) {
                    if ($rowA[$i] -ne $rowB[$i]) {
                        return "pixel diff at row $y byte $i ($($rowA[$i]) vs $($rowB[$i]))"
                    }
                }
            }
        }
        finally { $ba.UnlockBits($ra); $bb.UnlockBits($rb) }
        return $null   # identical
    }
    finally { $ba.Dispose(); $bb.Dispose() }
}

# The duplex personality delivers the back side rotated 180 degrees; this is
# the assertion that catches a back side arriving unrotated or misordered.
function Test-Rot180 {
    param([string]$Front, [string]$Back)
    $bf = New-Object System.Drawing.Bitmap($Front)
    $bb = New-Object System.Drawing.Bitmap($Back)
    try {
        if ($bf.Width -ne $bb.Width -or $bf.Height -ne $bb.Height) { return "size mismatch" }
        for ($x = 0; $x -lt $bf.Width; $x += 7) {
            for ($y = 0; $y -lt $bf.Height; $y += 5) {
                $pf = $bf.GetPixel($x, $y)
                $pb = $bb.GetPixel($bf.Width - 1 - $x, $bf.Height - 1 - $y)
                if ($pf.R -ne $pb.R -or $pf.G -ne $pb.G -or $pf.B -ne $pb.B) {
                    return "back($($bf.Width-1-$x),$($bf.Height-1-$y)) != front($x,$y)"
                }
            }
        }
        return $null
    }
    finally { $bf.Dispose(); $bb.Dispose() }
}

# ---------------------------------------------------------------- case table
# Check values:
#   golden      compare page 1 against the reference PNG
#   golden2     compare pages 1 and 2 (_002 suffix) and assert rot180 relation
#   text:<pat>  behavioural - assert the pattern appears in nsprobe output
#   fail:<pat>  behavioural - assert nsprobe FAILED with the pattern
$cases = @(
    @{ Name="wb_bars_color24";      Pers="";           Img="";       Args=@("--dpi","150","--region","0,0,3,2");                 Check="golden" }
    @{ Name="wb_gradient_color24";  Pers="";           Img="gradient"; Args=@("--dpi","150","--region","0,0,3,2");              Check="golden" }
    @{ Name="wb_checker_color24";   Pers="";           Img="checker"; Args=@("--dpi","150","--region","0,0,3,2");              Check="golden" }
    @{ Name="wb_bars_gray8";        Pers="";           Img="";       Args=@("--mode","Gray8","--dpi","150","--region","0,0,3,2"); Check="golden" }
    @{ Name="wb_bars_gray16";       Pers="";           Img="";       Args=@("--mode","Gray16","--dpi","150","--region","0,0,3,2"); Check="golden" }
    @{ Name="wb_bars_color48";      Pers="";           Img="";       Args=@("--mode","Color48","--dpi","150","--region","0,0,3,2"); Check="golden" }
    @{ Name="wb_checker_bw1";       Pers="";           Img="checker"; Args=@("--mode","BlackWhite1","--dpi","150","--region","0,0,3,2"); Check="golden" }
    @{ Name="odd_checker_color24";  Pers="oddwidth";   Img="checker"; Args=@("--dpi","150","--region","0,0,3,2");              Check="golden" }
    @{ Name="bottomup_bars";        Pers="bottomup";   Img="";       Args=@("--dpi","150","--region","0,0,3,2");              Check="golden" }
    @{ Name="topdown_bars";         Pers="topdown";    Img="";       Args=@("--dpi","150","--region","0,0,3,2");              Check="golden" }
    @{ Name="bw1_forced";           Pers="bw1";        Img="";       Args=@("--dpi","150","--region","0,0,3,2");              Check="golden" }
    @{ Name="x64_bars_color24";     Pers="";           Img="";       Args=@("--dpi","150","--region","0,0,3,2");              Check="golden"; Dsm="x64" }
    @{ Name="duplex_reversed";      Pers="duplex";     Img="";       Args=@("--source","FeederDuplex","--pages","2","--dpi","150","--region","0,0,4,2"); Check="golden2" }
    @{ Name="setlies_dpi";          Pers="setlies";    Img="";       Args=@("--dpi","300","--region","0,0,3,2");              Check="text:450x300 3ch 8bpc @ 150dpi" }
    @{ Name="refusesui";            Pers="refusesui";  Img="";       Args=@("--dpi","150","--region","0,0,3,2");              Check="fail:TwainEnableFailed" }
    @{ Name="crash7_isolation";     Pers="crash7";     Img="";       Args=@("--dpi","150","--region","0,0,3,2");              Check="fail:HostCrashed" }
    @{ Name="busy_retry";           Pers="busy";       Img="";       Args=@("--dpi","150","--region","0,0,3,2");              Check="text:450x300 3ch 8bpc @ 150dpi" }
)
if ($WithHang) {
    $cases += @{ Name="hang_watchdog"; Pers="hang"; Img=""; Args=@("--dpi","150","--region","0,0,1,1"); Check="fail:HostTimeout" }
}

# ---------------------------------------------------------------- runner
$env:NEXTSCAN_TWAIN_DSM = $simX86
$failed = 0

Write-Host "TWAIN simulator golden harness" -ForegroundColor Cyan
Write-Host "  references: $refDir"
Write-Host "  output:     $outDir"
Write-Host ""

foreach ($c in $cases) {
    $name = $c.Name
    if ($c.Pers) { $env:NEXTSCAN_SIM_PERSONALITY = $c.Pers } else { Remove-Item Env:NEXTSCAN_SIM_PERSONALITY -ErrorAction SilentlyContinue }
    if ($c.Img)  { $env:NEXTSCAN_SIM_IMAGE = $c.Img }       else { Remove-Item Env:NEXTSCAN_SIM_IMAGE -ErrorAction SilentlyContinue }
    if ($c.Dsm -eq "x64") { $env:NEXTSCAN_TWAIN_DSM = $simX64 } else { $env:NEXTSCAN_TWAIN_DSM = $simX86 }

    $out1 = Join-Path $outDir "$name.png"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $output = & $nsprobe scan "Simulator" @($c.Args) --out $out1 2>&1 | Out-String
    $secs = $sw.Elapsed.TotalSeconds

    Remove-Item Env:NEXTSCAN_SIM_PERSONALITY -ErrorAction SilentlyContinue
    Remove-Item Env:NEXTSCAN_SIM_IMAGE -ErrorAction SilentlyContinue

    $err = $null
    if ($c.Check -eq "golden" -or $c.Check -eq "golden2") {
        $ref1 = Join-Path $refDir "$name.png"
        if ($Generate) {
            Copy-Item $out1 $ref1 -Force
            if ($c.Check -eq "golden2") { Copy-Item (Join-Path $outDir "$name`_002.png") (Join-Path $refDir "$name`_002.png") -Force }
        }
        else {
            if (-not (Test-Path $ref1)) { $err = "reference missing: tests\golden\$name.png (run -Generate once and commit it)" }
            else { $err = Compare-BitmapPixels $out1 $ref1 }
            if (-not $err -and $c.Check -eq "golden2") {
                $out2 = Join-Path $outDir "$name`_002.png"
                $ref2 = Join-Path $refDir "$name`_002.png"
                if (-not (Test-Path $out2)) { $err = "page 2 missing" }
                elseif (-not (Test-Path $ref2)) { $err = "reference for page 2 missing" }
                else {
                    $err = Compare-BitmapPixels $out2 $ref2
                    if (-not $err) { $err = Test-Rot180 $out1 $out2; if ($err) { $err = "rot180: $err" } }
                }
            }
        }
    }
    elseif ($c.Check.StartsWith("text:")) {
        $want = $c.Check.Substring(5)
        if ($output -notmatch [regex]::Escape($want)) { $err = "output did not contain '$want'" }
    }
    elseif ($c.Check.StartsWith("fail:")) {
        $want = $c.Check.Substring(5)
        if ($output -notmatch [regex]::Escape($want)) { $err = "expected failure '$want', output was:`n$output" }
    }

    if ($err) {
        $failed++
        Write-Host ("  FAIL {0,-22} {1}" -f $name, $err) -ForegroundColor Red
    }
    else {
        Write-Host ("  ok   {0,-22} {1,5:N1}s" -f $name, $secs) -ForegroundColor Green
    }
}

Write-Host ""
if ($Generate) {
    Write-Host "References generated in $refDir - verify and commit them." -ForegroundColor Yellow
    if ($failed -gt 0) { exit 1 } else { exit 0 }
}
if ($failed -gt 0) {
    Write-Host "$failed case(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "All cases passed." -ForegroundColor Green
exit 0
