@echo off
cd /d "%~dp0src\NewsletterGenerator"
echo Building newsletter generator...
dotnet build
if %ERRORLEVEL% NEQ 0 (
    echo Build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Running newsletter generator...
dotnet run
pause
