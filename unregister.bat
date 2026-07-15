@echo off
rem Unregisters the deskband. Self-elevates to admin.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /unregister "%~dp0bin\Release\net48\MiniPlayerBand.dll"
pause
