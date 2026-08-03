@echo off
REM ExifRemover uninstaller - one-shot helper for users who only need to remove.
REM Equivalent to: .\install.cmd uninstall
REM
REM Why we don't delegate to install.cmd: the previous version did `"%~dp0install.cmd" uninstall`,
REM which fails when the parent shell's CWD is not the install folder (the leading
REM ".\" is required by PowerShell to find the script, and a quoted absolute path is
REM actually fine, but the previous version also had a quoting bug that produced
REM confusing errors on a non-cmd console). Running the uninstall logic directly here
REM keeps the user-facing command robust regardless of how it's invoked.

setlocal EnableDelayedExpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

echo.
echo Removing ExifRemover context-menu entries ...
reg delete "HKCU\Software\Classes\Applications\ExifRemover.exe" /f >nul 2>&1
for %%E in (.jpg .jpeg .png) do (
    reg delete "HKCU\Software\Classes\SystemFileAssociations\%%E\shell\ExifRemove" /f >nul 2>&1
)
reg delete "HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove" /f >nul 2>&1
echo.
echo Done. ExifRemover has been uninstalled.
echo.
endlocal
exit /b 0