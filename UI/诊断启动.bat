@echo off
rem ============================================================
rem  SiliconScope diagnostic launcher
rem  Run this (double-click) if SiliconScope.exe fails to open.
rem  It turns on .NET host tracing and keeps the window open so
rem  you can see the exit code. Send back:
rem    1) this window's screenshot
rem    2) the generated file "hosttrace.txt"
rem    3) the "logs" folder (if present)
rem ============================================================
cd /d "%~dp0"
echo ==== SiliconScope diagnostic launch ====
echo Working directory: %CD%
echo.
set COREHOST_TRACE=1
set COREHOST_TRACEFILE=hosttrace.txt
set DOTNET_DiagnosticPorts=
start /wait "" "SiliconScope.exe"
set RC=%errorlevel%
echo.
echo ==========================================================
echo SiliconScope exited. Exit code: %RC%
echo (0 = launched OK; nonzero = host/startup failure)
echo Trace written to: %CD%\hosttrace.txt
echo ==========================================================
pause
