#ifndef SourceDir
#define SourceDir "..\artifacts\installer-input"
#endif

#ifndef OutputDir
#define OutputDir "..\artifacts\installer"
#endif

#ifndef AppVersion
#define AppVersion "0.1.0"
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
UninstallDisplayIcon={app}\MacroStudio\{#AppExeName}
InfoBeforeFile=DriverInstallNotes.txt

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
english.InstallDriverTask=Attempt to install the MacroHID test driver after setup
chinesesimp.InstallDriverTask=安装完成后尝试安装 MacroHID 测试驱动
chinesetrad.InstallDriverTask=安裝完成後嘗試安裝 MacroHID 測試驅動
english.DriverGroup=Driver:
chinesesimp.DriverGroup=驱动：
chinesetrad.DriverGroup=驅動：
english.MacroRunnerSampleShortcut=MacroRunner dry-run sample
chinesesimp.MacroRunnerSampleShortcut=MacroRunner 干运行示例
chinesetrad.MacroRunnerSampleShortcut=MacroRunner 試跑範例
english.InstallDriverShortcut=Install MacroHID test driver
chinesesimp.InstallDriverShortcut=安装 MacroHID 测试驱动
chinesetrad.InstallDriverShortcut=安裝 MacroHID 測試驅動
english.UninstallDriverShortcut=Uninstall MacroHID test driver
chinesesimp.UninstallDriverShortcut=卸载 MacroHID 测试驱动
chinesetrad.UninstallDriverShortcut=解除安裝 MacroHID 測試驅動
english.DocumentationShortcut=Documentation
chinesesimp.DocumentationShortcut=文档
chinesetrad.DocumentationShortcut=文件
english.MacroConverterShortcut=MacroConverter
chinesesimp.MacroConverterShortcut=MacroConverter 宏转换器
chinesetrad.MacroConverterShortcut=MacroConverter 巨集轉換器
english.InstallDriverRun=Install MacroHID test driver
chinesesimp.InstallDriverRun=安装 MacroHID 测试驱动
chinesetrad.InstallDriverRun=安裝 MacroHID 測試驅動
english.LaunchMacroStudio=Launch MacroStudio
chinesesimp.LaunchMacroStudio=启动 MacroStudio
chinesetrad.LaunchMacroStudio=啟動 MacroStudio

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopShortcut}"; GroupDescription: "{cm:ShortcutsGroup}"; Flags: unchecked
Name: "installdriver"; Description: "{cm:InstallDriverTask}"; GroupDescription: "{cm:DriverGroup}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\MacroStudio\*"; DestDir: "{app}\MacroStudio"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\MacroRunner\*"; DestDir: "{app}\MacroRunner"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\LatencyProbe\*"; DestDir: "{app}\LatencyProbe"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\driver\*"; DestDir: "{app}\driver"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\samples\*"; DestDir: "{app}\samples"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\MacroConverter\*"; DestDir: "{app}\MacroConverter"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\MacroStudio"; Filename: "{app}\MacroStudio\{#AppExeName}"
Name: "{group}\{cm:MacroConverterShortcut}"; Filename: "{app}\MacroConverter\MacroConverter.exe"; Check: FileExists(ExpandConstant('{app}\MacroConverter\MacroConverter.exe'))
Name: "{group}\{cm:MacroRunnerSampleShortcut}"; Filename: "powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -Command ""& '{app}\MacroRunner\MacroRunner.exe' --macro '{app}\samples\baseline.mcrx' --pixels match"""; WorkingDir: "{app}"
Name: "{group}\{cm:InstallDriverShortcut}"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Install-TestDriverInteractive.ps1"" -Configuration Release -SkipBuild"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallDriverShortcut}"; Filename: "powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -File ""{app}\scripts\Uninstall-TestDriver.ps1"""; WorkingDir: "{app}"
Name: "{group}\{cm:DocumentationShortcut}"; Filename: "{app}\README.md"
Name: "{autodesktop}\MacroStudio"; Filename: "{app}\MacroStudio\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Install-TestDriverInteractive.ps1"" -Configuration Release -SkipBuild"; Description: "{cm:InstallDriverRun}"; Flags: postinstall skipifsilent; Tasks: installdriver
Filename: "{app}\MacroStudio\{#AppExeName}"; Description: "{cm:LaunchMacroStudio}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Uninstall-TestDriver.ps1"""; Flags: runhidden; RunOnceId: "MacroHID.UninstallTestDriver"
