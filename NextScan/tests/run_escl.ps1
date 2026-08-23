# =============================================================================
# NextScan Studio - eSCL harness (plan section 18.3/7.4)
#
#   .\tests\run_escl.ps1
#
# Runs the python eSCL simulator and drives the REAL production path against
# it: nsprobe -> DeviceBroker -> EsclDriver (in-process) -> HTTP. The manual
# device comes from NEXTSCAN_ESCL_URL, the same env var an operator uses on a
# network that blocks mDNS.
# =============================================================================
param([switch]$NoWait)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$nsprobe = Join-Path $root "bin\nsprobe.exe"
$sim = Join-Path $root "tests\escl_sim.py"
$port = 8951
$failed = 0

if (-not (Test-Path $nsprobe)) { throw "nsprobe.exe not built. Run .\build.ps1 first." }
if (-not (Test-Path $sim)) { throw "missing fixture: $sim" }

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("  ok   " + $name) -ForegroundColor Green }
    else { $script:failed++; Write-Host ("  FAIL " + $name + ": " + $detail) -ForegroundColor Red }
}

function Start-Sim([hashtable]$envExtra) {
    foreach ($k in $envExtra.Keys) { Set-Item ("Env:" + $k) $envExtra[$k] }
    $p = Start-Process python -ArgumentList ('"' + $sim + '" ' + $port) `
        -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root "out\escl_sim_err.txt")
    foreach ($k in $envExtra.Keys) { Remove-Item ("Env:" + $k) -ErrorAction SilentlyContinue }
    # wait for the listener
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ($p.HasExited) { throw "simulator died on startup - see out\escl_sim_err.txt" }
        try {
            $tcp = New-Object Net.Sockets.TcpClient
            $tcp.Connect("127.0.0.1", $port); $tcp.Close(); break
        } catch { }
    }
    return $p
}

Write-Host "eSCL harness" -ForegroundColor Cyan
$env:NEXTSCAN_ESCL_URL = "http://127.0.0.1:$port/eSCL"

# ---------------- case 1: well-behaved (UUID job ids) ----------------
$simProc = Start-Sim @{ "ESCL_SIM_PAGES" = "2" }
try {
    $list = & $nsprobe list 2>&1 | Out-String
    Check "probe_sees_manual_device" ($list -match [regex]::Escape("eSCL Manual")) ($list)

    $caps = & $nsprobe caps "eSCL" --transport escl 2>&1 | Out-String
    Check "caps_flatbed_8.5x11.7" ($caps -match "8\.5 x 11\.7") ($caps)
    Check "caps_three_colour_modes" ($caps -match "Color24" -and $caps -match "Gray8" -and $caps -match "BlackWhite1") ($caps)
    Check "caps_resolutions" ($caps -match "75, 150, 300") ($caps)

    $scan = & $nsprobe scan "eSCL" --transport escl --dpi 150 --pages 2 2>&1 | Out-String
    Check "scan_page_48x32" ($scan -match "48x32") ($scan)
    Check "scan_two_pages" ($scan -match "Done: 2 page") ($scan)

    $p1 = Join-Path $root "out\escl_page.png"
    $scan2 = & $nsprobe scan "eSCL" --transport escl --dpi 150 --out $p1 2>&1 | Out-String
    Check "scan_saves_png" (Test-Path $p1) ($scan2)
}
finally {
    if ($simProc -and -not $simProc.HasExited) { Stop-Process -Id $simProc.Id -Force }
    Remove-Item Env:NEXTSCAN_ESCL_URL -ErrorAction SilentlyContinue
}

# ---------------- case 2: 503 storm + int job ids ----------------
$env:NEXTSCAN_ESCL_URL = "http://127.0.0.1:$port/eSCL"
$simProc = Start-Sim @{ "ESCL_SIM_503_COUNT" = "2"; "ESCL_SIM_JOB_STYLE" = "int"; "ESCL_SIM_PAGES" = "1" }
try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $scan = & $nsprobe scan "eSCL" --transport escl --dpi 150 2>&1 | Out-String
    $secs = $sw.Elapsed.TotalSeconds
    Check "scan_survives_503_storm" ($scan -match "48x32") ($scan)
    Check "storm_cost_about_2s" ($secs -ge 2.0 -and $secs -le 8.0) ("elapsed " + [math]::Round($secs,1) + "s (2 x 1s retries expected)")
}
finally {
    if ($simProc -and -not $simProc.HasExited) { Stop-Process -Id $simProc.Id -Force }
    Remove-Item Env:NEXTSCAN_ESCL_URL -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failed -gt 0) { Write-Host "$failed case(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "All eSCL cases passed." -ForegroundColor Green
exit 0
