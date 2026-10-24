@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK not found. Install it with:   winget install Microsoft.DotNet.SDK.8
  echo ^(Only needed to BUILD. The finished exe runs without .NET installed.^)
  echo Then run this file again.
  pause
  exit /b 1
)

echo Building BleTerminal.exe ^(first run downloads packages, ~1-2 min^)...
dotnet publish src\BleTerminal\BleTerminal.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false ^
  -o dist
if errorlevel 1 (
  echo.
  echo Build failed. See the error above.
  pause
  exit /b 1
)

echo.
echo Done:  %~dp0dist\BleTerminal.exe
pause
