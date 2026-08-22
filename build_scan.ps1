param([switch]$NoWait)
$ErrorActionPreference = "Stop"
$src = "C:\PS_Fix\scanhelper.cs"
$exe = "C:\PS_Fix\scanhelper.exe"

Write-Host "Building scanner bridge..." -ForegroundColor Cyan
if (-not (Test-Path $src)) { Write-Host "scanhelper.cs not found" -ForegroundColor Red; if (-not $NoWait) { Read-Host }; exit }

Get-Process scanhelper -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
if (Test-Path $exe) { Remove-Item $exe -Force }

$code = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
Add-Type -TypeDefinition $code `
         -ReferencedAssemblies @("System.Windows.Forms","System.Drawing","System.Core","System.Xml") `
         -OutputAssembly $exe `
         -OutputType WindowsApplication

if (Test-Path $exe) { Write-Host "OK -> $exe" -ForegroundColor Green }
else { Write-Host "FAILED" -ForegroundColor Red }
if (-not $NoWait) { Read-Host "Press Enter to close" }