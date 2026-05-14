@echo off
REM Elite Soft Erwin Add-In - Per-user installer (double-click friendly).
REM
REM Wraps install-impl.ps1 so the user can extract the ZIP and double-click
REM this file. install-impl.ps1 is the actual installer logic; this .bat is
REM the user-facing entry point (and the only "install*" name without a
REM script extension, so there is no double-click ambiguity).
REM
REM Three things to note:
REM
REM   1. "cd /d %~dp0" makes the script's own folder the cwd. When you
REM      double-click a .bat in Explorer, cwd defaults to C:\Windows\System32;
REM      without this line install-impl.ps1 would copy files from the wrong
REM      place (no install-impl.ps1 sibling found in System32).
REM
REM   2. "-ExecutionPolicy Bypass" is a per-process flag baked into the
REM      powershell.exe command line, NOT a Set-ExecutionPolicy call. GPO
REM      MachinePolicy/UserPolicy can override the latter but cannot override
REM      this one (the policy is consulted per-invocation), so corporate-
REM      managed laptops run the script without the "policy override"
REM      SecurityException that bare Set-ExecutionPolicy would throw.
REM
REM   3. "%*" forwards any extra args (e.g. -DBHost x -DBName y) so packagers
REM      can keep their existing CLI workflow even when calling via the .bat.

setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-impl.ps1" %*
set "RC=%ERRORLEVEL%"
endlocal & exit /b %RC%
