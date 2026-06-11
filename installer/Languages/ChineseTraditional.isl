; MacroHID Traditional Chinese Inno Setup overrides.
; This file is layered after compiler:Default.isl, so untranslated messages
; intentionally fall back to English instead of blocking the build.

[LangOptions]
LanguageName=繁體中文
LanguageID=$0404
LanguageCodePage=950
DialogFontName=Microsoft JhengHei UI

[Messages]
SetupAppTitle=安裝
SetupWindowTitle=安裝 - %1
UninstallAppTitle=解除安裝
UninstallAppFullTitle=%1 解除安裝
InformationTitle=資訊
ConfirmTitle=確認
ErrorTitle=錯誤
SetupLdrStartupMessage=這將安裝 %1。是否繼續？
AdminPrivilegesRequired=安裝此程式時必須以系統管理員身分登入。
ExitSetupTitle=結束安裝
ExitSetupMessage=安裝尚未完成。如果現在結束，程式將不會被安裝。%n%n你可以稍後再次執行安裝程式完成安裝。%n%n是否結束安裝？
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安裝(&I)
ButtonOK=確定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonFinish=完成(&F)
ButtonBrowse=瀏覽(&B)...
ButtonWizardBrowse=瀏覽(&R)...
ButtonNewFolder=新增資料夾(&M)
SelectLanguageTitle=選擇安裝語言
SelectLanguageLabel=請選擇安裝過程中使用的語言。
ClickNext=按一下「下一步」繼續，或按一下「取消」結束安裝。
BrowseDialogTitle=瀏覽資料夾
BrowseDialogLabel=請在清單中選擇資料夾，然後按一下「確定」。
NewFolderName=新增資料夾
WelcomeLabel1=歡迎使用 [name] 安裝精靈
WelcomeLabel2=這將在你的電腦上安裝 [name/ver]。%n%n建議繼續前關閉其他應用程式。
WizardInfoBefore=資訊
InfoBeforeClickLabel=準備繼續安裝時，請按一下「下一步」。
WizardSelectDir=選擇目標位置
SelectDirDesc=[name] 應安裝到哪裡？
SelectDirLabel3=安裝程式會將 [name] 安裝到以下資料夾。
SelectDirBrowseLabel=按一下「下一步」繼續。如需選擇其他資料夾，請按一下「瀏覽」。
DirExists=資料夾：%n%n%1%n%n已經存在。是否仍安裝到該資料夾？
WizardSelectTasks=選擇附加工作
SelectTasksDesc=要執行哪些附加工作？
SelectTasksLabel2=請選擇安裝 [name] 時要執行的附加工作，然後按一下「下一步」。
WizardReady=準備安裝
ReadyLabel1=安裝程式已準備好開始在你的電腦上安裝 [name]。
ReadyLabel2a=按一下「安裝」繼續安裝，或按一下「上一步」檢查或變更設定。
ReadyLabel2b=按一下「安裝」繼續安裝。
ReadyMemoDir=目標位置：
ReadyMemoGroup=開始功能表資料夾：
ReadyMemoTasks=附加工作：
WizardInstalling=正在安裝
InstallingLabel=請稍候，安裝程式正在你的電腦上安裝 [name]。
FinishedHeadingLabel=正在完成 [name] 安裝精靈
FinishedLabelNoIcons=安裝程式已在你的電腦上安裝 [name]。
FinishedLabel=安裝程式已在你的電腦上安裝 [name]。你可以透過已安裝的捷徑啟動應用程式。
RunEntryExec=執行 %1
RunEntryShellExec=檢視 %1
ConfirmUninstall=確定要完全移除 %1 及其所有元件嗎？
UninstallStatusLabel=請稍候，正在從你的電腦中移除 %1。
UninstalledAll=%1 已成功從你的電腦中移除。
