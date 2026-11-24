@echo off
setlocal
rem Works next to the exe (distributed folder) and from the solution root (after Publish.bat).
set "APP=%~dp0UserRemoveTool.exe"
if not exist "%APP%" set "APP=%~dp0publish\UserRemoveTool.exe"
if not exist "%APP%" (
    echo UserRemoveTool.exe was not found. Run Publish.bat first.
    pause
    exit /b 1
)
start "" "%APP%"
