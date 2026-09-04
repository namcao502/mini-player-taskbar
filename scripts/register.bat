@echo off
rem Registers the deskband for COM. Self-elevates to admin (RegAsm writes HKCR/HKLM).
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
rem Resolve to a full path first: RegAsm writes whatever it is handed into the
rem registry codebase, and a literal "scripts\.." in there is fragile.
for %%I in ("%~dp0..\bin\Release\net48\MiniPlayerBand.dll") do set "DLL=%%~fI"
"%REGASM%" /codebase "%DLL%"
echo.
echo Done. Right-click the taskbar -^> Toolbars -^> Mini Player to show it.
echo (If it does not appear, restart Explorer or sign out and back in.)
pause
