# =============================================================================
# Builds the Photoshop acquire module, NextScanner.8ba
#
# Separate from build.ps1 because it needs a C++ toolchain and the Adobe SDK,
# neither of which the rest of the build requires. The engine and the
# application must keep building on a machine that has neither.
#
# The Adobe SDK is NOT in this repository and must not be: its licence allows
# us to ship compiled output that includes its sample code, but not to
# redistribute the SDK itself. Clone it beside the repo and point -SdkRoot at
# it, or set NEXTSCAN_PS_SDK:
#
#   git clone --depth 1 https://github.com/AdobeDocs/photoshop-cpp-sdk.git
# =============================================================================
param(
    [string]$SdkRoot = $env:NEXTSCAN_PS_SDK,
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root "src\Photoshop"
$out  = Join-Path $root "bin"

if (-not $SdkRoot) { $SdkRoot = Join-Path (Split-Path -Parent $root) "sdk\photoshop-cpp-sdk" }
if (-not (Test-Path $SdkRoot)) {
    throw "Photoshop SDK not found at $SdkRoot. Clone AdobeDocs/photoshop-cpp-sdk there, or pass -SdkRoot."
}

$api = Join-Path $SdkRoot "pluginsdk\photoshopapi"
$inc = @(
    (Join-Path $api "photoshop"),
    (Join-Path $api "pica_sp"),
    (Join-Path $SdkRoot "pluginsdk\samplecode\common\includes")
)
foreach ($i in $inc) { if (-not (Test-Path $i)) { throw "SDK include folder missing: $i" } }

# ---- locate the toolchain --------------------------------------------------
$vsRoot = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools"
$msvc = Get-ChildItem (Join-Path $vsRoot "VC\Tools\MSVC") -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
if (-not $msvc) { throw "MSVC toolchain not found under $vsRoot" }

$cl   = Join-Path $msvc.FullName "bin\Hostx64\x64\cl.exe"
$link = Join-Path $msvc.FullName "bin\Hostx64\x64\link.exe"
foreach ($t in @($cl, $link)) { if (-not (Test-Path $t)) { throw "missing tool: $t" } }

# The Windows SDK supplies rc.exe plus the CRT and user32 import libraries.
$kitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10"
$kit = Get-ChildItem (Join-Path $kitRoot "Include") -Directory -ErrorAction SilentlyContinue |
       Where-Object { Test-Path (Join-Path $_.FullName "um\windows.h") } |
       Sort-Object Name -Descending | Select-Object -First 1
if (-not $kit) { throw "Windows SDK not found under $kitRoot" }

$rc = Join-Path $kitRoot "bin\$($kit.Name)\x64\rc.exe"
if (-not (Test-Path $rc)) { throw "rc.exe not found at $rc" }

$sysInc = @(
    (Join-Path $msvc.FullName "include"),
    (Join-Path $kit.FullName "ucrt"),
    (Join-Path $kit.FullName "um"),
    (Join-Path $kit.FullName "shared")
)
$libs = @(
    (Join-Path $msvc.FullName "lib\x64"),
    (Join-Path $kitRoot "Lib\$($kit.Name)\ucrt\x64"),
    (Join-Path $kitRoot "Lib\$($kit.Name)\um\x64")
)

Write-Host "  toolchain : MSVC $($msvc.Name), Windows SDK $($kit.Name)"
Write-Host "  sdk       : $SdkRoot"

$work = Join-Path $env:TEMP "nextscan_8ba"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

# ---- resources -------------------------------------------------------------
# rc.exe resolves the JSON path relative to the .rc file, so it is compiled
# where it lives rather than copied into the work folder.
$res = Join-Path $work "NextScannerAcquire.res"
$rcArgs = @("/nologo", "/fo", $res)
foreach ($i in $sysInc) { $rcArgs += @("/i", $i) }
$rcArgs += (Join-Path $src "NextScannerAcquire.rc")
& $rc $rcArgs
if ($LASTEXITCODE -ne 0) { throw "rc.exe failed" }

# ---- compile ---------------------------------------------------------------
$obj = Join-Path $work "NextScannerAcquire.obj"
$clArgs = @("/nologo", "/c", "/EHsc", "/MT", "/O2", "/W3", "/D", "WIN32", "/D", "NDEBUG",
            "/D", "_WINDOWS", "/D", "_USRDLL", "/Fo:$obj")
foreach ($i in ($inc + $sysInc)) { $clArgs += "/I$i" }
$clArgs += (Join-Path $src "NextScannerAcquire.cpp")
& $cl $clArgs
if ($LASTEXITCODE -ne 0) { throw "cl.exe failed" }

# ---- link ------------------------------------------------------------------
# The extension is what makes Photoshop look at it at all: it scans its
# Plug-ins folder for .8ba (acquire) and friends, not for .dll.
$target = Join-Path $out "NextScanner.8ba"
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }

$linkArgs = @("/nologo", "/DLL", "/MACHINE:X64", "/OUT:$target",
              "/EXPORT:PluginMain", $obj, $res,
              "user32.lib", "kernel32.lib", "advapi32.lib")
foreach ($l in $libs) { $linkArgs += "/LIBPATH:$l" }
& $link $linkArgs
if ($LASTEXITCODE -ne 0) { throw "link.exe failed" }

Write-Host ("  built     : {0}  ({1:N0} bytes)" -f $target, (Get-Item $target).Length)

# ---- install ---------------------------------------------------------------
if ($Install) {
    $installed = 0
    foreach ($rootDir in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        $adobe = Join-Path $rootDir "Adobe"
        if (-not (Test-Path $adobe)) { continue }
        foreach ($ps in Get-ChildItem $adobe -Directory -Filter "Adobe Photoshop*") {
            $plugins = Join-Path $ps.FullName "Plug-ins"
            if (-not (Test-Path $plugins)) { continue }
            try {
                Copy-Item $target (Join-Path $plugins "NextScanner.8ba") -Force
                Write-Host "  installed : $plugins"
                $installed++
            } catch {
                Write-Host "  NEEDS ADMIN: $plugins  ($($_.Exception.Message))"
            }
        }
    }
    if ($installed -eq 0) { Write-Host "  nothing installed - run elevated, or copy the .8ba by hand" }
}
