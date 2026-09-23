# =============================================================================
# NextScan - fetches the document editor engine
# Plan ref: docs/DOCUMENT_WORKSPACE.md
#
#   .\tools\get_onlyoffice.ps1              download, verify, extract
#   .\tools\get_onlyoffice.ps1 -From <dir>  use packages already downloaded there
#
# The engine is ONLYOFFICE 9.4.0 (AGPL-3.0). It is about 500 MB, so it is not
# kept in the repository; this script takes it from ONLYOFFICE's own GitHub
# releases and checks each package against the SHA-256 GitHub publishes for it.
#
# Two packages, because neither has everything:
#   - The Document Server package has the editors built with the offline mode
#     NextScan runs them in (sdkjs, web-apps). The desktop build does not: its
#     Word editor expects the desktop application's native object.
#   - The desktop package has the Windows converter, x2t.exe, and the libraries
#     it needs, including graphics.dll, which nsfonts drives.
#
# Result, in vendor\onlyoffice:
#   editors\sdkjs, editors\web-apps     from the Document Server package
#   converter\                          from the desktop package, less its templates
#   LICENSE.txt, 3rd-Party.txt          ONLYOFFICE's own, shipped with the engine
# =============================================================================
param([string]$From = "")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$vendor = Join-Path $root "vendor\onlyoffice"
$cache = if ($From) { $From } else { Join-Path $root "vendor\downloads" }

$packages = @(
    @{ Name = "DesktopEditors_x64.zip"
       Url  = "https://github.com/ONLYOFFICE/DesktopEditors/releases/download/v9.4.0/DesktopEditors_x64.zip"
       Sha  = "22ae48a7813954e5079bff1ea211c6583aba67f34f2d0dbcb2431d4a20e94dfc" },
    @{ Name = "onlyoffice-documentserver_amd64.deb"
       Url  = "https://github.com/ONLYOFFICE/DocumentServer/releases/download/v9.4.0/onlyoffice-documentserver_amd64.deb"
       Sha  = "0860e68c4fecf429b4e13602a4a5ec6945e6ec9f0e9af9867ef0171845aa07df" }
)

New-Item -ItemType Directory -Force $cache | Out-Null

foreach ($p in $packages) {
    $file = Join-Path $cache $p.Name
    if (-not (Test-Path $file)) {
        Write-Host "  downloading $($p.Name)"
        # curl, not Invoke-WebRequest: one long connection to GitHub's release
        # storage was measured slowing to 10 KB/s partway through, and curl
        # resumes (-C -) where a stalled attempt stopped.
        $curl = Join-Path $env:WINDIR "System32\curl.exe"
        & $curl -L --retry 10 --retry-delay 3 --speed-limit 100000 --speed-time 30 -C - -o $file $p.Url
        $tries = 0
        while ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 33) {
            if (++$tries -gt 20) { throw "could not download $($p.Name) (curl $LASTEXITCODE)" }
            Write-Host "    the connection slowed; resuming"
            & $curl -L --retry 10 --retry-delay 3 --speed-limit 100000 --speed-time 30 -C - -o $file $p.Url
        }
    }
    $sha = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha -ne $p.Sha) { throw "$($p.Name) does not match the published SHA-256 ($sha). Delete it and run again." }
    Write-Host "  verified  $($p.Name)"
}

$tar = Join-Path $env:WINDIR "System32\tar.exe"
$scratch = Join-Path $cache "extract"
if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
New-Item -ItemType Directory -Force $scratch | Out-Null

# ---- the editors, from the Document Server package -------------------------
Write-Host "  extracting the editors"
Push-Location $scratch
# tar writes notes to stderr, and Windows PowerShell turns any stderr line from
# a native program into a terminating error under "Stop". The results are
# checked by what is on disk instead.
$ErrorActionPreference = "Continue"
try {
    & $tar -xf (Join-Path $cache "onlyoffice-documentserver_amd64.deb")
    $data = Get-ChildItem -Filter "data.tar.*" | Select-Object -First 1
    if (-not $data) { throw "the Document Server package has no data archive" }
    & $tar -xf $data.Name "./var/www/onlyoffice/documentserver/sdkjs" "./var/www/onlyoffice/documentserver/web-apps" `
        "./var/www/onlyoffice/documentserver/LICENSE.txt" "./var/www/onlyoffice/documentserver/3rd-Party.txt" 2>$null
    if (-not (Test-Path "var\www\onlyoffice\documentserver\sdkjs")) { throw "sdkjs was not in the Document Server package" }
}
finally { Pop-Location; $ErrorActionPreference = "Stop" }

# ---- the converter, from the desktop package -------------------------------
Write-Host "  extracting the converter"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $cache "DesktopEditors_x64.zip"))
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName
        if (-not $name.StartsWith("converter/")) { continue }
        if ($name.StartsWith("converter/templates/")) { continue }   # 217 MB of sample files
        $target = Join-Path $scratch ("converter\" + $name.Substring(10).Replace('/', '\'))
        if ($name.EndsWith("/")) { New-Item -ItemType Directory -Force $target | Out-Null; continue }
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
}
finally { $zip.Dispose() }

# ---- into place -------------------------------------------------------------
if (Test-Path $vendor) { Remove-Item $vendor -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $vendor "editors") | Out-Null
$ds = Join-Path $scratch "var\www\onlyoffice\documentserver"
Move-Item (Join-Path $ds "sdkjs") (Join-Path $vendor "editors\sdkjs")
Move-Item (Join-Path $ds "web-apps") (Join-Path $vendor "editors\web-apps")
foreach ($f in "LICENSE.txt", "3rd-Party.txt") {
    if (Test-Path (Join-Path $ds $f)) { Move-Item (Join-Path $ds $f) (Join-Path $vendor $f) }
}
Move-Item (Join-Path $scratch "converter") (Join-Path $vendor "converter")
Remove-Item $scratch -Recurse -Force

# ---- api.js -------------------------------------------------------------------
# A Document Server ships api.js as a template and writes its version hash into
# it when it starts. Left unfilled, the template's own code sees "{{" and skips
# the versioned path -- which is what an editor served from disk wants -- so it
# is used as it is.
$api = Join-Path $vendor "editors\web-apps\apps\api\documents"
if (-not (Test-Path (Join-Path $api "api.js")) -and (Test-Path (Join-Path $api "api.js.tpl"))) {
    Copy-Item (Join-Path $api "api.js.tpl") (Join-Path $api "api.js")
}

# ---- what NextScan never loads ----------------------------------------------
# The help pages are 600 of the 1,080 MB, most of it animated GIFs, and the
# editors are started with help switched off. The mobile editors are for
# phones; NextScan starts the desktop ones.
$apps = Join-Path $vendor "editors\web-apps\apps"
Get-ChildItem $apps -Directory | ForEach-Object {
    $help = Join-Path $_.FullName "main\resources\help"
    if (Test-Path $help) { Remove-Item $help -Recurse -Force }
    $mobile = Join-Path $_.FullName "mobile"
    if (Test-Path $mobile) { Remove-Item $mobile -Recurse -Force }
}

$size = (Get-ChildItem $vendor -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("  done: vendor\onlyoffice, {0:N0} MB" -f $size) -ForegroundColor Green
