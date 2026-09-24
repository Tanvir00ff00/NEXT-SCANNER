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
    "-r:$fw\System.Management.dll",
    "-r:$fw\System.Web.Extensions.dll"
)

Write-Host "NextScan Studio build" -ForegroundColor Cyan
Write-Host "  compiler: $csc"

# A build must not terminate a live scan or discard unsaved session pages.
# Fail before replacing any outputs if this checkout is currently running.
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path) -eq $bin) } catch { $false }
}
if ($running) { throw "Close programs running from $bin before building: $($running.Name -join ', ')" }

if ($Clean -and (Test-Path $bin)) {
    Write-Host "  cleaning bin\" -ForegroundColor DarkGray
    $resolvedBin = [IO.Path]::GetFullPath($bin)
    if ($resolvedBin -ne [IO.Path]::GetFullPath((Join-Path $root 'bin'))) { throw "Unexpected build output path" }
    Get-ChildItem -LiteralPath $resolvedBin -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -Recurse }
}
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

function Build-Target {
    param([string]$Name, [string]$Out, [string]$Platform, [string]$Kind, [string[]]$Sources, [string]$EntryPoint,
          [string]$Icon, [string[]]$Extra, [string[]]$ExtraRefs)

    $files = @()
    foreach ($s in $Sources) {
        $matched = Get-ChildItem (Join-Path $src $s) -ErrorAction SilentlyContinue
        if (-not $matched) { throw "no sources matched $s" }
        $files += $matched.FullName
    }

    $args = @("-nologo", "-target:$Kind", "-platform:$Platform", "-out:$Out",
              "-unsafe", "-langversion:7.3", "-optimize+", "-warn:3") + $refs
    foreach ($r in $ExtraRefs) { $args += "-r:$r" }
    if ($EntryPoint) { $args += "-main:$EntryPoint" }
    if ($Icon -and (Test-Path $Icon)) { $args += "-win32icon:$Icon" }
    $args += $files
    if ($Extra) { $args += $Extra }

    Write-Host ("  building {0,-22} ({1}, {2})" -f $Name, $Platform, $Kind) -NoNewline
    $out = & $csc $args 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED" -ForegroundColor Red
        $out | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        throw "build failed: $Name"
    }
    Write-Host "  ok" -ForegroundColor Green
}

# The version lives in one file and is read back out of it, so the executable's
# file properties cannot drift from what the About panel says.
$versionFile = Join-Path $src "Core\AppInfo.cs"
if (-not (Test-Path $versionFile)) { throw "missing $versionFile" }
$m = [regex]::Match((Get-Content $versionFile -Raw), 'Version\s*=\s*"([0-9]+(?:\.[0-9]+)*)"')
if (-not $m.Success) { throw "no Version constant in $versionFile" }
$appVersion = $m.Groups[1].Value
$fileVersion = ($appVersion.Split('.') + @('0','0','0','0'))[0..3] -join '.'
Write-Host "  version : $appVersion"

$stamp = Join-Path $env:TEMP "nextscan_version.cs"
@"
using System.Reflection;
[assembly: AssemblyTitle("NextScan Studio")]
[assembly: AssemblyProduct("NextScan Studio")]
[assembly: AssemblyCompany("NextScan")]
[assembly: AssemblyVersion("$fileVersion")]
[assembly: AssemblyFileVersion("$fileVersion")]
"@ | Set-Content $stamp -Encoding UTF8

# ---------------------------------------------------------------------------
# The AI layer
#
# The only part of this repository with dependencies, so it is the only part
# built by the .NET SDK rather than by csc directly. Its output goes to bin\ai
# rather than bin: thirty-one files from three provider SDKs sitting beside the
# application would make it impossible to see at a glance what we actually ship.
# ---------------------------------------------------------------------------
$aiProject = Join-Path $src "Ai\NextScan.Ai.csproj"
$aiOut = Join-Path $bin "ai"
$aiDll = Join-Path $aiOut "NextScan.Ai.dll"

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dotnet) {
    throw ("The .NET SDK is needed to build the AI layer, and the application no longer builds " +
           "without it. https://dotnet.microsoft.com/download")
}

Write-Host "  building NextScan.Ai" -NoNewline
$aiLog = & $dotnet build $aiProject -c Release -v q --nologo -p:OutputPath="$aiOut\" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAILED" -ForegroundColor Red
    $aiLog | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    throw "build failed: NextScan.Ai"
}
Write-Host "  ok" -ForegroundColor Green

