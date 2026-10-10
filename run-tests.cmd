@echo off
rem Lets "run-tests" work from Command Prompt as well as PowerShell.
rem Usage: run-tests            (auto: AI if Claude Code is available)
rem        run-tests rules      (free, deterministic, about 1 minute)
rem        run-tests ai         (real Claude agents required, about 30 minutes)
setlocal
set "MODE=%~1"
if "%MODE%"=="" set "MODE=auto"
if /I "%MODE%"=="ai" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-tests.ps1" -Agents ai -RequireAi
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-tests.ps1" -Agents %MODE%
)
exit /b %ERRORLEVEL%
