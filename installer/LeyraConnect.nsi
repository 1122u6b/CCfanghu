Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
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
Name "LEYRA 连接助手"
OutFile "${OUTPUT}"
InstallDir "$PROGRAMFILES64\LEYRAConnect"
RequestExecutionLevel admin
SetCompressor zlib
ShowInstDetails show
ShowUninstDetails show
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "安装桌面程序、后台服务和所需运行环境。服务器连接信息会在首次打开时导入。$\r$\n$\r$\n安装包包含离线运行环境。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "安装完成。请从桌面打开 LEYRA 连接助手。$\r$\n$\r$\n如需退出后台防护，请使用程序中的暂停功能；网络异常时可使用开始菜单中的网络恢复工具。"
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
 SetOutPath "$PLUGINSDIR"
 File "ValidateInstallPath.ps1"
 StrCpy $0 "validate-path"
 StrCpy $1 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\ValidateInstallPath.ps1" -Target "$INSTDIR"'
 Call RunLogged
 ${If} $Result != 0
  MessageBox MB_ICONSTOP "安装目录检查未通过，不能写入包含链接的目录。" /SD IDOK
  Abort
 ${EndIf}
 IfFileExists "$INSTDIR\versions\${VERSION}\Leyra.Agent.exe" 0 +3
  MessageBox MB_ICONSTOP "此版本已经安装。请先关闭程序并卸载此版本，再重新安装。" /SD IDOK
  Abort
FunctionEnd
Section "安装" SEC_MAIN
 SetShellVarContext all
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
 CreateDirectory "$SMPROGRAMS\LEYRA 连接助手"
 CreateShortcut "$DESKTOP\LEYRA 连接助手.lnk" "$INSTDIR\versions\${VERSION}\LeyraConnect.exe"
 CreateShortcut "$SMPROGRAMS\LEYRA 连接助手\LEYRA 连接助手.lnk" "$INSTDIR\versions\${VERSION}\LeyraConnect.exe"
 CreateShortcut "$SMPROGRAMS\LEYRA 连接助手\网络恢复工具（管理员）.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -Command "Start-Process -FilePath $\"$INSTDIR\versions\${VERSION}\Leyra.Agent.exe$\" -ArgumentList $\"--recover$\" -Verb RunAs"'
 WriteUninstaller "$INSTDIR\Uninstall.exe"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "DisplayName" "LEYRA 连接助手"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "DisplayVersion" "${VERSION}"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
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
 Delete "$DESKTOP\LEYRA 连接助手.lnk"
 RMDir /r "$SMPROGRAMS\LEYRA 连接助手"
 ; Older side-by-side versions left after automatic updates are removed together.
 RMDir /r "$INSTDIR\versions"
 DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\LEYRAConnect"
 Delete "$INSTDIR\Uninstall.exe"
 RMDir "$INSTDIR"
SectionEnd
