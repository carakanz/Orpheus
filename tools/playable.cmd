@echo off
rem Orpheus playable wrapper for cmd: runs tools\playable.ps1 (interim build: server + bots + frames).
rem ASCII only, see header of tools\ci.cmd.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0playable.ps1" %*
exit /b %ERRORLEVEL%
