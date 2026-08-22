$KeysFile = "C:\PS_Fix\keys.txt"
$Report   = "C:\PS_Fix\diag_report.txt"
$TestImg  = "C:\PS_Fix\diag_test.jpg"

$MODELS = @(
    "gemini-3.1-flash-image",
    "gemini-3.1-flash-lite-image",
    "gemini-2.5-flash-image"
)

Set-Content -LiteralPath $Report -Value ""
function Say([string]$m) {
    Write-Host $m
    Add-Content -LiteralPath $Report -Value $m
}

function ErrBody($ex) {
    try {
        $st = $ex.Exception.Response.GetResponseStream()
        $sr = New-Object IO.StreamReader($st)
        return $sr.ReadToEnd()
    } catch { return "(no body)" }
}

function ErrCode($ex) {
    try { return [int]$ex.Exception.Response.StatusCode } catch { return 0 }
}

Say "=== GEMINI API DIAGNOSTIC ==="
Say ("time: " + (Get-Date))
Say ""

$key = $null
if (Test-Path $KeysFile) {
    foreach ($ln in Get-Content -LiteralPath $KeysFile) {
        $t = $ln.Trim()
        if ($t.Length -gt 10 -and -not $t.StartsWith("#")) { $key = $t; break }
    }
}
if (-not $key) { Say "NO KEY FOUND"; Read-Host "Enter to close"; exit }
Say ("key tail ..." + $key.Substring($key.Length - 6) + "   length " + $key.Length)
Say ""

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Add-Type -AssemblyName System.Drawing
if (-not (Test-Path $TestImg)) {
    $bmp = New-Object System.Drawing.Bitmap 64,64
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::LightGray)
    $g.Dispose()
    $bmp.Save($TestImg, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bmp.Dispose()
}
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($TestImg))

# ---------- 1. text call ----------
Say "--- 1. TEXT CALL ---"
try {
    $u = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent"
    $r = Invoke-WebRequest -Uri $u -Method POST -UseBasicParsing -TimeoutSec 60 `
         -Headers @{ "x-goog-api-key" = $key } -ContentType "application/json" `
         -Body '{"contents":[{"parts":[{"text":"say ok"}]}]}' -ErrorAction Stop
    Say ("    HTTP " + $r.StatusCode + "   key is ALIVE for text")
} catch {
    Say ("    HTTP " + (ErrCode $_))
    Say ("    " + (ErrBody $_))
}
Say ""

# ---------- 2. image models, full google reply ----------
Say "--- 2. IMAGE MODELS ---"
foreach ($model in $MODELS) {
    Say ""
    Say (">>> " + $model)

    $body = '{"contents":[{"parts":[' +
            '{"text":"Make the background pure white."},' +
            '{"inline_data":{"mime_type":"image/jpeg","data":"' + $b64 + '"}}' +
            ']}],"generationConfig":{"responseModalities":["TEXT","IMAGE"]}}'

    $u = "https://generativelanguage.googleapis.com/v1beta/models/$model" + ":generateContent"
    try {
        $r = Invoke-WebRequest -Uri $u -Method POST -UseBasicParsing -TimeoutSec 120 `
             -Headers @{ "x-goog-api-key" = $key } -ContentType "application/json" `
             -Body $body -ErrorAction Stop
        if ($r.Content -match '"(inlineData|inline_data)"') {
            Say ("    HTTP " + $r.StatusCode + "   *** IMAGE RETURNED - WORKS ***")
        } else {
            Say ("    HTTP " + $r.StatusCode + "   replied, NO image")
            Say ("    " + $r.Content.Substring(0, [Math]::Min(500, $r.Content.Length)))
        }
    } catch {
        Say ("    HTTP " + (ErrCode $_))
        Say "    ---- GOOGLE SAYS ----"
        Say ("    " + (ErrBody $_))
        Say "    ---------------------"
    }
    Start-Sleep -Seconds 2
}

Say ""
Say "=== END ==="
Read-Host "Press Enter to close"