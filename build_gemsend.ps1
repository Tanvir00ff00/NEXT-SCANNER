$ErrorActionPreference = "Stop"
$src = "C:\PS_Fix\gemhelper.cs"
$exe = "C:\PS_Fix\gemhelper.exe"

Write-Host "Building Gemini Send..." -ForegroundColor Cyan
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