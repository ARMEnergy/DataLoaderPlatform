@echo off
cd /d "%~dp0src\DataLoader.Host\bin\Release\net8.0"
DataLoader.Host.exe IIR
echo Exit code: %ERRORLEVEL%
pause