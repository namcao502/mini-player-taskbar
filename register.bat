@echo off
rem Registers the deskband for COM. Self-elevates to admin (RegAsm writes HKCR/HKLM).
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
"%REGASM%" /codebase "%~dp0bin\Release\net48\MiniPlayerBand.dll"
echo.
echo Done. Right-click the taskbar -^> Toolbars -^> Mini Player to show it.
echo (If it does not appear, restart Explorer or sign out and back in.)
pause
