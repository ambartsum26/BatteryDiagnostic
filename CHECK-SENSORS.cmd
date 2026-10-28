@echo off
cd /d "%~dp0"
start "DevicePulse status" /wait "%~dp0DevicePulse.exe" --pawnio-status
start "DevicePulse sensors" /wait "%~dp0DevicePulse.exe" --performance-probe
echo Reports: pawnio-status.json and performance-probe.json
pause
