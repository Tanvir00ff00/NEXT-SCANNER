@echo off
rem =============================================================================
rem NextScan Studio - elevated USB reset helper (run by the NextScan_UsbReset
rem scheduled task, never directly by the app).
rem
rem Reads the target device instance from the file written by UsbReset and
rem retries the pnputil restart up to three times with pauses - the field
rem complaint was that a physical re-plug also sometimes needs several tries
rem before the carriage lets go.
rem =============================================================================
setlocal
set "TARGETFILE=%~dp0..\tmp\usb_reset_target.txt"
if not exist "%TARGETFILE%" set "TARGETFILE=%~dp0..\..\NextScan\tmp\usb_reset_target.txt"
if not exist "%TARGETFILE%" exit /b 2

set /p TARGET=<"%TARGETFILE%"
if "%TARGET%"=="" exit /b 2

set "LOGFILE=%~dp0..\tmp\usb_reset.log"
if not exist "%~dp0..\tmp" mkdir "%~dp0..\tmp"

echo [%date% %time%] resetting %TARGET% >> "%LOGFILE%"

set /a TRIES=0
:retry
set /a TRIES+=1
echo [%date% %time%] attempt %TRIES% >> "%LOGFILE%"
pnputil /restart-device "%TARGET%" >> "%LOGFILE%" 2>&1
if %ERRORLEVEL%==0 goto done
if %TRIES% LSS 3 (
    timeout /t 3 /nobreak >nul
    goto retry
)
echo [%date% %time%] gave up after %TRIES% attempts >> "%LOGFILE%"
exit /b 1

:done
echo [%date% %time%] success >> "%LOGFILE%"
exit /b 0
