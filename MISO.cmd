@echo off
echo WARNING: MISO.cmd runs DataLoader.Host with the appsettings.json connection strings as built:
echo          core.LoadLog on ARMH-OPSDB01 ^(DataLoaderPlatform^) and the Misc database ^(miso schema^).
echo          For a local test, set a DEV override first, e.g.
echo            set DATALOADER_Loaders__MISO__ConnectionString=Server=^<dev^>;Database=Misc;Integrated Security=SSPI;TrustServerCertificate=True;
echo          and DATALOADER_Platform__LoadLogConnectionString to a DEV DataLoaderPlatform, or press Ctrl+C now.
pause
cd /d "%~dp0src\DataLoader.Host\bin\Release\net8.0"
DataLoader.Host.exe MISO
echo Exit code: %ERRORLEVEL%
pause