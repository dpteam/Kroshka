@echo off
rem ============================================================
rem  dev-server.cmd - run the Kroshka server against the live web/
rem
rem  Uses the repository root as the data directory, so edits to
rem  web/ are visible on the next browser reload without copying.
rem
rem    dev-server.cmd          start
rem    dev-server.cmd stop     stop a running instance
rem ============================================================
setlocal

set "ROOT=%~dp0"
cd /d "%ROOT%"

if /i "%~1"=="stop" goto :stop

rem Free the port before starting: a stale instance would silently keep it.
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /r /c:"LISTENING" ^| findstr ":6767"') do (
  taskkill /f /pid %%p >nul 2>&1
)

echo [1/2] building...
dotnet build src\DPT.Kroshka.Server\DPT.Kroshka.Server.csproj -v q --nologo || exit /b 1

echo [2/2] starting...
start "" /b dotnet run --project src\DPT.Kroshka.Server\DPT.Kroshka.Server.csproj --no-build -- --data "%ROOT:~0,-1%"
goto :eof

:stop
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /r /c:"LISTENING" ^| findstr ":6767"') do (
  echo stopping pid %%p
  taskkill /f /pid %%p >nul 2>&1
)
goto :eof
