@echo off
setlocal enabledelayedexpansion
rem ============================================================================
rem  build.cmd - build Kroshka from source into bin\
rem
rem    build.cmd             Release, framework dependent (fast, needs .NET 10)
rem    build.cmd self        self-contained single folder per platform
rem    build.cmd publish     ready-to-ship zips in artifacts\
rem    build.cmd test        run the self-test suite only
rem    build.cmd clean       remove bin\ and obj\
rem
rem  Requires only the .NET 10 SDK. No NuGet beyond WebView2 for the client.
rem ============================================================================

set "ROOT=%~dp0"
cd /d "%ROOT%"

if /i "%~1"=="clean" goto :clean
if /i "%~1"=="test" goto :test
if /i "%~1"=="self" goto :selfcontained
if /i "%~1"=="publish" goto :publish

rem ------------------------------------------------------------------ default
echo [1/3] restoring...
dotnet restore Kroshka.sln -v q || goto :fail

echo [2/3] building Release...
dotnet build Kroshka.sln -c Release --no-restore -v q || goto :fail

echo [3/3] staging runtime files...
call :stage "src\DPT.Kroshka.Server\bin\Release\net10.0" "bin\server"
call :stage "src\DPT.Kroshka.Client\bin\Release\net10.0-windows" "bin\client"

echo.
echo   server : bin\server\KroshkaServer.exe
echo   client : bin\client\Kroshka.exe
echo   web    : served from web\ (edit and reload)
echo.
goto :eof

rem --------------------------------------------------------- self-contained
:selfcontained
echo [1/2] publishing self-contained builds (win-x64, linux-x64)...

dotnet publish src\DPT.Kroshka.Server\DPT.Kroshka.Server.csproj ^
  -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=false -o bin\self\server-win-x64 -v q || goto :fail

dotnet publish src\DPT.Kroshka.Server\DPT.Kroshka.Server.csproj ^
  -c Release -r linux-x64 --self-contained true ^
  -p:PublishSingleFile=false -o bin\self\server-linux-x64 -v q || goto :fail

dotnet publish src\DPT.Kroshka.Client\DPT.Kroshka.Client.csproj ^
  -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=false -o bin\self\client-win-x64 -v q || goto :fail

echo [2/2] copying runtime files...
call :stage "bin\self\server-win-x64" "bin\self\server-win-x64"
call :stage "bin\self\server-linux-x64" "bin\self\server-linux-x64"
call :stage "bin\self\client-win-x64" "bin\self\client-win-x64"

echo.
echo   bin\self\server-win-x64\KroshkaServer.exe
echo   bin\self\server-linux-x64\KroshkaServer
echo   bin\self\client-win-x64\Kroshka.exe
echo.
goto :eof

rem ---------------------------------------------------------------- publish
:publish
echo Building distributable archives...

if exist artifacts rmdir /s /q artifacts
mkdir artifacts

call :pack "server-win-x64" "KroshkaServer.exe" "win-x64" "server"
call :pack "server-linux-x64" "KroshkaServer" "linux-x64" "server"
call :pack "client-win-x64" "Kroshka.exe" "win-x64" "client"

call :installer

echo.
echo   artifacts\
dir /b artifacts
goto :eof

:installer
rem The installer is the thing most people actually download, so build it last
rem and only if its compiler is present.
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not exist "%ISCC%" (
  echo   [skip] installer - Inno Setup 6 not found
  echo          install it, then run: build.cmd publish
  goto :eof
)

if not exist "bin\self\client-win-x64\Kroshka.exe" (
  echo   [skip] installer - run "build.cmd self" first
  goto :eof
)

echo   building installer...
"%ISCC%" /Q installer\kroshka.iss >nul
if errorlevel 1 (
  echo   [warn] installer build failed
) else (
  echo   packed kroshka installer
)
goto :eof

:pack
rem %1 = folder under bin\self, %2 = exe to verify, %3 = rid, %4 = role
set "SRC=bin\self\%~1"
set "EXE=%~2"
set "RID=%~3"
set "ROLE=%~4"

if not exist "%SRC%\%EXE%" (
  echo   [skip] %~1 - missing %EXE%, run "build.cmd self" first
  goto :eof
)

call :stage "%SRC%" "%SRC%"

set "NAME=kroshka-%ROLE%-%RID%"
if exist "artifacts\%NAME%" rmdir /s /q "artifacts\%NAME%"
xcopy "%SRC%" "artifacts\%NAME%\" /E /I /Y /Q >nul

echo   packed %NAME%
goto :eof

rem ------------------------------------------------------------------ stage
:stage
rem %1 = build output, %2 = target
set "SRC=%~1"
set "DST=%~2"

if not exist "%DST%" mkdir "%DST%"

rem The compiled output goes in first: without this the folder has web/ and data/
rem but no exe, which looks like a successful build and is not one.
rem
rem When SRC and DST are the same folder (the self-contained case) there is nothing
rem to copy - xcopy would try to copy the tree into itself.
if /i not "%SRC%"=="%DST%" (
  xcopy "%SRC%" "%DST%\" /E /I /Y /Q >nul
  if errorlevel 1 (
    echo   [warn] could not copy binaries from %SRC%
  )
)

if not exist "%DST%\web" mkdir "%DST%\web"
xcopy "web" "%DST%\web\" /E /I /Y /Q >nul

if not exist "%DST%\data" mkdir "%DST%\data"
if not exist "%DST%\logs" mkdir "%DST%\logs"

if not exist "%DST%\config.example.ini" (
  copy "config.example.ini" "%DST%\config.example.ini" >nul
)

if not exist "%DST%\README.md" (
  copy "README.md" "%DST%\README.md" >nul 2>nul
)

if not exist "%DST%\LICENSE" (
  copy "LICENSE" "%DST%\LICENSE" >nul 2>nul
)

goto :eof

rem ------------------------------------------------------------------- test
:test
echo Running self-test...
dotnet build src\DPT.Kroshka.Server\DPT.Kroshka.Server.csproj -c Release -v q || goto :fail
dotnet run --project src\DPT.Kroshka.Server\DPT.Kroshka.Server.csproj -c Release --no-build -- --selftest
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" (
  echo.
  echo [ERROR] self-test failed with code %CODE%
  exit /b %CODE%
)
echo.
echo self-test passed.
goto :eof

rem ------------------------------------------------------------------ clean
:clean
echo Removing build output...
for %%d in (bin obj) do (
  if exist "%%d" rmdir /s /q "%%d"
)
for %%d in (src\DPT.Kroshka.Shared\bin src\DPT.Kroshka.Shared\obj
            src\DPT.Kroshka.Server\bin src\DPT.Kroshka.Server\obj
            src\DPT.Kroshka.Client\bin src\DPT.Kroshka.Client\obj) do (
  if exist "%%d" rmdir /s /q "%%d"
)
echo done.
goto :eof

:fail
echo.
echo [ERROR] build failed.
exit /b 1
