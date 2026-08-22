$ErrorActionPreference = "Stop"
$src = "C:\PS_Fix\geminifix.cs"
$exe = "C:\PS_Fix\geminifix.exe"

Write-Host "Building Gemini Fix..." -ForegroundColor Cyan
if (Test-Path $exe) { Remove-Item $exe -Force }

$code = Get-Content -LiteralPath $src -Raw
Add-Type -TypeDefinition $code `
         -ReferencedAssemblies @("System.Windows.Forms","System.Drawing","System.Core") `
         -OutputAssembly $exe `
         -OutputType WindowsApplication

if (Test-Path $exe) { Write-Host "OK -> $exe" -ForegroundColor Green }
else { Write-Host "FAILED" -ForegroundColor Red }
Write-Host "Press Enter to close"
Read-Host