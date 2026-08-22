$ErrorActionPreference = "Stop"
$exe = "C:\PS_Fix\gemserver.exe"

Write-Host "Building Gemini Bridge (CDP Edition)..." -ForegroundColor Cyan
if (Test-Path $exe) { Remove-Item $exe -Force }

$sources = @(
    "C:\PS_Fix\Cdp\SimpleJson.cs",
    "C:\PS_Fix\Cdp\CdpClient.cs",
    "C:\PS_Fix\Cdp\ChromeLauncher.cs",
    "C:\PS_Fix\Cdp\CdpSession.cs",
    "C:\PS_Fix\Cdp\PageAutomation.cs",
    "C:\PS_Fix\Cdp\ISiteAdapter.cs",
    "C:\PS_Fix\Cdp\SiteGemini.cs",
    "C:\PS_Fix\Cdp\SiteAIStudio.cs",
    "C:\PS_Fix\Cdp\SitePuter.cs",
    "C:\PS_Fix\Cdp\SitePuterNode.cs",
    "C:\PS_Fix\Cdp\JobRunner.cs",
    "C:\PS_Fix\gemserver.cs"
)

$usings = New-Object System.Collections.Generic.HashSet[string]
$bodyBlocks = New-Object System.Collections.Generic.List[string]

foreach ($s in $sources) {
    if (-not (Test-Path $s)) { throw "Source file not found: $s" }
    $lines = Get-Content -LiteralPath $s
    $cleanLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        # Only real using DIRECTIVES get hoisted. The old test also matched a one-line
        # "using (x) y;" STATEMENT and moved it to the top of the file, which fails with
        # a baffling "Identifier expected" on line 1.
        if ($trimmed -match '^using\s+(static\s+)?[A-Za-z_@][A-Za-z0-9_.]*(\s*=\s*[A-Za-z_@][A-Za-z0-9_.]*)?\s*;$') {
            [void]$usings.Add($trimmed)
        } else {
            [void]$cleanLines.Add($line)
        }
    }
    [void]$bodyBlocks.Add(($cleanLines -join "`r`n"))
}

$header = ($usings | Sort-Object) -join "`r`n"
$combinedCode = $header + "`r`n`r`n" + ($bodyBlocks -join "`r`n`r`n")

Add-Type -TypeDefinition $combinedCode `
         -ReferencedAssemblies @("System.Windows.Forms","System.Drawing","System.Core",
                                 "PresentationCore","WindowsBase","System.Xaml",
                                 "System.Net.Http","Microsoft.CSharp") `
         -OutputAssembly $exe -OutputType WindowsApplication

if (Test-Path $exe) { Write-Host "OK -> $exe" -ForegroundColor Green }
else { Write-Host "FAILED" -ForegroundColor Red }