# Binding redirects only count in the config of the program that runs. MSBuild
# writes them next to the library, where the CLR never looks, so they are
# carried over to each executable's own config below. The probing path is what
# lets the CLR find the SDKs in bin\ai at all.
$fromAi = Join-Path $aiOut "NextScan.Ai.dll.config"
if (Test-Path $fromAi) {
    [xml]$aiConfig = Get-Content $fromAi
    $ns = "urn:schemas-microsoft-com:asm.v1"

    # The probing element gets an assemblyBinding of its own. MSBuild writes one
    # per redirect -- fifteen of them -- so there is no single element to hang
    # this off, and asking PowerShell for "the" assemblyBinding walks all
    # fifteen and leaves the node in whichever came last. That happens to work
    # and is not a thing to rely on.
    $holder = $aiConfig.CreateElement("assemblyBinding", $ns)
    $probing = $aiConfig.CreateElement("probing", $ns)
    $probing.SetAttribute("privatePath", "ai;docs")
    $holder.AppendChild($probing) | Out-Null

    $runtime = $aiConfig.configuration.runtime
    $runtime.InsertBefore($holder, $runtime.FirstChild) | Out-Null
}

# ---------------------------------------------------------------------------
# The document layer (docs/DOCUMENT_WORKSPACE.md)
#
# WebView2's wrapper comes from NuGet, so this is built by the .NET SDK like the
# AI layer, into bin\docs. Beside it goes the editor engine, which is not in the
# repository: tools\get_onlyoffice.ps1 fetches and verifies it into
# vendor\onlyoffice, and it is mirrored from there.
# ---------------------------------------------------------------------------
$docsProject = Join-Path $src "Docs\NextScan.Docs.csproj"
$docsOut = Join-Path $bin "docs"
$docsDll = Join-Path $docsOut "NextScan.Docs.dll"

Write-Host "  building NextScan.Docs" -NoNewline
$docsLog = & $dotnet build $docsProject -c Release -v q --nologo -p:OutputPath="$docsOut\" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAILED" -ForegroundColor Red
    $docsLog | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    throw "build failed: NextScan.Docs"
}
Write-Host "  ok" -ForegroundColor Green

