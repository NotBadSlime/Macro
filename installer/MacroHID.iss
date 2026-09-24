#ifndef SourceDir
#define SourceDir "..\artifacts\installer-input"
#endif

#ifndef OutputDir
#define OutputDir "..\artifacts\installer"
#endif

#ifndef AppVersion
#define AppVersion "1.4.1"
#endif

#define AppName "MacroHID"
#define AppPublisher "MacroHID"
#define AppExeName "MacroStudio.exe"

[Setup]
AppId={{B5F9673A-4F1A-4F3B-90E6-BB2A83DF13C8}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\MacroHID
DefaultGroupName=MacroHID
DisableDirPage=no
AlwaysShowDirOnReadyPage=yes
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=MacroHID-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
SetupIconFile=..\src\ui\MacroStudio\Assets\AppIcon.ico
UninstallDisplayIcon={app}\MacroStudio\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl,Languages\ChineseSimplified.isl"
Name: "chinesetrad"; MessagesFile: "compiler:Default.isl,Languages\ChineseTraditional.isl"

[CustomMessages]
english.CreateDesktopShortcut=Create a desktop shortcut for MacroStudio
chinesesimp.CreateDesktopShortcut=创建 MacroStudio 桌面快捷方式
chinesetrad.CreateDesktopShortcut=建立 MacroStudio 桌面捷徑
english.ShortcutsGroup=Shortcuts:
chinesesimp.ShortcutsGroup=快捷方式：
chinesetrad.ShortcutsGroup=捷徑：
english.MacroRunnerSampleShortcut=MacroRunner dry-run sample
chinesesimp.MacroRunnerSampleShortcut=MacroRunner 干运行示例
chinesetrad.MacroRunnerSampleShortcut=MacroRunner 試跑範例
english.DocumentationShortcut=Documentation
chinesesimp.DocumentationShortcut=文档
chinesetrad.DocumentationShortcut=文件
english.LaunchMacroStudio=Launch MacroStudio
chinesesimp.LaunchMacroStudio=启动 MacroStudio
chinesetrad.LaunchMacroStudio=啟動 MacroStudio
english.DeleteUserDataCaption=MacroHID user data
chinesesimp.DeleteUserDataCaption=MacroHID 用户数据
chinesetrad.DeleteUserDataCaption=MacroHID 使用者資料
english.DeleteUserDataPrompt=Also delete this user's macro database, settings, and workspace layout?%n%nChoose No to keep them for a later reinstall. Keeping data is recommended.
chinesesimp.DeleteUserDataPrompt=是否同时删除当前用户的宏数据库、设置和界面布局？%n%n选择“否”会保留数据，重新安装后仍可继续使用。建议保留。
chinesetrad.DeleteUserDataPrompt=是否同時刪除目前使用者的巨集資料庫、設定和介面配置？%n%n選擇「否」會保留資料，重新安裝後仍可繼續使用。建議保留。
english.DeleteUserDataFinalWarning=This permanently deletes all files under:%n%1%n%nThis cannot be undone. Delete them now?
chinesesimp.DeleteUserDataFinalWarning=这将永久删除以下目录中的全部文件：%n%1%n%n此操作无法撤销，确定立即删除吗？
chinesetrad.DeleteUserDataFinalWarning=這將永久刪除以下目錄中的全部檔案：%n%1%n%n此操作無法復原，確定立即刪除嗎？
english.DeleteUserDataFailed=Some user data could not be deleted. You can remove it manually from:%n%1
chinesesimp.DeleteUserDataFailed=部分用户数据无法删除，可稍后手动清理：%n%1
chinesetrad.DeleteUserDataFailed=部分使用者資料無法刪除，可稍後手動清理：%n%1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopShortcut}"; GroupDescription: "{cm:ShortcutsGroup}"; Flags: unchecked

[Files]
; MacroHid.NativePlayback.dll is copied into MacroStudio, MacroRunner, and LatencyProbe before these wildcard entries are packed.
Source: "{#SourceDir}\MacroStudio\*"; DestDir: "{app}\MacroStudio"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\MacroRunner\*"; DestDir: "{app}\MacroRunner"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\LatencyProbe\*"; DestDir: "{app}\LatencyProbe"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\samples\*"; DestDir: "{app}\samples"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\MacroStudio"; Filename: "{app}\MacroStudio\{#AppExeName}"
Name: "{group}\{cm:MacroRunnerSampleShortcut}"; Filename: "powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -Command ""& '{app}\MacroRunner\MacroRunner.exe' --macro '{app}\samples\baseline.mcrx' --pixels match"""; WorkingDir: "{app}"
Name: "{group}\{cm:DocumentationShortcut}"; Filename: "{app}\README.md"
Name: "{autodesktop}\MacroStudio"; Filename: "{app}\MacroStudio\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\MacroStudio\{#AppExeName}"; Description: "{cm:LaunchMacroStudio}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataPath: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  { SuppressibleMsgBox returns IDNO during silent uninstall, so user data is preserved by default. }
  if SuppressibleMsgBox(
       ExpandConstant('{cm:DeleteUserDataPrompt}'),
       mbConfirmation,
       MB_YESNO or MB_DEFBUTTON2,
       IDNO) <> IDYES then
    Exit;

  UserDataPath := ExpandConstant('{userappdata}\MacroHID');
  if SuppressibleMsgBox(
       FmtMessage(ExpandConstant('{cm:DeleteUserDataFinalWarning}'), [UserDataPath]),
       mbError,
       MB_YESNO or MB_DEFBUTTON2,
       IDNO) <> IDYES then
    Exit;

  if DirExists(UserDataPath) and (not DelTree(UserDataPath, True, True, True)) then
    MsgBox(
      FmtMessage(ExpandConstant('{cm:DeleteUserDataFailed}'), [UserDataPath]),
      mbError,
      MB_OK);
end;
