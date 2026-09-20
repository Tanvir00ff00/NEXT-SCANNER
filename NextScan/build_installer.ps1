# =============================================================================
# Builds NextScanSetup.exe: one file, its own window, the payload inside it.
#
# There is no NSIS here and no installer framework. The window is built from the
# application's own Theme, icon set, controls and Animator, so the first screen
# anyone sees of this product already looks like the product -- a stock
# installer chrome would be the one screen that looks like something else, and
# it is the screen everybody sees.
#
# Most of this file is checks rather than commands, and that is deliberate. The
# models and onnxruntime.dll are not in the repository, nothing in the ordinary
# build fails without them, and an installer built without them does not fail
# either: it produces a working application that detects worse on every machine
# it reaches, with nothing on screen to say why. So the payload is verified by
# name and by size before anything is packaged.
# =============================================================================
param(
    # Empty by default: the version is read out of src\Core\AppInfo.cs, so the
    # installer, the executable and the About panel cannot disagree. Pass one
    # only to build something other than what the source says it is.
    [string]$Version = "",
    [switch]$SkipBuild,
    [switch]$NoConnector
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$bin = Join-Path $root "bin"
$models = Join-Path $root "models"
$dist = Join-Path $root "dist"
$work = Join-Path $env:TEMP "nextscan_setup"

if (-not $Version) {
    $versionFile = Join-Path $root "src\Core\AppInfo.cs"
    $m = [regex]::Match((Get-Content $versionFile -Raw), 'Version\s*=\s*"([0-9]+(?:\.[0-9]+)*)"')
    if (-not $m.Success) { throw "no Version constant in $versionFile" }
    $Version = $m.Groups[1].Value
}
# Written as 1.0 in source; the file version resource wants three parts.
$Version = ($Version.Split('.') + @('0','0','0'))[0..2] -join '.'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$Version'" }

Write-Host "NextScan Studio installer $Version" -ForegroundColor Cyan

# ---- toolchain --------------------------------------------------------------
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe" `
                     -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $csc) { throw "Roslyn csc.exe not found under Visual Studio 2022 Build Tools." }
Write-Host "  compiler  : $($csc.FullName)"

# ---- build the application --------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "  building application..." -ForegroundColor DarkGray
    & (Join-Path $root "build.ps1") -NoWait | Out-Null
}

$connector = Join-Path $bin "NextScanner.8ba"
if (-not $NoConnector -and -not (Test-Path $connector)) {
    Write-Host "  no NextScanner.8ba in bin\; building it" -ForegroundColor DarkGray
    try { & (Join-Path $root "build_8ba.ps1") | Out-Null }
    catch { Write-Host "  connector build failed: $($_.Exception.Message)" -ForegroundColor Yellow }
}
if (-not $NoConnector -and -not (Test-Path $connector)) {
    Write-Host "  NextScanner.8ba missing; building WITHOUT the Photoshop connector" -ForegroundColor Yellow
    $NoConnector = $true
}

# ---- the payload, by name and by size ---------------------------------------
# Sizes are floors, not equalities: they catch a truncated download, an empty
# placeholder or a Git LFS pointer, without breaking every time something is
# legitimately rebuilt a few bytes larger.
$payload = @(
    @{ From = "$bin\NextScanner.exe";     To = "NextScanner.exe";     Least = 300KB; Why = "the application" }
    @{ From = "$bin\NextScan.Host32.exe"; To = "NextScan.Host32.exe"; Least = 200KB; Why = "reaches 32-bit only TWAIN drivers" }
    @{ From = "$bin\NextScan.Host64.exe"; To = "NextScan.Host64.exe"; Least = 200KB; Why = "reaches 64-bit drivers" }
    @{ From = "$bin\NextScan.Engine.dll"; To = "NextScan.Engine.dll"; Least = 200KB; Why = "the engine" }
    @{ From = "$bin\nsprobe.exe";         To = "nsprobe.exe";         Least = 200KB; Why = "scanner diagnostics" }
    @{ From = "$bin\onnxruntime.dll";     To = "onnxruntime.dll";     Least = 5MB;   Why = "without it the model layer is off" }
    @{ From = "$models\mobile_sam_image_encoder.onnx"; To = "models\mobile_sam_image_encoder.onnx"; Least = 20MB; Why = "segmentation encoder" }
    @{ From = "$models\sam_mask_decoder_multi.onnx";   To = "models\sam_mask_decoder_multi.onnx";   Least = 10MB; Why = "segmentation decoder" }
)
if (-not $NoConnector) {
    $payload += @{ From = $connector; To = "NextScanner.8ba"; Least = 50KB; Why = "the Photoshop connector" }
}

