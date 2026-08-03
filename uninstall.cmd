@echo off
REM ExifRemover uninstaller - one-shot helper for users who only need to remove.
REM Equivalent to: .\install.cmd uninstall
REM Uses %~dp0 so it works regardless of the caller's current directory.
"%~dp0install.cmd" uninstall %*