@echo off
cd /d "%~dp0src\DataLoader.Host\bin\Release\net8.0"
DataLoader.Host.exe ModernCommodities
echo Exit code: %ERRORLEVEL%
pause