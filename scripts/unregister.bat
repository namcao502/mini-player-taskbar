@echo off
rem Unregisters the deskband. Self-elevates to admin.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
for %%I in ("%~dp0..\bin\Release\net48\MiniPlayerBand.dll") do set "DLL=%%~fI"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /unregister "%DLL%"
pause
