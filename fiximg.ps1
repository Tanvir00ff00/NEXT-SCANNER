[CmdletBinding(PositionalBinding=$false)]
param(
    [string]$Out  = "",
    [string]$Flag = "",
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$Files
)

$LogPath = "C:\PS_Fix\ps_log.txt"
function W([string]$m) {
    try { Add-Content -LiteralPath $LogPath -Value ((Get-Date -Format "HH:mm:ss") + "  " + $m) } catch { }
}

try { Set-Content -LiteralPath $LogPath -Value "--- start ---" } catch { }
W ("Out=[" + $Out + "]  files=" + $(if ($Files) { $Files.Count } else { 0 }))

if ((-not $Files -or $Files.Count -eq 0) -and -not [string]::IsNullOrWhiteSpace($Out) -and (Test-Path -LiteralPath $Out)) {
    W "recovered: -Out was actually the input"
    $Files = @($Out); $Out = ""
}

try {
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase
    Add-Type -AssemblyName System.Drawing
} catch { W ("assembly FAILED: " + $_.Exception.Message) }

$script:SrgbCtx = $null
function Get-Srgb {
    if ($script:SrgbCtx -ne $null) { return $script:SrgbCtx }
    $p = Join-Path $env:SystemRoot "System32\spool\drivers\color\sRGB Color Space Profile.icm"
    if (Test-Path -LiteralPath $p) {
        try { $script:SrgbCtx = New-Object System.Windows.Media.ColorContext((New-Object System.Uri($p))); return $script:SrgbCtx } catch { }
    }
    try { $script:SrgbCtx = New-Object System.Windows.Media.ColorContext([System.Windows.Media.PixelFormats]::Bgr24) } catch { }
    return $script:SrgbCtx
}

function Save-Frame {
    param($src, [string]$outFile)

    $conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap
    $conv.BeginInit()
    $conv.Source = $src
    $conv.DestinationFormat = [System.Windows.Media.PixelFormats]::Bgr24
    $conv.EndInit()

    $frame = $null
    $cc = Get-Srgb
    if ($cc -ne $null) {
        try {
            $list = New-Object 'System.Collections.Generic.List[System.Windows.Media.ColorContext]'
            $list.Add($cc)
            $frame = [System.Windows.Media.Imaging.BitmapFrame]::Create($conv, $null, $null, $list.AsReadOnly())
        } catch { }
    }
    if ($frame -eq $null) { $frame = [System.Windows.Media.Imaging.BitmapFrame]::Create($conv) }

    $ext = [System.IO.Path]::GetExtension($outFile).ToLower()
    if ($ext -eq ".png") { $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder }
    else {
        $enc = New-Object System.Windows.Media.Imaging.JpegBitmapEncoder
        $enc.QualityLevel = 96
    }
    $enc.Frames.Add($frame)

    $tmp = [System.IO.Path]::Combine($env:TEMP, "psfix_" + [System.Guid]::NewGuid().ToString("N") + $ext)
    $fs = [System.IO.File]::Open($tmp, [System.IO.FileMode]::Create)
    $enc.Save($fs)
    $fs.Close()

    if ([System.IO.File]::Exists($outFile)) { [System.IO.File]::Delete($outFile) }
    [System.IO.File]::Move($tmp, $outFile)
}

function Convert-One {
    param([string]$inPath, [string]$outFile)
    if ([string]::IsNullOrWhiteSpace($outFile)) { W "  empty target"; return $false }

    $bytes = $null
    try { $bytes = [System.IO.File]::ReadAllBytes($inPath) }
    catch { W ("  read FAILED: " + $_.Exception.Message); return $false }
    W ("  read " + [math]::Round($bytes.Length / 1KB) + " KB")

    try {
        $ms = New-Object System.IO.MemoryStream(,$bytes)
        $dec = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
                 $ms,
                 [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
                 [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $img = $dec.Frames[0]

        $ori = 1
        try {
            $md = $dec.Frames[0].Metadata
            if ($md -ne $null) {
                $v = $md.GetQuery("/app1/ifd/{ushort=274}")
                if ($v -ne $null) { $ori = [int]$v }
            }
        } catch { }
        $angle = 0
        if ($ori -eq 3) { $angle = 180 } elseif ($ori -eq 6) { $angle = 90 } elseif ($ori -eq 8) { $angle = 270 }
        if ($angle -ne 0) {
            try {
                $rt = New-Object System.Windows.Media.RotateTransform($angle)
                $img = New-Object System.Windows.Media.Imaging.TransformedBitmap($img, $rt)
                W ("  rotated " + $angle)
            } catch { }
        }

        $ms.Close()
        Save-Frame $img $outFile
        W ("  WIC saved -> " + $outFile)
        return $true
    } catch { W ("  WIC failed: " + $_.Exception.Message) }

    try {
        $ms2 = New-Object System.IO.MemoryStream(,$bytes)
        $bmp = [System.Drawing.Image]::FromStream($ms2)
        $mid = [System.IO.Path]::Combine($env:TEMP, "psfix_gdi_" + [System.Guid]::NewGuid().ToString("N") + ".png")
        $bmp.Save($mid, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose(); $ms2.Close()

        $b2 = [System.IO.File]::ReadAllBytes($mid)
        $ms3 = New-Object System.IO.MemoryStream(,$b2)
        $d2 = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
                $ms3,
                [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
                [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        Save-Frame $d2.Frames[0] $outFile
        $ms3.Close()
        try { [System.IO.File]::Delete($mid) } catch { }
        W ("  GDI saved -> " + $outFile)
        return $true
    } catch { W ("  GDI failed: " + $_.Exception.Message) }

    return $false
}

$done = 0
if ($Files -and $Files.Count -gt 0) {
    foreach ($f in $Files) {
        W ("input: " + $f)
        if (-not (Test-Path -LiteralPath $f)) { W "  not found"; continue }
        $full = [System.IO.Path]::GetFullPath($f)

        $target = ""
        if (-not [string]::IsNullOrWhiteSpace($Out)) { $target = $Out }
        else {
            $dir  = [System.IO.Path]::GetDirectoryName($full)
            $base = [System.IO.Path]::GetFileNameWithoutExtension($full)
            if ([string]::IsNullOrWhiteSpace($dir))  { $dir  = "C:\PS_Fix" }
            if ([string]::IsNullOrWhiteSpace($base)) { $base = "image" }
            $target = [System.IO.Path]::Combine($dir, $base + "_fix.jpg")
        }
        W ("target: " + $target)
        if (Convert-One $full $target) { $done++ }
    }
} else { W "NO FILES RECEIVED" }

W ("done = " + $done)
if (-not [string]::IsNullOrWhiteSpace($Flag)) {
    try { Set-Content -LiteralPath $Flag -Value $done -Encoding ASCII; W "flag written" } catch { }
}