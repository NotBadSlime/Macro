; MacroHID Simplified Chinese Inno Setup overrides.
; This file is layered after compiler:Default.isl, so untranslated messages
; intentionally fall back to English instead of blocking the build.

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=936
DialogFontName=Microsoft YaHei UI

[Messages]
SetupAppTitle=安装
SetupWindowTitle=安装 - %1
UninstallAppTitle=卸载
UninstallAppFullTitle=%1 卸载
InformationTitle=信息
ConfirmTitle=确认
ErrorTitle=错误
SetupLdrStartupMessage=这将安装 %1。是否继续？
AdminPrivilegesRequired=安装此程序时必须以管理员身份登录。
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。如果现在退出，程序将不会被安装。%n%n你可以稍后再次运行安装程序完成安装。%n%n是否退出安装？
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)
SelectLanguageTitle=选择安装语言
SelectLanguageLabel=请选择安装过程中使用的语言。
ClickNext=单击“下一步”继续，或单击“取消”退出安装。
BrowseDialogTitle=浏览文件夹
BrowseDialogLabel=请在列表中选择文件夹，然后单击“确定”。
NewFolderName=新建文件夹
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=这将在你的计算机上安装 [name/ver]。%n%n建议继续前关闭其他应用程序。
WizardInfoBefore=信息
InfoBeforeClickLabel=准备继续安装时，请单击“下一步”。
WizardSelectDir=选择目标位置
SelectDirDesc=[name] 应安装到哪里？
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。
SelectDirBrowseLabel=单击“下一步”继续。如需选择其他文件夹，请单击“浏览”。
DirExists=文件夹：%n%n%1%n%n已经存在。是否仍安装到该文件夹？
WizardSelectTasks=选择附加任务
SelectTasksDesc=要执行哪些附加任务？
SelectTasksLabel2=请选择安装 [name] 时要执行的附加任务，然后单击“下一步”。
WizardReady=准备安装
ReadyLabel1=安装程序已准备好开始在你的计算机上安装 [name]。
ReadyLabel2a=单击“安装”继续安装，或单击“上一步”检查或更改设置。
ReadyLabel2b=单击“安装”继续安装。
ReadyMemoDir=目标位置：
ReadyMemoGroup=开始菜单文件夹：
ReadyMemoTasks=附加任务：
WizardInstalling=正在安装
InstallingLabel=请稍候，安装程序正在你的计算机上安装 [name]。
FinishedHeadingLabel=正在完成 [name] 安装向导
FinishedLabelNoIcons=安装程序已在你的计算机上安装 [name]。
FinishedLabel=安装程序已在你的计算机上安装 [name]。你可以通过已安装的快捷方式启动应用程序。
RunEntryExec=运行 %1
RunEntryShellExec=查看 %1
ConfirmUninstall=确定要完全移除 %1 及其所有组件吗？
UninstallStatusLabel=请稍候，正在从你的计算机中移除 %1。
UninstalledAll=%1 已成功从你的计算机中移除。
