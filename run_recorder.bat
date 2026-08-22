@echo off
title Windows HD Screen Recorder
cd /d "%~dp0"
echo ===================================================
echo   Starting Windows HD Screen Recorder...
echo ===================================================
python screen_recorder.py
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo An error occurred. Checking dependencies...
    python -m pip install -r requirements_recorder.txt
    python screen_recorder.py
)
pause
