@echo off
setlocal
cd /d "%~dp0"
dotnet publish src\UserRemoveTool\UserRemoveTool.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=win-x64 -o publish
if errorlevel 1 (
    echo Publish failed.
    if not "%~1"=="nopause" pause
    exit /b 1
)
echo Done: %~dp0publish
if not "%~1"=="nopause" pause
