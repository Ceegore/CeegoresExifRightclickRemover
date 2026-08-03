@echo off
setlocal EnableDelayedExpansion

REM ============================================================
REM ExifRemover installer (single-file entry point)
REM
REM Usage:
REM     .\install.cmd            Build (if needed) and install.
REM     .\install.cmd build      Build the release folder only.
REM     .\install.cmd uninstall  Remove the context-menu entries.
REM     .\install.cmd help       Show this help.
REM
REM Notes:
REM   - The .\ prefix is required in Windows PowerShell because
REM     PowerShell does not search the current directory for
REM     executables by default. cmd.exe also accepts .\install.cmd.
REM   - All operations are per-user (HKCU) and require no admin.
REM   - The install/uninstall are safe to run multiple times.
REM ============================================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "EXE=%ROOT%\ExifRemover.exe"
set "ICON=%EXE%,0"
set "VERB=Remove EXIF metadata"

REM ---- dispatch on first arg ---------------------------------
if /i "%~1"=="help" goto :help
if /i "%~1"=="/?"   goto :help
if /i "%~1"=="-h"  goto :help
if /i "%~1"=="uninstall" goto :uninstall
if /i "%~1"=="remove"   goto :uninstall
if /i "%~1"=="build"    goto :build_only
goto :build_and_install

:help
echo.
echo ExifRemover installer
echo.
echo Usage:
echo     .\install.cmd            Build (if needed) and install.
echo     .\install.cmd build      Build the release folder only.
echo     .\install.cmd uninstall  Remove the context-menu entries.
echo     .\install.cmd help       Show this help.
echo.
echo After a successful build, this folder contains:
echo     ExifRemover.exe + ~70MB of DLLs (self-contained, no .NET install needed)
echo     install.cmd             - this script
echo     uninstall.cmd           - one-shot removal helper
echo.
echo The right-click menu entries point at ExifRemover.exe in THIS folder.
echo Move the whole folder together if you want to redistribute it.
echo.
exit /b 0

REM ============================================================
REM Build the release folder
REM ============================================================
:build_only
call :do_build
exit /b %ERRORLEVEL%

:build_and_install
if not exist "%EXE%" call :do_build || exit /b 1
goto :install

REM ============================================================
REM Remove the context-menu entries (idempotent)
REM ============================================================
:uninstall
echo.
echo Removing ExifRemover context-menu entries ...
REM Application registration under HKCU\Software\Classes\Applications\ExifRemover.exe
reg delete "HKCU\Software\Classes\Applications\ExifRemover.exe" /f >nul 2>&1
REM Per-extension entries under SystemFileAssociations (used by legacy "Show more options" menu)
for %%E in (.jpg .jpeg .png) do (
    reg delete "HKCU\Software\Classes\SystemFileAssociations\%%E\shell\ExifRemove" /f >nul 2>&1
)
REM Image-class entry (used by Win 11 modern context menu for any image)
reg delete "HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove" /f >nul 2>&1
echo.
echo Done. ExifRemover has been uninstalled.
echo.
exit /b 0

REM ============================================================
REM Install the context-menu entries
REM
REM Win 11 reads context-menu entries from multiple places. To show up in BOTH
REM the legacy "Show more options" menu AND the modern default menu, we register
REM in three places:
REM   1. HKCU\Software\Classes\Applications\ExifRemover.exe\shell\ExifRemove
REM      - This is the canonical "Application shell verb" location. Win 11's modern
REM        context menu scans this for any exe registered as an Application.
REM   2. HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove
REM      - Special "image" key covers all image file types via SystemFileAssociations.
REM        Shown in modern menu when any image file is right-clicked.
REM   3. HKCU\Software\Classes\SystemFileAssociations\.<ext>\shell\ExifRemove
REM      - Per-extension entries for .jpg, .jpeg, .png. Shown in legacy menu
REM        (Show more options) reliably.
REM ============================================================
:install
echo.
echo Installing ExifRemover context-menu entries for .jpg, .jpeg, and .png
echo Executable: %EXE%
echo.

REM (1) Application registration (Win 11 modern menu reads from Applications\<exe>\shell)
echo Registering application shell verb ...
set "CMD_EXE=\"%EXE%\""
set "CMD_PCT=\"%%1\""
reg add "HKCU\Software\Classes\Applications\ExifRemover.exe" /ve /d "ExifRemover" /f >nul
reg add "HKCU\Software\Classes\Applications\ExifRemover.exe\shell\ExifRemove" /ve /d "%VERB%" /f >nul
reg add "HKCU\Software\Classes\Applications\ExifRemover.exe\shell\ExifRemove" /v "Icon" /d "%ICON%" /f >nul
reg add "HKCU\Software\Classes\Applications\ExifRemover.exe\shell\ExifRemove\command" /ve /d "\"%EXE%\" \"%%1\"" /f >nul

REM (2) Image-class entry (covers all image file types via SystemFileAssociations)
echo Registering under image class ...
reg add "HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove" /ve /d "%VERB%" /f >nul
reg add "HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove" /v "Icon" /d "%ICON%" /f >nul
reg add "HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove\command" /ve /d "\"%EXE%\" \"%%1\"" /f >nul

REM (3) Per-extension entries (legacy "Show more options" menu)
for %%E in (.jpg .jpeg .png) do (
    echo Registering %%E ...
    reg add "HKCU\Software\Classes\SystemFileAssociations\%%E\shell\ExifRemove" /ve /d "%VERB%" /f >nul
    reg add "HKCU\Software\Classes\SystemFileAssociations\%%E\shell\ExifRemove" /v "Icon" /d "%ICON%" /f >nul
    reg add "HKCU\Software\Classes\SystemFileAssociations\%%E\shell\ExifRemove\command" /ve /d "\"%EXE%\" \"%%1\"" /f >nul
)

echo.
echo Done. Right-click any image file in Explorer and look for '%VERB%'.
echo To uninstall later, run: .\install.cmd uninstall
echo.
exit /b 0

REM ============================================================
REM Internal: build the release folder with dotnet publish
REM ============================================================
:do_build
echo.
echo === Building ExifRemover release folder ===
echo.

set "CSPROJ=%ROOT%\src\ExifRemover.App\ExifRemover.App.csproj"
if not exist "%CSPROJ%" (
    echo ERROR: Cannot find %CSPROJ%
    echo This script must be run from a full source checkout.
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: 'dotnet' is not on PATH.
    echo Install the .NET 8 SDK from https://dot.net and try again.
    exit /b 1
)

REM Build into a staging folder, then copy the resulting exe + DLLs to the
REM install location. We use a non-single-file publish so the SDK bug where
REM PublishSingleFile produces an empty binary doesn't affect us.
set "STAGE=%ROOT%\stage"
if exist "%STAGE%" rmdir /s /q "%STAGE%"

dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true -o "%STAGE%" -nologo -v:m
if errorlevel 1 (
    echo.
    echo Publish failed. See output above.
    exit /b 1
)

REM Move the build output next to install.cmd.
if exist "%ROOT%\ExifRemover.exe" del /q "%ROOT%\ExifRemover.exe"
for %%F in ("%STAGE%\*") do move /y "%%F" "%ROOT%\" >nul
rmdir /s /q "%STAGE%"

echo.
echo === Build complete: %EXE% ===
dir /b "%ROOT%\ExifRemover.exe" "%ROOT%\*.dll" 2>nul | findstr /v "^$" | findstr /v "File"
echo.
exit /b 0

endlocal