Write-Host ""
Write-Host "  payload" -ForegroundColor Cyan
$missing = @()
$total = 0
foreach ($item in $payload) {
    $name = Split-Path $item.From -Leaf
    if (-not (Test-Path $item.From)) {
        Write-Host ("    {0,-34} MISSING   {1}" -f $name, $item.Why) -ForegroundColor Red
        $missing += "$name ($($item.Why))"; continue
    }
    $size = (Get-Item $item.From).Length
    if ($size -lt $item.Least) {
        Write-Host ("    {0,-34} TOO SMALL {1:N0} bytes" -f $name, $size) -ForegroundColor Red
        $missing += "$name (only $size bytes)"; continue
    }
    $total += $size
    Write-Host ("    {0,-34} {1,12:N0} bytes" -f $name, $size) -ForegroundColor Green
}
if ($missing.Count -gt 0) {
    Write-Host ""
    throw ("Refusing to build an installer missing " + ($missing -join ", ") +
           ". These are gitignored and must be fetched separately; an installer without them " +
           "works and detects worse, with nothing on screen to say why.")
}
Write-Host ("    {0,-34} {1,12:N0} bytes total" -f "", $total)

# ---- stage and compress -----------------------------------------------------
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
$stage = Join-Path $work "payload"

foreach ($item in $payload) {
    $target = Join-Path $stage $item.To
    $parent = Split-Path $target -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item $item.From $target -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $work "NextScan.Payload.zip"
Write-Host ""
Write-Host "  compressing..." -ForegroundColor DarkGray
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("  compressed: {0:N1} MB from {1:N1} MB" -f ((Get-Item $zip).Length / 1MB), ($total / 1MB))

# ---- compile ----------------------------------------------------------------
# The window reuses the application's own UI files. They compile standalone --
# nothing in Theme, the icon set or the control library reaches back into the
# shell -- which is what makes this possible without duplicating the look.
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
$out = Join-Path $dist "NextScanSetup-$Version.exe"

$sources = @(
    "$src\Setup\SetupApp.cs",
    "$src\Setup\SetupWork.cs",
    "$src\Setup\SetupWindow.cs",
    "$src\App\StudioTheme.cs",
    "$src\App\StudioIcons.cs",
    "$src\App\StudioControls.cs"
)
foreach ($f in $sources) { if (-not (Test-Path $f)) { throw "missing source: $f" } }

$info = Join-Path $work "Info.cs"
@"
using System.Reflection;
[assembly: AssemblyTitle("NextScan Studio Setup")]
[assembly: AssemblyProduct("NextScan Studio")]
[assembly: AssemblyCompany("NextScan")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
"@ | Set-Content $info -Encoding UTF8

$args = @(
    "-nologo", "-target:winexe", "-platform:anycpu", "-out:$out",
    "-langversion:7.3", "-optimize+", "-warn:3", "-nostdlib+",
    "-r:$fw\mscorlib.dll", "-r:$fw\System.dll", "-r:$fw\System.Core.dll",
    "-r:$fw\System.Drawing.dll", "-r:$fw\System.Windows.Forms.dll",
    "-r:$fw\System.IO.Compression.dll", "-r:$fw\System.IO.Compression.FileSystem.dll",
    "-main:NextScan.Setup.SetupApp",
    "-win32icon:$src\App\NextScanner.ico",
    "-win32manifest:$src\Setup\Setup.manifest",
    "-resource:$zip,NextScan.Payload.zip"
) + $sources + @($info)

Write-Host "  compiling..." -ForegroundColor DarkGray
& $csc.FullName $args
if ($LASTEXITCODE -ne 0) { throw "csc failed" }

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

$built = Get-Item $out
Write-Host ""
Write-Host ("  built     : {0}" -f $built.FullName) -ForegroundColor Green
Write-Host ("  size      : {0:N1} MB" -f ($built.Length / 1MB))
if ($NoConnector) { Write-Host "  note      : built WITHOUT the Photoshop connector" -ForegroundColor Yellow }

# Unsigned installers make SmartScreen warn on first run, and that warning is
# what a buyer sees before anything else. Saying so here is cheaper than
# finding out from them.
Write-Host ""
Write-Host "  Not code signed. SmartScreen will warn until it is." -ForegroundColor Yellow
Write-Host "  signtool sign /fd SHA256 /a /tr <timestamp-url> /td SHA256 `"$out`"" -ForegroundColor DarkGray
