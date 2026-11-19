@echo off
REM Builds Ukti with the .NET SDK. No Visual Studio installation required.
REM If the SDK was installed locally via dotnet-install.ps1, use that first.

setlocal
set DOTNET=
if exist "%USERPROFILE%\.dotnet\dotnet.exe" set DOTNET=%USERPROFILE%\.dotnet\dotnet.exe
if "%DOTNET%"=="" (
  where dotnet.exe >nul 2>&1
  if not errorlevel 1 set DOTNET=dotnet.exe
)
if "%DOTNET%"=="" (
  echo Could not find the .NET SDK.
  echo Install .NET 8 SDK from https://dotnet.microsoft.com/download
  exit /b 1
)

echo Building Ukti...
"%DOTNET%" build "%~dp0Ukti.csproj" -c Release -nologo
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo Built %~dp0app\Ukti.exe
echo.
echo Next: run scripts\fetch-runtime.ps1 to place the speech runtime
echo beside the executable, then start Ukti.exe. The model downloads itself.
