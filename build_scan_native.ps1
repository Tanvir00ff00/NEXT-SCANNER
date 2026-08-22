# =============================================================================
# Builds scanhelper.exe against the native NextScan engine.
#
# Differs from build_scan.ps1 in three ways:
#   1. compiles NextScanBridge.cs alongside scanhelper.cs
#   2. references NextScan\bin\NextScan.Engine.dll
#   3. uses csc.exe directly instead of Add-Type, which means /unsafe is available
#      (Add-Type does not pass it - see the PixBuf comment in scanhelper.cs)
#
# build_scan.ps1 is left untouched, so the previous NAPS2-only build is still
# one command away if this one misbehaves.
# =============================================================================
param([switch]$NoWait, [switch]$SkipEngine)

$ErrorActionPreference = "Stop"
$root   = "C:\PS_Fix"
$src    = Join-Path $root "scanhelper.cs"
$bridge = Join-Path $root "NextScanBridge.cs"
$exe    = Join-Path $root "scanhelper.exe"
$engine = Join-Path $root "NextScan\bin\NextScan.Engine.dll"

function Find-Csc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe")
}

Write-Host "Building scanner bridge (native engine)..." -ForegroundColor Cyan

foreach ($f in @($src, $bridge)) {
    if (-not (Test-Path $f)) { Write-Host "missing: $f" -ForegroundColor Red; if (-not $NoWait) { Read-Host }; exit 1 }
}

# The engine must exist before the helper can reference it.
if (-not $SkipEngine) {
    Write-Host "  building the engine first..." -ForegroundColor DarkGray
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "NextScan\build.ps1") -NoWait | Out-Null
}
if (-not (Test-Path $engine)) {
    Write-Host "engine not found: $engine" -ForegroundColor Red
    Write-Host "run NextScan\build.ps1 first" -ForegroundColor Yellow
    if (-not $NoWait) { Read-Host }
    exit 1
}

Get-Process scanhelper -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process NextScan.Host32, NextScan.Host64 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
if (Test-Path $exe) { Remove-Item $exe -Force }

$csc = Find-Csc
$fw  = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"

$args = @(
    "-nologo", "-target:winexe", "-platform:anycpu", "-out:$exe",
    "-unsafe", "-langversion:7.3", "-optimize+",
    "-nostdlib+",
    "-r:$fw\mscorlib.dll", "-r:$fw\System.dll", "-r:$fw\System.Core.dll",
    "-r:$fw\System.Drawing.dll", "-r:$fw\System.Windows.Forms.dll", "-r:$fw\System.Xml.dll",
    "-r:$engine",
    $src, $bridge
)

& $csc $args
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED" -ForegroundColor Red
    if (-not $NoWait) { Read-Host "Press Enter to close" }
    exit 1
}

# scanhelper.exe resolves NextScan.Engine.dll from its own folder, so keep a copy
# next to it rather than relying on the NextScan\bin path at runtime.
Copy-Item $engine (Join-Path $root "NextScan.Engine.dll") -Force

Write-Host "OK -> $exe" -ForegroundColor Green
Write-Host "     engine: $engine" -ForegroundColor DarkGray
if (-not $NoWait) { Read-Host "Press Enter to close" }
