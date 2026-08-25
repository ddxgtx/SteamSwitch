@echo off
echo ========================================
echo Steam Switch - Build Script
echo ========================================
echo.

REM Check if .NET SDK is installed
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK is not installed!
    echo.
    echo Please download and install .NET 8.0 SDK from:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo .NET SDK found:
dotnet --version
echo.

echo Building Steam Switch...
echo.

REM Restore, build and test the complete solution
dotnet restore SteamSwitch.sln --configfile NuGet.Config
if errorlevel 1 (
    echo.
    echo RESTORE FAILED!
    pause
    exit /b 1
)

dotnet build SteamSwitch.sln -c Release --no-restore

if errorlevel 1 (
    echo.
    echo BUILD FAILED!
    pause
    exit /b 1
)

dotnet test tests\SteamSwitcher.Tests\SteamSwitcher.Tests.csproj -c Release --no-build
if errorlevel 1 (
    echo.
    echo TESTS FAILED!
    pause
    exit /b 1
)

echo.
echo ========================================
echo Build successful!
echo ========================================
echo.
echo Output location: src\SteamSwitcher\bin\Release\net8.0-windows10.0.19041.0\
echo.

REM Ask if user wants to publish as single file
set /p PUBLISH="Do you want to publish as a single executable? (Y/N): "
if /i "%PUBLISH%"=="Y" (
    echo.
    echo Publishing single file executable...
    dotnet publish src\SteamSwitcher\SteamSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish_zip
    
    if errorlevel 1 (
        echo.
        echo PUBLISH FAILED!
        pause
        exit /b 1
    )
    
    echo.
    echo ========================================
    echo Published successfully!
    echo ========================================
    echo Output: publish_zip\SteamSwitch.exe
)

echo.
pause
