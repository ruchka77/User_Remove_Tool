@echo off
rem Starts the pre-built application. Nothing is built or published here.
rem Works from the solution root (exe in .\publish) and from inside the distributed folder (exe next to this file).
set "DIR=%~dp0publish"
if not exist "%DIR%\UserRemoveTool.exe" set "DIR=%~dp0"
if not exist "%DIR%\UserRemoveTool.exe" (
    echo UserRemoveTool.exe was not found next to Launch.bat or in the publish folder.
    pause
    exit /b 1
)
rem The working directory is the exe folder, so appsettings.json and relative paths resolve there.
start "" /d "%DIR%" "%DIR%\UserRemoveTool.exe"
