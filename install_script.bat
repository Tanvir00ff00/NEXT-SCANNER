@echo off
echo ========================================================
echo Installing Scan_Import.jsx to Photoshop 2026 Scripts...
echo ========================================================

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator permission...
    powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c copy /y \"\"C:\PS_Fix\Scan_Import.jsx\"\" \"\"C:\Program Files\Adobe\Adobe Photoshop 2026\Presets\Scripts\Scan_Import.jsx\"\" & echo. & echo Successfully copied to Photoshop Presets Scripts! & pause' -Verb RunAs"
    exit /b
)

copy /y "C:\PS_Fix\Scan_Import.jsx" "C:\Program Files\Adobe\Adobe Photoshop 2026\Presets\Scripts\Scan_Import.jsx"
echo.
echo Successfully copied to Photoshop Presets Scripts!
pause
