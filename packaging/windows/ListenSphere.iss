#ifndef ProductVersion
  #error ProductVersion must be provided by Build-ListenSphereInstaller.ps1
#endif
#ifndef NumericVersion
  #error NumericVersion must be provided by Build-ListenSphereInstaller.ps1
#endif
#ifndef SourceRoot
  #error SourceRoot must be provided by Build-ListenSphereInstaller.ps1
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be provided by Build-ListenSphereInstaller.ps1
#endif
#ifndef InstallerBaseName
  #error InstallerBaseName must be provided by Build-ListenSphereInstaller.ps1
#endif

[Setup]
AppId={{73B23AE6-AFA2-402B-B0E0-993531F7527B}
AppName=聆界 ListenSphere
AppVersion={#ProductVersion}
AppVerName=聆界 ListenSphere {#ProductVersion}
AppPublisher=ListenSphere Project
AppPublisherURL=https://github.com/fengxxdwn/listen-sphere
AppSupportURL=https://github.com/fengxxdwn/listen-sphere/issues
DefaultDirName={autopf}\ListenSphere
DefaultGroupName=聆界 ListenSphere
DisableProgramGroupPage=yes
UninstallDisplayName=聆界 ListenSphere
UninstallDisplayIcon={app}\Controller\ListenSphere.Controller.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir={#OutputDirectory}
OutputBaseFilename={#InstallerBaseName}
SetupIconFile={#SourceRoot}\assets\branding\windows\ListenSphere.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
VersionInfoVersion={#NumericVersion}
VersionInfoCompany=ListenSphere Project
VersionInfoDescription=聆界 ListenSphere Installer
VersionInfoProductName=聆界 ListenSphere
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#ProductVersion}
VersionInfoOriginalFileName={#InstallerBaseName}.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "controller"; Description: "聆界主控端 / Controller（推荐）"
Name: "full"; Description: "聆界主控端和发送端 / Controller and Sender"
Name: "custom"; Description: "自定义 / Custom"; Flags: iscustom

[Components]
Name: "controller"; Description: "聆界主控端 / Controller"; Types: controller full custom; Flags: fixed
Name: "sender"; Description: "聆界发送端 / Sender"; Types: full

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceRoot}\artifacts\publish\Controller\*"; DestDir: "{app}\Controller"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: controller
Source: "{#SourceRoot}\artifacts\publish\Sender\*"; DestDir: "{app}\Sender"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: sender

[InstallDelete]
Type: filesandordirs; Name: "{app}\Sender"; Check: not WizardIsComponentSelected('sender')
Type: files; Name: "{group}\聆界发送端.lnk"; Check: not WizardIsComponentSelected('sender')

[Icons]
Name: "{group}\聆界"; Filename: "{app}\Controller\ListenSphere.Controller.exe"; WorkingDir: "{app}\Controller"; Components: controller
Name: "{group}\聆界发送端"; Filename: "{app}\Sender\ListenSphere.Sender.exe"; WorkingDir: "{app}\Sender"; Components: sender
Name: "{autodesktop}\聆界"; Filename: "{app}\Controller\ListenSphere.Controller.exe"; WorkingDir: "{app}\Controller"; Tasks: desktopicon; Components: controller

[Run]
Filename: "{app}\Controller\ListenSphere.Controller.exe"; Description: "启动聆界"; WorkingDir: "{app}\Controller"; Flags: nowait postinstall skipifsilent; Components: controller
