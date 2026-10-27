@echo off
rem Build BatteryTray.exe with the .NET Framework compiler shipped with Windows.
rem Double-click this file, or run it from a command prompt.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"

if not exist "%CSC%" (
  echo [ERROR] csc.exe not found: %CSC%
  echo Install .NET Framework 4.x and try again.
  pause
  exit /b 1
)

echo Building BatteryTray.exe ...
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:BatteryTray.exe ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /reference:"%FW%\System.Management.dll" ^
  /reference:"lib\winrt\Windows.WinMD" ^
  /reference:"lib\winrt\Windows.Foundation.UniversalApiContract.winmd" ^
  /reference:"lib\winrt\Windows.Foundation.FoundationContract.winmd" ^
  /reference:"%FW%\System.Runtime.dll" ^
  /reference:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  /reference:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  src\*.cs

if errorlevel 1 (
  echo.
  echo [FAILED] Build failed, see errors above.
  pause
  exit /b 1
)

echo.
echo [OK] Build finished:
for %%F in (BatteryTray.exe) do echo      %%~fF  ^(%%~zF bytes^)
echo.
echo Run BatteryTray.exe to start. The tray icon appears in the notification area.
pause
