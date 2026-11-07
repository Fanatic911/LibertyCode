@echo off
setlocal
rem Compiles LibertyCode.exe for every architecture into Finish\<arch>\
cd /d "%~dp0"

if exist Finish rmdir /s /q Finish

call :build win-x64   x64
call :build win-x86   x86
call :build win-arm64 arm64

echo.
echo Done. Output in Finish\
dir /b Finish
pause
exit /b 0

:build
echo.
echo === Building %2 (%1) ===
dotnet publish LibertyCode\LibertyCode.csproj -c Release -r %1 --self-contained true -p:PublishSingleFile=true -o "Finish\%2"
if errorlevel 1 (
  echo [SKIPPED] %2 failed to build.
  if exist "Finish\%2" rmdir /s /q "Finish\%2"
)
exit /b 0
