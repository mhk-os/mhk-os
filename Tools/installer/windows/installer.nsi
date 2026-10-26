; Windows installer of one desktop build, made by Tools/installer/package.sh (makensis defines SOURCE, OUTFILE, ICON,
; ARCH x64 or arm64, BUILD, SHA7, ENTRIES). Installs per user without admin rights, so players can edit the loose
; files in Mods\<id>\Assets. Registers the mhkos:// URL scheme (room links mhkos://join/CODE) for the current user. The
; uninstaller removes the build's files, the scheme and the folder when nothing else is left in it, never the player's data
; in AppData\LocalLow.
Unicode true
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include MUI2.nsh
!include LogicLib.nsh
!include x64.nsh
!include FileFunc.nsh

!define NAME "Moorhuhn Kart OSE"
!define EXE "MoorhuhnKart.exe"
!define ICONFILE "MoorhuhnKart.ico"
!define UNINSTALLER "Uninstall.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\MoorhuhnKartOSE"
!define SCHEME_KEY "Software\Classes\mhkos"

Name "${NAME}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${NAME}"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
BrandingText "Build ${BUILD} | ${SHA7}"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_SHOWREADME ""
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Create a desktop shortcut"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION DesktopShortcut

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  !if "${ARCH}" == "arm64"
    ${IfNot} ${IsNativeARM64}
      MessageBox MB_ICONSTOP "This is the Windows on Arm build. This PC needs the x64 installer." /SD IDOK
      Abort
    ${EndIf}
  !else
    ${IfNot} ${RunningX64}
      MessageBox MB_ICONSTOP "${NAME} needs 64-bit Windows." /SD IDOK
      Abort
    ${EndIf}
  !endif
FunctionEnd

Function DesktopShortcut
  CreateShortcut "$DESKTOP\${NAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${ICONFILE}"
FunctionEnd

Section "Install"
  ; An update first runs the installed uninstaller in place, so files the new build no longer has do not linger.
  ${If} ${FileExists} "$INSTDIR\${UNINSTALLER}"
    ExecWait '"$INSTDIR\${UNINSTALLER}" /S _?=$INSTDIR'
  ${EndIf}
  SetOutPath "$INSTDIR"
  File /r /x "*_DoNotShip" /x "*_ButDontShipItWithYourGame" "${SOURCE}\*.*"
  File "/oname=${ICONFILE}" "${ICON}"
  WriteUninstaller "$INSTDIR\${UNINSTALLER}"
  CreateShortcut "$SMPROGRAMS\${NAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${ICONFILE}"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "Build ${BUILD} | ${SHA7}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "MHK Open Source"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/mhk-os/mhk-os"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${ICONFILE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALLER}"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALLER}" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ; Room links: Windows starts the game with the link as its argument (a running game takes it over, JoinLinkForwarder).
  WriteRegStr HKCU "${SCHEME_KEY}" "" "URL:Moorhuhn Kart OSE room link"
  WriteRegStr HKCU "${SCHEME_KEY}" "URL Protocol" ""
  WriteRegStr HKCU "${SCHEME_KEY}\DefaultIcon" "" '"$INSTDIR\${ICONFILE}"'
  WriteRegStr HKCU "${SCHEME_KEY}\shell\open\command" "" '"$INSTDIR\${EXE}" "%1"'
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0
SectionEnd

Section "Uninstall"
  ; RMDir /r and Delete lines for the build's top-level entries and Mods\<id> folders, written by package.sh.
  !include "${ENTRIES}"
  Delete "$INSTDIR\${ICONFILE}"
  Delete "$INSTDIR\${UNINSTALLER}"
  RMDir "$INSTDIR\Mods"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${NAME}.lnk"
  Delete "$DESKTOP\${NAME}.lnk"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ; Only the scheme this install registered: another install may have taken it over since.
  ReadRegStr $0 HKCU "${SCHEME_KEY}\shell\open\command" ""
  ${If} $0 == '"$INSTDIR\${EXE}" "%1"'
    DeleteRegKey HKCU "${SCHEME_KEY}"
  ${EndIf}
SectionEnd
