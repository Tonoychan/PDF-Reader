@echo off
rem Builds PDF Reader into a single self-contained PDFReader.exe next to this script.
rem If no .NET SDK is installed, it installs the .NET 8 SDK into a local .sdk folder here (no admin).
setlocal
set "ROOT=%~dp0"
cd /d "%ROOT%"
echo Building PDF Reader... (details in build.log)
call :main > "%ROOT%build.log" 2>&1
if errorlevel 1 (
  echo BUILD FAILED - see build.log
  timeout /t 5 >nul
  exit /b 1
)
echo BUILD OK - PDFReader.exe is ready.
timeout /t 3 >nul
exit /b 0

:main
echo === %date% %time% ===
set "DOTNET=dotnet"
set "HAS_SDK="
if exist "%ROOT%.sdk\dotnet.exe" goto uselocal
for /f "delims=" %%v in ('dotnet --list-sdks 2^>nul') do set "HAS_SDK=1"
if defined HAS_SDK goto build
echo No .NET SDK found - installing the .NET 8 SDK into "%ROOT%.sdk"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol='Tls12'; Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%ROOT%dotnet-install.ps1'; & '%ROOT%dotnet-install.ps1' -Channel 8.0 -InstallDir '%ROOT%.sdk'"
if not exist "%ROOT%.sdk\dotnet.exe" (
  echo SDK install failed
  exit /b 1
)
:uselocal
set "DOTNET=%ROOT%.sdk\dotnet.exe"
set "DOTNET_MULTILEVEL_LOOKUP=0"
:build
rem Keep downloaded packages inside this folder.
set "NUGET_PACKAGES=%ROOT%.packages"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
"%DOTNET%" --list-sdks
"%DOTNET%" publish "%ROOT%source\src\PdfReader.App\PdfReader.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "%ROOT%build-output"
if errorlevel 1 exit /b 1
rem If the app is running, Windows still lets us rename its exe, so move it aside first.
if exist "%ROOT%PDFReader.old.exe" del /f /q "%ROOT%PDFReader.old.exe" >nul 2>&1
if exist "%ROOT%PDFReader.exe" move /y "%ROOT%PDFReader.exe" "%ROOT%PDFReader.old.exe" >nul 2>&1
copy /y "%ROOT%build-output\PDFReader.exe" "%ROOT%PDFReader.exe" >nul
if errorlevel 1 exit /b 1
echo BUILD OK
exit /b 0
