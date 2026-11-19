; Build with scripts/package-exe.ps1. No model or user preferences are embedded.
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
Var RuntimeDir

!ifndef VERSION
  !error "Use scripts/package-exe.ps1 to supply the version and staging directory."
!endif
Name "Ukti"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Ukti"
InstallDirRegKey HKCU "Software\Ukti" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
SetDatablockOptimize on
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "Ukti"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}.0"
VIAddVersionKey /LANG=1033 "FileDescription" "Ukti Windows x64 installer"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (c) 2026 Deviprasad Shetty"

!define MUI_ABORTWARNING
!define MUI_ICON "${PROJECT_ROOT}\src\assets\ukti.ico"
!define MUI_UNICON "${PROJECT_ROOT}\src\assets\ukti.ico"
!define MUI_WELCOMEPAGE_TITLE "Install Ukti"
!ifdef OFFLINE
!define MUI_WELCOMEPAGE_TEXT "Local hold-to-talk dictation for Windows.$\r$\n$\r$\nRequires Windows 10/11 x64, .NET Framework 4.8, and a compatible NVIDIA GPU with an up-to-date driver. Intel/AMD-only laptops are not supported.$\r$\n$\r$\nThe app and speech runtime are included. Choose and download a model on first launch."
!else
!define MUI_WELCOMEPAGE_TEXT "Local hold-to-talk dictation for Windows.$\r$\n$\r$\nRequires Windows 10/11 x64, .NET Framework 4.8, and a compatible NVIDIA GPU with an up-to-date driver. Intel/AMD-only laptops are not supported.$\r$\n$\r$\nThis small installer downloads and verifies the NVIDIA speech runtime (about 485 MiB). Internet and about 2 GB free setup space are required. Choose and download a model separately on first launch."
!endif
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${STAGE}\payload\LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Ukti.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open Ukti and choose a speech model"
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "Remove Ukti and its speech runtime? Downloaded models and preferences are kept in %LOCALAPPDATA%\Ukti for a future reinstall. To reclaim that space too, delete that folder manually after uninstalling."
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function CheckNotRunning
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Ukti.SingleInstance") p .r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "Please quit Ukti from its system-tray menu before installing or updating it." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
FunctionEnd
Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Ukti requires 64-bit Windows." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Ukti requires Windows 10 or Windows 11." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    MessageBox MB_OK|MB_ICONSTOP "Ukti requires .NET Framework 4.8. Install it through Windows Update or from https://dotnet.microsoft.com/download/dotnet-framework/net48, then run this installer again." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  Call CheckNotRunning
  ${GetParameters} $0
  ${GetOptions} $0 "/RUNTIME_DIR=" $RuntimeDir
FunctionEnd

Section "Ukti and speech runtime (required)" Main
  SectionIn RO
  !ifndef OFFLINE
    AddSize ${RUNTIME_KB}
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=Install-Runtime.ps1 "${PROJECT_ROOT}\packaging\Install-Runtime.ps1"
    File /oname=runtime-manifest.json "${STAGE}\payload\runtime-manifest.json"
    ${If} $RuntimeDir == ""
      StrCpy $RuntimeDir "$INSTDIR"
    ${EndIf}
    runtime_retry:
    DetailPrint "Preparing the verified NVIDIA runtime before installing application files..."
    nsExec::ExecToLog /TIMEOUT=600000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Install-Runtime.ps1" -Manifest "$PLUGINSDIR\runtime-manifest.json" -Destination "$PLUGINSDIR\runtime" -CacheDirectory "$LOCALAPPDATA\Ukti\downloads" -ExistingDirectory "$RuntimeDir"'
    Pop $0
    ${If} $0 != 0
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "The speech runtime could not be downloaded or verified. Check the details above, your Internet connection/proxy and disk space. Retry resumes an interrupted download. No application files have been replaced." /SD IDCANCEL IDRETRY runtime_retry
      SetErrorLevel 4
      Abort
    ${EndIf}
  !endif
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${STAGE}\payload\*"
  !ifndef OFFLINE
    ClearErrors
    CopyFiles /SILENT "$PLUGINSDIR\runtime\*.dll" "$INSTDIR"
    IfErrors runtime_copy_failed runtime_copy_ok
    runtime_copy_failed:
      MessageBox MB_OK|MB_ICONSTOP "Could not copy the verified speech runtime. Check free disk space and quit any running Ukti copy, then retry." /SD IDOK
      SetErrorLevel 5
      Abort
    runtime_copy_ok:
    Delete "$LOCALAPPDATA\Ukti\downloads\nemo-speech-0.3.0-windows-x86_64-cuda.zip"
    RMDir "$LOCALAPPDATA\Ukti\downloads"
  !endif
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Ukti" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "DisplayName" "Ukti"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "Publisher" "Deviprasad Shetty"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "DisplayIcon" "$INSTDIR\Ukti.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "NoRepair" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti" "EstimatedSize" ${PAYLOAD_KB}
  CreateDirectory "$SMPROGRAMS\Ukti"
  CreateShortcut "$SMPROGRAMS\Ukti\Ukti.lnk" "$INSTDIR\Ukti.exe" "" "$INSTDIR\Ukti.exe"
  CreateShortcut "$SMPROGRAMS\Ukti\Uninstall Ukti.lnk" "$INSTDIR\Uninstall.exe"
  ; Preserve an existing opt-in to sign-in launch, but do not enable it by default.
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Ukti"
  ${If} $0 != ""
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Ukti" '$\"$INSTDIR\Ukti.exe$\" --background'
  ${EndIf}
SectionEnd
Function un.onInit
  SetShellVarContext current
  SetRegView 64
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Ukti.SingleInstance") p .r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "Quit Ukti from the system tray before uninstalling it." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
FunctionEnd
Section "Uninstall"
  ; Only remove files owned by this installer, never recursively erase a custom
  ; installation directory that might contain unrelated files.
  !include "${STAGE}\uninstall-files.nsh"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Ukti"
  ${If} $0 == '$\"$INSTDIR\Ukti.exe$\" --background'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Ukti"
  ${EndIf}
  ReadRegStr $0 HKCU "Software\Ukti" "InstallDir"
  ${If} $0 == "$INSTDIR"
    Delete "$SMPROGRAMS\Ukti\Ukti.lnk"
    Delete "$SMPROGRAMS\Ukti\Uninstall Ukti.lnk"
    RMDir "$SMPROGRAMS\Ukti"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti"
    DeleteRegKey HKCU "Software\Ukti"
  ${EndIf}
SectionEnd
