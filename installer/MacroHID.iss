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

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut for MacroStudio"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "installdriver"; Description: "Attempt to install the MacroHID test driver after setup"; GroupDescription: "Driver:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\MacroStudio\*"; DestDir: "{app}\MacroStudio"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\MacroRunner\*"; DestDir: "{app}\MacroRunner"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\LatencyProbe\*"; DestDir: "{app}\LatencyProbe"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\driver\*"; DestDir: "{app}\driver"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\samples\*"; DestDir: "{app}\samples"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\MacroStudio"; Filename: "{app}\MacroStudio\{#AppExeName}"
Name: "{group}\MacroRunner dry-run sample"; Filename: "powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -Command ""& '{app}\MacroRunner\MacroRunner.exe' --macro '{app}\samples\baseline.mcrx' --pixels match"""; WorkingDir: "{app}"
Name: "{group}\Install MacroHID test driver"; Filename: "powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -File ""{app}\scripts\Install-TestDriver.ps1"" -Configuration Release -SkipBuild"; WorkingDir: "{app}"
Name: "{group}\Uninstall MacroHID test driver"; Filename: "powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -File ""{app}\scripts\Uninstall-TestDriver.ps1"""; WorkingDir: "{app}"
Name: "{group}\Documentation"; Filename: "{app}\README.md"
Name: "{autodesktop}\MacroStudio"; Filename: "{app}\MacroStudio\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Install-TestDriver.ps1"" -Configuration Release -SkipBuild"; Description: "Install MacroHID test driver"; Flags: postinstall skipifsilent; Tasks: installdriver
Filename: "{app}\MacroStudio\{#AppExeName}"; Description: "Launch MacroStudio"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Uninstall-TestDriver.ps1"""; Flags: runhidden; RunOnceId: "MacroHID.UninstallTestDriver"
