@echo off
rem Orpheus opencode watchdog in a visible console: checks every 10 minutes; close the window to stop it.
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\carak\source\Orpheus\tools\watchdog_console.ps1"
