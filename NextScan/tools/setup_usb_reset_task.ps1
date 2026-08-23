# =============================================================================
# NextScan Studio - one-time elevated setup for USB reset recovery.
#
# Registers the NextScan_UsbReset scheduled task (RunLevel Highest) pointing
# at tools\usb_reset.cmd. After this one consent, the application can reset a
# wedged scanner's USB device WITHOUT any further elevation prompts - the
# software equivalent of the "detach and reconnect the cable" that Canon's own
# error dialog demands (ScanGear Code 2,250,4).
#
# Idempotent: re-running replaces the task.
# =============================================================================
param([switch]$NoWait)

$ErrorActionPreference = "Stop"
$root  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$cmd   = Join-Path $root "tools\usb_reset.cmd"

if (-not (Test-Path $cmd)) { throw "helper missing: $cmd" }

# The task principal is the CURRENT user at highest privileges; a random
# once-per-boot start time keeps it registerable, the app triggers it on
# demand via schtasks /run (no prompt - the elevation was granted here).
$action   = New-ScheduledTaskAction -Execute "cmd.exe" -Argument ('/c "' + $cmd + '"')
$trigger  = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(1)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName "NextScan_UsbReset" -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Task NextScan_UsbReset registered (elevated, on-demand)." -ForegroundColor Green
if (-not $NoWait -and $Host.Name -eq "ConsoleHost") { Read-Host "Press Enter to close" }
