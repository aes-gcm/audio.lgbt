Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

Name "audio.lgbt"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\audio.lgbt"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "audio.lgbt"
Icon "../AudioLgbt.Native/Assets/audio.lgbt.ico"
UninstallIcon "../AudioLgbt.Native/Assets/audio.lgbt.ico"
VIProductVersion "0.3.0.0"
VIAddVersionKey /LANG=1049 "ProductName" "audio.lgbt"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка audio.lgbt — кандидат для проверки"
VIAddVersionKey /LANG=1049 "FileVersion" "0.3.0-preview.7"
VIAddVersionKey /LANG=1049 "LegalCopyright" "audio.lgbt"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\audio.lgbt.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Открыть audio.lgbt"
!define MUI_FINISHPAGE_TITLE "audio.lgbt установлено"
!define MUI_FINISHPAGE_TEXT "Можно создать комнату или подключиться по приглашению."
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\audio.lgbt"

Function .onInit
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Для audio.lgbt нужна Windows 10 или новее."
    Quit
  ${EndIf}
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_OK|MB_ICONSTOP "Этот установщик предназначен для Windows x64 (Intel/AMD)."
    Quit
  ${EndIf}
  SetShellVarContext current
  SetRegView 32
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
  check_running:
  FindWindow $0 "" "audio.lgbt"
  ${If} $0 != 0
    IfSilent 0 +3
    SetErrorLevel 2
    Quit
    MessageBox MB_RETRYCANCEL|MB_ICONINFORMATION "Закройте audio.lgbt, чтобы установить обновление. Настройки сохранятся." IDRETRY check_running
    Abort
  ${EndIf}
FunctionEnd

Section "audio.lgbt"
  !include "${PAYLOAD}"
  SetOutPath "$INSTDIR"
  File "audio-lgbt-network.ps1"
  DetailPrint "Настройка сетевого разрешения audio.lgbt…"
  ${GetParameters} $2
  ClearErrors
  ${GetOptions} $2 "/UPDATE" $3
  ${IfNot} ${Errors}
    Goto network_done
  ${EndIf}
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\audio-lgbt-network.ps1" -Mode Add -EnginePath "$INSTDIR\audio-engine.exe"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    DetailPrint "Сетевое разрешение не настроено. Windows может запросить его при первом звонке."
  ${EndIf}
  network_done:
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\audio.lgbt.lnk" "$INSTDIR\audio.lgbt.exe"
  CreateShortcut "$DESKTOP\audio.lgbt.lnk" "$INSTDIR\audio.lgbt.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "audio.lgbt"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "0.3.0-preview.7"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "audio.lgbt"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\audio.lgbt.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://audio.lgbt/download"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 32
  uninstall_retry:
  FindWindow $0 "" "audio.lgbt"
  ${If} $0 != 0
    MessageBox MB_RETRYCANCEL|MB_ICONINFORMATION "Закройте audio.lgbt и повторите удаление." IDRETRY uninstall_retry
    Abort
  ${EndIf}
  ClearErrors
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\audio-lgbt-network.ps1" -Mode Remove -EnginePath "$INSTDIR\audio-engine.exe"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    DetailPrint "Не удалось убрать сетевое разрешение audio.lgbt."
  ${EndIf}
  ClearErrors
  !include "${REMOVE}"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONEXCLAMATION "Не все файлы удалены. Закройте audio.lgbt и повторите удаление."
    Abort
  ${EndIf}
  Delete "$SMPROGRAMS\audio.lgbt.lnk"
  Delete "$DESKTOP\audio.lgbt.lnk"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\audio-lgbt-network.ps1"
  RMDir "$INSTDIR"
  ; Keep preferences and the old WebView profile: these are user data.
SectionEnd
