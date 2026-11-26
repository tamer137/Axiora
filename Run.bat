@echo off
cd /d "%~dp0"
set "EXE=%~dp0bin\Release\net8.0-windows\AxioraSystemCare.exe"
if exist "%EXE%" (
    start "" "%EXE%"
    exit /b 0
)
echo Building Axiora SystemCare...
dotnet run --project "%~dp0SystemCare20.csproj" -c Release
if errorlevel 1 (
    echo.
    echo Launch failed. Install the .NET 8 SDK, then run this file again.
    pause
)
