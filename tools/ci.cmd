@echo off
rem Обёртка CI для cmd: tools\ci.cmd (и перенаправление tools\ci.cmd > log.txt 2>&1 даёт UTF-8).
rem Параметры ci.ps1 передаются как есть: tools\ci.cmd -TimeLimitSec 120 -LogFile ci.log
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ci.ps1" %*
exit /b %ERRORLEVEL%
