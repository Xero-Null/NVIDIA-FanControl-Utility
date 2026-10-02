@echo off
rem Builds and runs NVIDIA Fan Control Utility. Requires the .NET 8 SDK:
rem   winget install Microsoft.DotNet.SDK.8
cd /d "%~dp0"
dotnet run --project NVIDIAFanControlUtility\NVIDIAFanControlUtility.csproj -c Release
if errorlevel 1 (
    echo.
    echo Build failed. Make sure the .NET 8 SDK is installed:
    echo   winget install Microsoft.DotNet.SDK.8
)
pause
