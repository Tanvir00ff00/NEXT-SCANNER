$ErrorActionPreference = "Stop"
$src = "C:\PS_Fix\fiximg.cs"
$exe = "C:\PS_Fix\fiximg.exe"

Write-Host "Building image repair tool..." -ForegroundColor Cyan
if (-not (Test-Path $src)) { Write-Host "fiximg.cs not found" -ForegroundColor Red; Read-Host; exit }

# a resident copy may be holding the file
Get-Process fiximg -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
if (Test-Path $exe) { Remove-Item $exe -Force }

$code = Get-Content -LiteralPath $src -Raw
Add-Type -TypeDefinition $code `
         -ReferencedAssemblies @("PresentationCore","WindowsBase","System.Xaml","System.Drawing") `
         -OutputAssembly $exe `
         -OutputType WindowsApplication

if (Test-Path $exe) { Write-Host "OK -> $exe" -ForegroundColor Green }
else { Write-Host "FAILED" -ForegroundColor Red }
Read-Host "Press Enter to close"