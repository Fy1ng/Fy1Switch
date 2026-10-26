; -*- coding: utf-8 -*-
Unicode true
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!ifndef PAYLOAD_DIR
!error "Use tools/Package-Windows.ps1 to build this installer."
!endif
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Fy1Switch"

Name "Fy1Switch"
OutFile "${SETUP_OUT}"
InstallDir "$LOCALAPPDATA\Programs\Fy1Switch"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
VIProductVersion "${PRODUCT_VERSION_QUAD}"
VIAddVersionKey "ProductName" "Fy1Switch"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "FileDescription" "Fy1Switch per-user installer"
VIAddVersionKey "LegalCopyright" "See bundled notices and licenses."

!define MUI_ICON "${PRODUCT_ICON}"
!define MUI_UNICON "${PRODUCT_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "此安装程序将为当前用户安装 Fy1Switch.$\r$\n$\r$\n安装前请退出 Fy1Switch. 安装不会自动运行程序或开启登录自启. 卸载保留用户数据."
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateInstallDirectory
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Var PowerShell
Var InstallParent

!macro InitializeShell
    SetShellVarContext current
    SetRegView 64
    ${IfNot} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "Fy1Switch 需要 64 位 Windows." /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    StrCpy $PowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
    IfFileExists "$PowerShell" +2 0
        StrCpy $PowerShell "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe"
!macroend

Function .onInit
    !insertmacro InitializeShell
FunctionEnd

Function un.onInit
    !insertmacro InitializeShell
FunctionEnd

Function ValidateInstallDirectory
    ; NSIS GetFullPathName fails for a directory that does not exist yet.
    System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
    IntCmp $1 0 invalid_directory invalid_directory
    IntCmp $1 ${NSIS_MAX_STRLEN} invalid_directory 0 invalid_directory
    StrCpy $INSTDIR $0
    ${GetFileName} "$INSTDIR" $0
    StrCmp $0 "Fy1Switch" 0 invalid_directory
    ${GetParent} "$INSTDIR" $InstallParent
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    StrCmp $0 "" valid_directory
    StrCmp $0 "$INSTDIR" valid_directory
    MessageBox MB_OK|MB_ICONSTOP "已有安装位于 $0. 请选择该目录升级, 或先卸载原安装." /SD IDOK
    SetErrorLevel 1
    Abort
invalid_directory:
    MessageBox MB_OK|MB_ICONSTOP "安装目录的最后一级必须命名为 Fy1Switch." /SD IDOK
    SetErrorLevel 1
    Abort
valid_directory:
FunctionEnd

Section "Fy1Switch" MainSection
    Call ValidateInstallDirectory
    InitPluginsDir
    SetOutPath "$PLUGINSDIR\payload"
    File /r "${PAYLOAD_DIR}\*"
    ; The existing script owns process, marker, path and shortcut validation.
    nsExec::ExecToStack '$\"$PowerShell$\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File $\"$PLUGINSDIR\payload\Install.ps1$\" -InstallRoot $\"$InstallParent\.$\"'
    Pop $0
    Pop $1
    DetailPrint "$1"
    StrCmp $0 "0" installed
    MessageBox MB_OK|MB_ICONSTOP "安装未完成. 请退出 Fy1Switch 并确认目标路径可写.$\r$\n$1" /SD IDOK
    SetErrorLevel 1
    Abort
installed:
    ClearErrors
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Fy1Switch"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Fy1Switch.Host.exe,0"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${PAYLOAD_SIZE_KB}
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
    IfErrors registration_failed
    SetErrorLevel 0
    Goto done
registration_failed:
    MessageBox MB_OK|MB_ICONSTOP "程序文件已复制, 但卸载入口注册失败. 可在安装目录使用 Uninstall.ps1 卸载." /SD IDOK
    SetErrorLevel 1
    Abort
done:
SectionEnd

Section "Uninstall"
    ; NSIS runs its uninstaller from a temporary copy. Keep the working directory outside the target.
    SetOutPath "$TEMP"
    nsExec::ExecToStack '$\"$PowerShell$\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File $\"$INSTDIR\Uninstall.ps1$\"'
    Pop $0
    Pop $1
    DetailPrint "$1"
    StrCmp $0 "0" uninstalled
    MessageBox MB_OK|MB_ICONSTOP "卸载未完成. 请退出 Fy1Switch 后重试.$\r$\n$1" /SD IDOK
    SetErrorLevel 1
    Abort
uninstalled:
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    StrCmp $0 "$INSTDIR" 0 done
    ClearErrors
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    IfErrors registration_failed
done:
    SetErrorLevel 0
    Goto end
registration_failed:
    MessageBox MB_OK|MB_ICONSTOP "程序文件已移除, 但无法移除当前用户的卸载入口." /SD IDOK
    SetErrorLevel 1
    Abort
end:
SectionEnd
