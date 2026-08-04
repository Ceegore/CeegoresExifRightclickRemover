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
REM
REM D68 (M2.20.17): every reg add now goes through the :RegAdd helper which
REM surfaces the error and aborts the install on the first failure. The pre-fix
REM pattern (`reg add ... >nul 2>&1`) silently swallowed any error from
REM permissions, registry locks, or AV interference — the user saw "Done." even
REM when half the keys were missing. A partial install is a bug, not a state.
REM ============================================================
:install
echo.
echo Installing ExifRemover context-menu entries for .jpg, .jpeg, and .png
echo Executable: %EXE%
echo.

REM (1) Application registration (Win 11 modern menu reads from Applications\<exe>\shell)
echo Registering application shell verb ...
call :RegAdd "HKCU\Software\Classes\Applications\ExifRemover.exe" /ve /d "ExifRemover" /f
call :RegAdd "HKCU\Software\Classes\Applications\ExifRemover.exe\shell\ExifRemove" /ve /d "%VERB%" /f
call :RegAdd "HKCU\Software\Classes\Applications\ExifRemover.exe\shell\ExifRemove" /v "Icon" /d "%ICON%" /f
call :RegAdd "HKCU\Software\Classes\Applications\ExifRemover.exe\shell\ExifRemove\command" /ve /d "\"%EXE%\" \"%%1\"" /f

REM (2) Image-class entry (covers all image file types via SystemFileAssociations)
echo Registering under image class ...
call :RegAdd "HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove" /ve /d "%VERB%" /f
call :RegAdd "HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove" /v "Icon" /d "%ICON%" /f
call :RegAdd "HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove\command" /ve /d "\"%EXE%\" \"%%1\"" /f

REM (3) Per-extension entries (legacy "Show more options" menu)
for %%E in (.jpg .jpeg .png) do (
    echo Registering %%E ...
    call :RegAdd "HKCU\Software\Classes\SystemFileAssociations\%%E\shell\ExifRemove" /ve /d "%VERB%" /f
    call :RegAdd "HKCU\Software\Classes\SystemFileAssociations\%%E\shell\ExifRemove" /v "Icon" /d "%ICON%" /f
    call :RegAdd "HKCU\Software\Classes\SystemFileAssociations\%%E\shell\ExifRemove\command" /ve /d "\"%EXE%\" \"%%1\"" /f
)

echo.
echo Done. Right-click any image file in Explorer and look for '%VERB%'.
echo To uninstall later, run: .\install.cmd uninstall
echo.
exit /b 0

REM ============================================================
REM Internal: reg add wrapper that aborts the install on any failure.
REM Usage: call :RegAdd "<key>" [/v <name>] [/d <data>] [/f ...]
REM Replaces the silent-swallow pattern `reg add ... >nul 2>&1`.
REM
REM Note: ExifRemover registers per-user (HKCU) — no admin rights are
REM required. A failed reg add is almost always AV interference, a
REM running indexer, or another process holding the key. The error
REM message reflects that.
REM ============================================================
:RegAdd
reg add %* >nul
if errorlevel 1 (
    echo.
    echo ERROR: reg add failed for: %1
    reg add %*
    echo.
    echo Aborting install. A failed reg add is usually caused by antivirus
    echo interference, a Windows Search indexer, or another process holding
    echo the registry key. Re-run after the lock is released.
    exit /b 1
)
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
REM D70 (M2.20.19): the del/move/rmdir steps previously had no errorlevel checks.
REM If the user was running the previous ExifRemover.exe (or any sibling DLL
REM is locked by an AV scan / indexer / another process), the del would
REM fail, the move would partially complete, and the script would print
REM "Build complete" with a partial output. The user then ran the OLD exe
REM with no idea. Fix: errorlevel checks at every step + a final
REM existence check on ExifRemover.exe. The `findstr /v "File"` hack that
REM hid the "File Not Found" dir message is also removed — if the exe is
REM missing, the user needs to see it.
if exist "%ROOT%\ExifRemover.exe" (
    del /q "%ROOT%\ExifRemover.exe"
    if errorlevel 1 (
        echo.
        echo ERROR: Could not delete the previous ExifRemover.exe.
        echo It may be in use (close ExifRemover if it's running) or
        echo locked by an antivirus scan. Re-run after the lock is released.
        rmdir /s /q "%STAGE%" 2>nul
        exit /b 1
    )
)
for %%F in ("%STAGE%\*") do (
    move /y "%%F" "%ROOT%\" >nul
    if errorlevel 1 (
        echo.
        echo ERROR: Could not move file: %%F
        echo A file in the target folder may be locked (AV / indexer / running
        echo process). Re-run after the lock is released.
        rmdir /s /q "%STAGE%" 2>nul
        exit /b 1
    )
)
rmdir /s /q "%STAGE%"
if errorlevel 1 (
    echo.
    echo ERROR: Could not delete the staging folder. Stale files may remain.
    exit /b 1
)

REM Final sanity check: the build output must contain ExifRemover.exe.
REM If it's not there, the build silently failed somewhere and the user
REM needs to know. The pre-fix dir|findstr hack hid this exact case.
if not exist "%ROOT%\ExifRemover.exe" (
    echo.
    echo ERROR: Build did not produce ExifRemover.exe in %ROOT%.
    echo Check the publish output above for errors.
    exit /b 1
)

echo.
echo === Build complete: %EXE% ===
dir /b "%ROOT%\ExifRemover.exe" "%ROOT%\*.dll"
echo.
exit /b 0

endlocal