$vendor = Join-Path $root "vendor\onlyoffice"
if (Test-Path (Join-Path $vendor "editors\sdkjs")) {
    # Mirrored, not copied: robocopy only touches what changed, which after the
    # first build is nothing, and 500 MB is not copied again on every build.
    foreach ($part in "editors", "converter") {
        & robocopy (Join-Path $vendor $part) (Join-Path $docsOut $part) /MIR /R:1 /W:1 /NJH /NJS /NFL /NDL /NP /XF nsfonts.exe msvcp140.dll vcruntime140.dll vcruntime140_1.dll | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "could not copy the editor engine ($part)" }
    }
    foreach ($f in "LICENSE.txt", "3rd-Party.txt") {
        if (Test-Path (Join-Path $vendor $f)) { Copy-Item (Join-Path $vendor $f) (Join-Path $docsOut ("ONLYOFFICE-" + $f)) -Force }
    }
    Write-Host "  editor engine              mirrored" -ForegroundColor Green

    # x2t, graphics.dll and nsfonts link the Visual C++ runtime dynamically,
    # and ONLYOFFICE's zip relies on its own installer to put that in place.
    # A shop PC without Office may not have it, so the three files go beside
    # the converter -- the app-local deployment Microsoft's redistribution
    # terms allow for exactly these.
    foreach ($crt in "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll") {
        $from = Join-Path $env:WINDIR "System32\$crt"
        $to = Join-Path $docsOut "converter\$crt"
        if ((Test-Path $from) -and -not (Test-Path $to)) { Copy-Item $from $to }
    }

    # nsfonts drives graphics.dll's font worker, which is C++, so it is C++
    # too (src\Docs\native). Built with MSVC when there is one; without it the
    # engine still works, but with no fonts but the editor's defaults.
    $vcvars = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
    if (-not (Test-Path $vcvars)) {
        $vcvars = Get-Item "${env:ProgramFiles}\Microsoft Visual Studio\2022\*\VC\Auxiliary\Build\vcvars64.bat" -ErrorAction SilentlyContinue |
                  Select-Object -First 1 -ExpandProperty FullName
    }
    $nsfonts = Join-Path $docsOut "converter\nsfonts.exe"
    $nsSource = Join-Path $src "Docs\native\nsfonts.cpp"
    $stale = -not (Test-Path $nsfonts) -or ((Get-Item $nsSource).LastWriteTime -gt (Get-Item $nsfonts).LastWriteTime)
    if ($vcvars -and $stale) {
        Write-Host "  building nsfonts           (x64, native)" -NoNewline
        $work = Join-Path $env:TEMP "nextscan_nsfonts"
        New-Item -ItemType Directory -Force $work | Out-Null
        @"
LIBRARY graphics.dll
EXPORTS
??0CApplicationFontsWorker@@QEAA@XZ
??1CApplicationFontsWorker@@QEAA@XZ
?Check@CApplicationFontsWorker@@QEAAPEAVIApplicationFonts@NSFonts@@XZ
?CheckThumbnails@CApplicationFontsWorker@@QEAAXXZ
"@ | Set-Content (Join-Path $work "graphics.def") -Encoding ASCII
        $cmd = "call `"$vcvars`" >nul 2>&1 && cd /d `"$work`" && lib /nologo /def:graphics.def /machine:x64 /out:graphics.lib >nul && " +
               "cl /nologo /EHsc /MD /O2 /W3 /wd4251 /std:c++17 `"$nsSource`" /Fe:nsfonts.exe /link graphics.lib >nul 2>&1"
        cmd /c $cmd
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $work "nsfonts.exe"))) {
            Write-Host "  FAILED" -ForegroundColor Red
            throw "build failed: nsfonts"
        }
        Copy-Item (Join-Path $work "nsfonts.exe") $nsfonts -Force
        Write-Host "  ok" -ForegroundColor Green
    }
    elseif (-not $vcvars -and -not (Test-Path $nsfonts)) {
        Write-Host "  ! no C++ compiler: nsfonts not built, documents will use the editor's own fonts only" -ForegroundColor Yellow
    }
}
else {
    Write-Host "  ! editor engine missing: run tools\get_onlyoffice.ps1 (the Assist workspace will say so)" -ForegroundColor Yellow
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

# Cancellation tests (plan 13.4): graceful stop, kill fallback, recovery.
Build-Target -Name "nscanceltest"   -Out "$bin\nscanceltest.exe"   -Platform "anycpu" -Kind "exe" `
             -Sources ($engine + @("Tools\NsCancelTest.cs")) -EntryPoint "NextScan.Tools.NsCancelTest"

# Auto crop tests (plan 8.1): runs the engine's own self-test, then independent
# cases written against the specification rather than beside the code.
Build-Target -Name "nscroptest"     -Out "$bin\nscroptest.exe"     -Platform "anycpu" -Kind "exe" `
             -Sources ($engine + @("Tools\NsCropTest.cs", "Tools\RegionSplitter.cs")) -EntryPoint "NextScan.Tools.NsCropTest"

# Export and batch tests (plan 11.1, 12): naming, PDF and TIFF containers,
# blank detection, document separation.
Build-Target -Name "nsexporttest"   -Out "$bin\nsexporttest.exe"   -Platform "anycpu" -Kind "exe" `
             -Sources ($engine + @("Tools\NsExportTest.cs")) -EntryPoint "NextScan.Tools.NsExportTest"

# ONNX interop and segmentation (plan: model-assisted detection). x64 because
# the native runtime it binds to is, and the check it runs is worth nothing if
# it silently loads a different architecture.
Build-Target -Name "nsonnxtest"     -Out "$bin\nsonnxtest.exe"     -Platform "x64" -Kind "exe" `
             -Sources ($engine + @("Tools\NsOnnxTest.cs")) -EntryPoint "NextScan.Tools.NsOnnxTest"

Build-Target -Name "NextScan.Engine" -Out "$bin\NextScan.Engine.dll" -Platform "anycpu" -Kind "library" -Sources $engine

# Dedicated standalone studio application (Master Plan section 13)
$app_ = $engine + @("App\*.cs")
$appIcon = Join-Path $src "App\NextScanner.ico"
# The assistant's reading scripts (StudioDocTools), compiled in rather than
# shipped beside the exe: what the model is shown of a document is part of the
# program, not a file anyone can change.
$appScripts = @(Get-ChildItem (Join-Path $src "App\scripts\*.js") | ForEach-Object {
    "-resource:$($_.FullName),NextScan.App.scripts.$($_.Name)" })
# One reference, not thirty-one: the shell only ever sees our own facade, and
# the provider SDKs behind it are loaded at run time from bin\ai.
$appRefs = @($aiDll, $docsDll)

Build-Target -Name "NextScanner"     -Out "$bin\NextScanner.exe"     -Platform "anycpu" -Kind "winexe" `
             -Sources $app_ -EntryPoint "NextScan.App.StudioApp" -Icon $appIcon -Extra (@($stamp) + $appScripts) `
             -ExtraRefs $appRefs

# The AI layer loads, and stays behind its boundary (docs/AI_LAYER.md). Built
# with the same single reference the shell gets: if this can drive all three
# providers without naming one of their SDKs, so can the application.
Build-Target -Name "nsaitest"        -Out "$bin\nsaitest.exe"        -Platform "anycpu" -Kind "exe" `
             -Sources ($engine + @("Tools\NsAiTest.cs")) -EntryPoint "NextScan.Tools.NsAiTest" `
             -ExtraRefs $appRefs

# Every executable that reaches the AI layer needs the redirects, not just the
# application: a test that passes because it was run from a different folder
# proves nothing about the thing we ship.
if ($aiConfig) {
    foreach ($exe in @("NextScanner.exe", "nsaitest.exe")) {
        $aiConfig.Save((Join-Path $bin ($exe + ".config")))
    }
}

# Deploy NextScan.Engine.dll to the parent directory for scanhelper compatibility
Copy-Item "$bin\NextScan.Engine.dll" (Join-Path (Split-Path -Parent $root) "NextScan.Engine.dll") -Force -ErrorAction SilentlyContinue

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
