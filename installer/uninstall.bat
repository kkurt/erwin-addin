@echo off
REM Elite Soft Erwin Add-In - Per-user uninstaller (double-click friendly).
REM
REM Same wrapping rationale as install.bat (see comments there). This file
REM just forwards "-Uninstall" to install-impl.ps1, so we keep one script as
REM the single source of truth for the install/uninstall logic.

setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-impl.ps1" -Uninstall %*
set "RC=%ERRORLEVEL%"
endlocal & exit /b %RC%
