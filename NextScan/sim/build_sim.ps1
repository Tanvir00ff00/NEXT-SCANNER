# =============================================================================
# NextScan Studio - TWAIN simulator build (ADR-0002)
#
#   .\sim\build_sim.ps1           build x86 + x64
#
# Produces bin\sim\x86\TWAINDSM.DLL and bin\sim\x64\TWAINDSM.DLL with plain
# cl.exe - no MSBuild, matching the toolchain philosophy of ..\build.ps1.
# Uses the Hostx64 cross-compiler for the x86 target so both bitnesses build
# on any machine with the C++ workload installed.
# =============================================================================
param([switch]$NoWait)

$ErrorActionPreference = "Stop"
$root  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$sim   = Join-Path $root "sim"
$bin   = Join-Path $root "bin\sim"

# ---- locate the newest MSVC toolset ----
$msvcRoots = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\VC\Tools\MSVC",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\*\VC\Tools\MSVC"
)
$toolset = $null
foreach ($r in $msvcRoots) {
    $hit = Get-ChildItem $r -Directory -ErrorAction SilentlyContinue |
           Sort-Object Name -Descending | Select-Object -First 1
    if ($hit) { $toolset = $hit.FullName; break }
}
if (-not $toolset) { throw "No MSVC toolset found. Install VS Build Tools with the C++ workload." }

# ---- locate the newest Windows 10 SDK ----
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10"
$sdk = Get-ChildItem (Join-Path $sdkRoot "Include") -Directory -ErrorAction SilentlyContinue |
       Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw "No Windows SDK found under $sdkRoot" }
$sdkVer = $sdk.Name

Write-Host "TWAIN simulator build" -ForegroundColor Cyan
Write-Host "  toolset: $toolset"
Write-Host "  sdk:     $sdkVer"

function Build-Arch {
    param([string]$Arch, [string]$ClArch, [string]$OutDir)

    $cl = Join-Path $toolset "bin\Hostx64\$ClArch\cl.exe"
    if (-not (Test-Path $cl)) { throw "cross compiler missing: $cl" }

    $env:INCLUDE = (Join-Path $toolset "include") + ";" +
                   (Join-Path $sdkRoot "Include\$sdkVer\ucrt") + ";" +
                   (Join-Path $sdkRoot "Include\$sdkVer\um") + ";" +
                   (Join-Path $sdkRoot "Include\$sdkVer\shared") + ";" +
                   (Join-Path $sdkRoot "Include\$sdkVer\winrt")
    $env:LIB = (Join-Path $toolset "lib\$Arch") + ";" +
               (Join-Path $sdkRoot "Lib\$sdkVer\ucrt\$Arch") + ";" +
               (Join-Path $sdkRoot "Lib\$sdkVer\um\$Arch")

    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
    $dll = Join-Path $OutDir "TWAINDSM.DLL"

    Write-Host ("  building {0,-4} TWAINDSM.DLL" -f $Arch) -NoNewline
    Push-Location $sim
    try {
        # /DEF must ride inside /link - passed bare on the cl command line it is
        # silently ignored and the DLL ends up with an empty export table, which
        # surfaces far away as "DSM_Entry not exported" in the host's probe log.
        & $cl /nologo /LD /O2 /W3 /MT /GS `
             TwainSim.cpp `
             /Fe:$dll `
             /link /DEF:TwainSim.def kernel32.lib user32.lib
        if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED" -ForegroundColor Red; throw "simulator build failed: $Arch" }
    }
    finally { Pop-Location }
    Write-Host "  ok" -ForegroundColor Green

    # cl leaves .obj/.exp/.lib next to the sources; keep sim\ clean
    Remove-Item (Join-Path $sim "*.obj"), (Join-Path $sim "*.exp"), (Join-Path $sim "*.lib") -Force -ErrorAction SilentlyContinue
}

Build-Arch -Arch "x86" -ClArch "x86"   -OutDir (Join-Path $bin "x86")
Build-Arch -Arch "x64" -ClArch "x64"   -OutDir (Join-Path $bin "x64")

Write-Host ""
Write-Host "Output in $bin" -ForegroundColor Green
Get-ChildItem $bin -Recurse -Filter *.dll | ForEach-Object {
    Write-Host ("  {0,-46} {1,8:N0} bytes" -f $_.FullName.Replace($root, ""), $_.Length)
}

if (-not $NoWait -and $Host.Name -eq "ConsoleHost") {
    Write-Host ""
    Read-Host "Press Enter to close"
}
