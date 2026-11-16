Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect"
!ifndef VERSION
 !error "VERSION must be passed by scripts/build_windows_package.py"
!endif
!ifndef PAYLOAD
 !error "PAYLOAD must name the verified self-contained Windows payload"
!endif
!ifndef PREREQUISITES
 !error "PREREQUISITES must name the offline WebView2 prerequisites"
!endif
!ifndef WEBVIEW_SHA256
 !error "WEBVIEW_SHA256 must pin the bundled offline runtime"
!endif
!ifndef OUTPUT
 !error "OUTPUT must name the output installer"
!endif
Name "Leyra"
OutFile "${OUTPUT}"
InstallDir "$PROGRAMFILES64\Leyra"
RequestExecutionLevel admin
SetCompressor zlib
ShowInstDetails show
ShowUninstDetails show
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "将安装 Leyra 桌面程序、后台防护服务和所需的运行环境。$\r$\n$\r$\n安装完成后打开 Leyra，填写授权码即可激活使用。"
!insertmacro MUI_PAGE_WELCOME
!define MUI_DIRECTORYPAGE_TEXT_TOP "请选择 Leyra 的安装位置。建议使用默认位置；也可以装到其他磁盘，例如 D:\Leyra。"
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE CheckInstallDir
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "安装完成。请从桌面打开 Leyra。$\r$\n$\r$\n如需退出后台防护，请使用程序中的暂停功能；网络异常时可使用开始菜单中的网络恢复工具。"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
Var Result
; Each helper step's exit code and output go to %TEMP%\LEYRAConnect-setup.log, so a
; failed silent install (automatic update) can be diagnosed afterwards.
Function RunLogged
 nsExec::ExecToStack $1
 Pop $Result
 Pop $2
 FileOpen $9 "$TEMP\LEYRAConnect-setup.log" a
 FileSeek $9 0 END
 FileWrite $9 "[$0] exit=$Result$\r$\n$2$\r$\n"
 FileClose $9
FunctionEnd
Function .onInit
 ${IfNot} ${RunningX64}
  MessageBox MB_ICONSTOP "此程序需要 64 位 Windows 10 或 Windows 11。" /SD IDOK
  Abort
 ${EndIf}
 SetRegView 64
 InitPluginsDir
 ; Updates and reinstalls go back to the folder the user chose the first time.
 ReadRegStr $R0 HKLM "${UNINSTALL_KEY}" "InstallLocation"
 ${If} $R0 == ""
  ReadRegStr $R0 HKLM "${UNINSTALL_KEY}" "UninstallString"
  ${If} $R0 != ""
   StrCpy $R0 $R0 "" 1
   StrCpy $R0 $R0 -1
   ${GetParent} $R0 $R0
  ${EndIf}
 ${EndIf}
 ${If} $R0 != ""
  StrCpy $INSTDIR $R0
 ${EndIf}
FunctionEnd
; Runs when leaving the folder page, and again before installing (silent updates skip pages).
Function CheckInstallDir
 SetOutPath "$PLUGINSDIR"
 File "ValidateInstallPath.ps1"
 StrCpy $0 "validate-path"
 StrCpy $1 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\ValidateInstallPath.ps1" -Target "$INSTDIR"'
 Call RunLogged
 ${If} $Result == 2
  MessageBox MB_ICONEXCLAMATION "这个位置普通用户也能随意修改，不适合安装带系统权限的防护服务。$\r$\n请换一个位置，例如默认的 C:\Program Files\Leyra 或 D:\Leyra。" /SD IDOK
  Abort
 ${ElseIf} $Result != 0
  MessageBox MB_ICONEXCLAMATION "这个位置不能安装（不能是快捷方式或链接目录）。请换一个位置。" /SD IDOK
  Abort
 ${EndIf}
 IfFileExists "$INSTDIR\versions\${VERSION}\Leyra.Agent.exe" 0 +3
  MessageBox MB_ICONEXCLAMATION "这个版本已经安装在这里了。如需重装，请先在「设置 → 应用」里卸载 Leyra。" /SD IDOK
  Abort
FunctionEnd
Section "安装" SEC_MAIN
 SetShellVarContext all
 Call CheckInstallDir
 SetOutPath "$PLUGINSDIR"
 File "VerifyRuntime.ps1"
 File "${PREREQUISITES}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
 DetailPrint "正在安装 Microsoft WebView2 运行环境……"
 StrCpy $0 "webview2"
 StrCpy $1 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\VerifyRuntime.ps1" -Installer "$PLUGINSDIR\MicrosoftEdgeWebView2RuntimeInstallerX64.exe" -ExpectedSha256 "${WEBVIEW_SHA256}"'
 Call RunLogged
 ${If} $Result != 0
  MessageBox MB_ICONSTOP "Microsoft 运行环境未通过校验或安装失败，安装已停止。" /SD IDOK
  Abort
 ${EndIf}
 SetOutPath "$INSTDIR\versions\${VERSION}"
 File /r "${PAYLOAD}\*"
 DetailPrint "正在注册后台服务……"
 StrCpy $0 "service-install"
 StrCpy $1 '"$INSTDIR\versions\${VERSION}\Leyra.Agent.exe" --install'
 Call RunLogged
 ${If} $Result != 0
  MessageBox MB_ICONSTOP "后台服务安装未完成，请保留安装详情。此安装不会创建可用状态的快捷方式。" /SD IDOK
  Abort
 ${EndIf}
 CreateDirectory "$SMPROGRAMS\Leyra"
 CreateShortcut "$DESKTOP\Leyra.lnk" "$INSTDIR\versions\${VERSION}\LeyraConnect.exe"
 CreateShortcut "$SMPROGRAMS\Leyra\Leyra.lnk" "$INSTDIR\versions\${VERSION}\LeyraConnect.exe"
 CreateShortcut "$SMPROGRAMS\Leyra\网络恢复工具（管理员）.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -Command "Start-Process -FilePath $\"$INSTDIR\versions\${VERSION}\Leyra.Agent.exe$\" -ArgumentList $\"--recover$\" -Verb RunAs"'
 WriteUninstaller "$INSTDIR\Uninstall.exe"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "DisplayName" "Leyra"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "DisplayVersion" "${VERSION}"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
 WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
 WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "Leyra"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "DisplayIcon" "$INSTDIR\versions\${VERSION}\LeyraConnect.exe"
 WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "NoModify" 1
 WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "NoRepair" 1
SectionEnd
Section "Uninstall"
 SetRegView 64
 SetShellVarContext all
 nsExec::ExecToLog '"$INSTDIR\versions\${VERSION}\Leyra.Agent.exe" --remove-service'
 Pop $Result
 ${If} $Result != 0
  MessageBox MB_ICONSTOP "网络恢复或后台卸载未完成。程序文件已保留，请先使用网络恢复工具。" /SD IDOK
  Abort
 ${EndIf}
 Delete "$DESKTOP\Leyra.lnk"
 RMDir /r "$SMPROGRAMS\Leyra"
 ; Older side-by-side versions left after automatic updates are removed together.
 RMDir /r "$INSTDIR\versions"
 DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect"
 Delete "$INSTDIR\Uninstall.exe"
 RMDir "$INSTDIR"
SectionEnd
