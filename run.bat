@echo off
cd /d "%~dp0src\NewsletterGenerator"

if not defined CopilotNpmRegistryUrl (
    where npm >nul 2>&1
    if not errorlevel 1 (
        for /f "delims=" %%R in ('npm config get registry 2^>nul') do set "CopilotNpmRegistryUrl=%%R"
    )
)

echo Building newsletter generator...
dotnet build
if %ERRORLEVEL% NEQ 0 (
    echo Build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Running newsletter generator...
dotnet run --no-build
pause
