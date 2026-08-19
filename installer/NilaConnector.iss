#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName "Nila Connector"
#define AppExeName "NilaConnector.exe"
#define AppPublisher "KaguyaHQ"
#define AppUrl "https://nila.tax"
#define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish\win-x64"

[Setup]
AppId={{A2F1FA7D-C1F9-4502-A5CE-6545EE96E6E2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={localappdata}\Programs\Nila Connector
DefaultGroupName=Nila Connector
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=NilaConnector-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\NilaConnector.Desktop\Assets\nila.ico
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Tasks]
Name: "startup"; Description: "Start Nila Connector when I sign in"; GroupDescription: "Startup:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Nila Connector"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{userstartup}\Nila Connector"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: startup
Name: "{autodesktop}\Nila Connector"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Nila Connector"; Flags: nowait postinstall skipifsilent
