@echo off
rem Orpheus CI wrapper for cmd: runs tools\ci.ps1 with the same parameters.
rem ASCII only: cmd reads this file in the OEM code page before chcp, so UTF-8 text here
rem breaks into stray commands (that is how log.txt used to appear). Docs: header of ci.ps1.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ci.ps1" %*
exit /b %ERRORLEVEL%
