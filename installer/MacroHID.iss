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
