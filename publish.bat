@echo off
rem Publishes a single self-contained NVIDIAFanControlUtility.exe (no .NET runtime needed on target).
cd /d "%~dp0"
dotnet publish NVIDIAFanControlUtility\NVIDIAFanControlUtility.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
if errorlevel 1 (
    echo.
    echo Publish failed. Make sure the .NET 8 SDK is installed:
    echo   winget install Microsoft.DotNet.SDK.8
    pause
    exit /b 1
)
echo.
echo Done. Portable exe is at:
echo   NVIDIAFanControlUtility\bin\Release\net8.0-windows\win-x64\publish\NVIDIAFanControlUtility.exe
pause
