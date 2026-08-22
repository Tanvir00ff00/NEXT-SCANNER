@echo off
REM Launches Claude Code inside Windows Terminal, which can render Bengali.
REM The legacy console (conhost) cannot - it has no complex-script shaping,
REM so Bangla shows as ???? there no matter which font is selected.
chcp 65001 >nul
wt.exe new-tab --title "Claude Code" cmd /k "chcp 65001 >nul && cd /d C:\PS_Fix && claude"