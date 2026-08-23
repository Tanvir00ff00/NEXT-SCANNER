# =============================================================================
# NextScan Studio - build script
# Plan ref: MASTER_PLAN section 19.5.
#
#   .\build.ps1              build everything
#   .\build.ps1 -Clean       wipe bin first
#   .\build.ps1 -Test        build, then run a device probe
#
# Deliberately uses csc.exe directly rather than MSBuild: there is no .NET SDK on
# the target machines, and this keeps the toolchain requirement to "Windows +
# .NET Framework 4.x", which every Windows 10/11 install already satisfies.
# =============================================================================
param(
    [switch]$Clean,
    [switch]$Test,
    [switch]$NoWait
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root "src"
$bin  = Join-Path $root "bin"

function Find-Csc {
    # Roslyn first: it supports modern C#. The in-box .NET Framework compiler is
    # capped at C# 5 and is only a fallback.
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\*\MSBuild\Current\Bin\Roslyn\csc.exe"
    )
    foreach ($c in $candidates) {
        $hit = Get-Item $c -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $fallback = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (Test-Path $fallback) {
        Write-Host "  ! Roslyn not found; falling back to the C# 5 compiler" -ForegroundColor Yellow
        return $fallback
    }
    throw "No C# compiler found. Install Visual Studio Build Tools or the .NET Framework 4.x developer pack."
}

$csc = Find-Csc
$fw  = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$refs = @(
    "-nostdlib+",
    "-r:$fw\mscorlib.dll",
    "-r:$fw\System.dll",
    "-r:$fw\System.Core.dll",
    "-r:$fw\System.Drawing.dll",
    "-r:$fw\System.Windows.Forms.dll",
    "-r:$fw\System.Xml.dll",
    "-r:$fw\System.Management.dll"
)

Write-Host "NextScan Studio build" -ForegroundColor Cyan
Write-Host "  compiler: $csc"

if ($Clean -and (Test-Path $bin)) {
    Write-Host "  cleaning bin\" -ForegroundColor DarkGray
    Remove-Item "$bin\*" -Force -Recurse -ErrorAction SilentlyContinue
}
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

# Kill anything holding the outputs open, or the link step fails with a lock.
foreach ($p in @("NextScan.Host32","NextScan.Host64","nsprobe","NextScanner")) {
    Get-Process $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 200

function Build-Target {
    param([string]$Name, [string]$Out, [string]$Platform, [string]$Kind, [string[]]$Sources, [string]$EntryPoint)

    $files = @()
    foreach ($s in $Sources) {
        $matched = Get-ChildItem (Join-Path $src $s) -ErrorAction SilentlyContinue
        if (-not $matched) { throw "no sources matched $s" }
        $files += $matched.FullName
    }

    $args = @("-nologo", "-target:$Kind", "-platform:$Platform", "-out:$Out",
              "-unsafe", "-langversion:7.3", "-optimize+", "-warn:3") + $refs
    if ($EntryPoint) { $args += "-main:$EntryPoint" }
    $args += $files

    Write-Host ("  building {0,-22} ({1}, {2})" -f $Name, $Platform, $Kind) -NoNewline
    $out = & $csc $args 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED" -ForegroundColor Red
        $out | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        throw "build failed: $Name"
    }
    Write-Host "  ok" -ForegroundColor Green
}

$engine = @("Core\*.cs", "Twain\*.cs", "Wia\*.cs", "Net\*.cs")
$host_  = $engine + @("Host\*.cs")

# The two host processes are the same code compiled for both bitnesses - that is
# the entire trick that makes 32-bit-only scanner drivers reachable (plan 3.1).
Build-Target -Name "NextScan.Host64" -Out "$bin\NextScan.Host64.exe" -Platform "x64" -Kind "exe" -Sources $host_
Build-Target -Name "NextScan.Host32" -Out "$bin\NextScan.Host32.exe" -Platform "x86" -Kind "exe" -Sources $host_
Build-Target -Name "nsprobe"         -Out "$bin\nsprobe.exe"         -Platform "anycpu" -Kind "exe" `
             -Sources ($engine + @("Tools\NsProbe.cs")) -EntryPoint "NextScan.Tools.NsProbe"

# Imaging tests (plan 18.1): detection on synthetic previews + curve LUTs.
Build-Target -Name "nsimgtest"       -Out "$bin\nsimgtest.exe"       -Platform "anycpu" -Kind "exe" `
             -Sources ($engine + @("Tools\NsImgTest.cs")) -EntryPoint "NextScan.Tools.NsImgTest"

# Library form, so the existing Photoshop bridge (scanhelper.exe) can drop NAPS2
# and drive the native engine instead.
Build-Target -Name "NextScan.Engine" -Out "$bin\NextScan.Engine.dll" -Platform "anycpu" -Kind "library" -Sources $engine

Write-Host ""
Write-Host "Output in $bin" -ForegroundColor Green
Get-ChildItem $bin -Filter *.exe | ForEach-Object {
    Write-Host ("  {0,-26} {1,8:N0} bytes" -f $_.Name, $_.Length)
}

if ($Test) {
    Write-Host ""
    Write-Host "Probing for scanners..." -ForegroundColor Cyan
    & "$bin\nsprobe.exe" list
}

if (-not $NoWait -and $Host.Name -eq "ConsoleHost") {
    Write-Host ""
    Read-Host "Press Enter to close"
}
