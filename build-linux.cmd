@echo off
setlocal enabledelayedexpansion
rem ============================================================================
rem  build-linux.cmd - собирает Linux-клиент Kroshka
rem
rem    build-linux.cmd            linux-x64, self-contained
rem    build-linux.cmd arm64      linux-arm64 (Raspberry Pi, ARM-серверы)
rem    build-linux.cmd both       обе архитектуры
rem    build-linux.cmd test       только внутренние тесты клиента
rem
rem  Требуется только .NET 10 SDK. Кросс-компиляция с Windows работает,
rem  потому что весь код — чистый BCL без нативных библиотек.
rem ============================================================================

set "ROOT=%~dp0"
cd /d "%ROOT%"

if /i "%~1"=="test" goto :test
if /i "%~1"=="arm64" (set "RIDS=linux-arm64") else if /i "%~1"=="both" (set "RIDS=linux-x64 linux-arm64") else (set "RIDS=linux-x64")

echo [1/3] проверка сборки...
dotnet build src\DPT.Kroshka.Linux\DPT.Kroshka.Linux.csproj -c Release -v q --nologo || goto :fail

echo [2/3] публикация...
for %%r in (%RIDS%) do (
  echo   %%r ...
  dotnet publish src\DPT.Kroshka.Linux\DPT.Kroshka.Linux.csproj ^
    -c Release -r %%r --self-contained true ^
    -p:PublishSingleFile=false ^
    -o bin\linux\%%r -v q || goto :fail

  call :stage "bin\linux\%%r"
)

echo [3/3] архивы...
if exist artifacts\linux mkdir artifacts\linux >nul 2>&1
if not exist artifacts mkdir artifacts >nul

for %%r in (%RIDS%) do (
  rem The RID already starts with "linux", so do not prefix it again.
  set "NAME=kroshka-%%r"
  if exist "artifacts\!NAME!.tar.gz" del "artifacts\!NAME!.tar.gz"

  rem Pack the *contents*, not the wrapper folder: after `tar -xzf` the user
  rem should see kroshka and install.sh immediately, not a folder inside a folder.
  rem tar keeps the execute bit; zip loses it and ./kroshka would not start.
  tar -czf "artifacts\!NAME!.tar.gz" -C "bin\linux\%%r" .
  echo   artifacts\!NAME!.tar.gz
)

echo.
echo   bin\linux\linux-x64\kroshka
echo   artifacts\kroshka-linux-*.tar.gz
echo.
goto :eof

rem --------------------------------------------------------------- stage
:stage
rem %1 = папка публикации
set "DST=%~1"

rem Отладочные символы не нужны в релизном архиве и весят десятки мегабайт.
del /q "%DST%\*.pdb" >nul 2>&1

if not exist "%DST%\web" mkdir "%DST%\web"
xcopy "web" "%DST%\web\" /E /I /Y /Q >nul

if not exist "%DST%\icons" mkdir "%DST%\icons"
if exist "web\assets\img\logo-192.png" copy "web\assets\img\logo-192.png" "%DST%\icons\kroshka-192.png" >nul
if exist "web\assets\img\logo-512.png" copy "web\assets\img\logo-512.png" "%DST%\icons\kroshka-512.png" >nul

copy "config.example.ini" "%DST%\config.example.ini" >nul 2>&1
copy "linux\install.sh" "%DST%\install.sh" >nul 2>&1
copy "README.md" "%DST%\README.md" >nul 2>&1
copy "LICENSE" "%DST%\LICENSE" >nul 2>&1

rem install.sh должен быть исполняемым внутри архива.
if exist "%DST%\install.sh" (
  powershell -NoProfile -Command "$p='%DST%\install.sh'; $c=[IO.File]::ReadAllText($p); [IO.File]::WriteAllText($p, ($c -replace \"`r`n\", \"`n\"), (New-Object Text.UTF8Encoding($false)))" >nul 2>&1
)

goto :eof

rem ---------------------------------------------------------------- test
:test
echo Тесты Linux-клиента...
dotnet build src\DPT.Kroshka.Linux\DPT.Kroshka.Linux.csproj -c Release -v q || goto :fail
dotnet run --project src\DPT.Kroshka.Linux\DPT.Kroshka.Linux.csproj -c Release --no-build -- --selftest
if errorlevel 1 goto :fail
echo.
echo все тесты прошли.
goto :eof

:fail
echo.
echo [ERROR] сборка не удалась.
exit /b 1
