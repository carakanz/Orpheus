@echo off
rem Orpheus opencode watchdog OFF: removes the scheduled task.
schtasks /delete /tn "Orpheus opencode watchdog" /f
timeout /t 5
