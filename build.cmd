@echo off
REM Builds Ukti with the C# compiler that ships with Windows, so no SDK or
REM Visual Studio is required.

setlocal
set SRC=%~dp0src
set OUT=%~dp0app
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler at:
  echo   %CSC%
  echo Install .NET Framework 4.8 and try again.
  exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"

echo Building Ukti...
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:"%OUT%\Ukti.exe" ^
  /win32icon:"%SRC%\assets\ukti.ico" ^
  /resource:"%SRC%\assets\ukti-glyph.png",ukti-glyph.png ^
  "%SRC%\Program.cs" ^
  "%SRC%\NemoAsr.cs" ^
  "%SRC%\AudioCapture.cs" ^
  "%SRC%\AudioPipeline.cs" ^
  "%SRC%\TextInjector.cs" ^
  "%SRC%\PillForm.cs" ^
  "%SRC%\DotScope.cs" ^
  "%SRC%\Theme.cs" ^
  "%SRC%\Memory.cs" ^
  "%SRC%\Settings.cs" ^
  "%SRC%\SettingsForm.cs" ^
  "%SRC%\ModelSetupForm.cs" ^
  "%SRC%\Startup.cs" ^
  "%SRC%\Ipc.cs" ^
  "%SRC%\HoldHotkey.cs" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll

if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo Built %OUT%\Ukti.exe
echo.
echo Next: run scripts\fetch-runtime.ps1 to place the speech runtime
echo beside the executable, then start Ukti.exe. The model downloads itself.
