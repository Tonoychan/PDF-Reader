@echo off
rem Builds the app (build.cmd) and then the installer: SimplePDFNarrator-Setup.msi next to this script.
rem The installer is for the current user only (no administrator prompt). Details in installer.log.
setlocal
set "ROOT=%~dp0"
cd /d "%ROOT%"
set "VERSION=2.0.0"

call "%ROOT%build.cmd"
if errorlevel 1 exit /b 1

echo Making the installer... (details in installer.log)
call :main > "%ROOT%installer.log" 2>&1
if errorlevel 1 (
  echo INSTALLER FAILED - see installer.log
  timeout /t 5 >nul
  exit /b 1
)
echo INSTALLER OK - SimplePDFNarrator-Setup.msi is ready.
timeout /t 3 >nul
exit /b 0

:main
echo === %date% %time% ===
set "DOTNET=dotnet"
if exist "%ROOT%.sdk\dotnet.exe" (
  set "DOTNET=%ROOT%.sdk\dotnet.exe"
  set "DOTNET_MULTILEVEL_LOOKUP=0"
)
set "NUGET_PACKAGES=%ROOT%.packages"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
"%DOTNET%" build "%ROOT%source\installer\PdfReader.Installer.wixproj" -c Release -o "%ROOT%installer-output" ^
  "-p:AppExe=%ROOT%build-output\PDFReader.exe" ^
  "-p:AppIcon=%ROOT%source\src\PdfReader.App\app.ico" ^
  "-p:ProductVersion=%VERSION%"
if errorlevel 1 exit /b 1
copy /y "%ROOT%installer-output\SimplePDFNarrator-Setup.msi" "%ROOT%SimplePDFNarrator-Setup.msi" >nul
if errorlevel 1 exit /b 1
echo INSTALLER OK
exit /b 0
