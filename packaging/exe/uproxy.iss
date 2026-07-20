#define MyAppName "μProxy Tool"
#define MyAppExeName "uproxy.exe"

#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif

#ifndef MySourceDir
  #error MySourceDir must point to the dotnet publish directory.
#endif

#ifndef MyOutputDir
  #error MyOutputDir must point to the installer output directory.
#endif

[Setup]
AppId=uproxy.1cee7a36-30f1-4af4-b656-cef4f37e18f8
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=net'n'yahoo
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=net'n'yahoo
VersionInfoDescription=μProxy Tool offline installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\uproxy
DefaultGroupName=μProxy Tool
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#MyOutputDir}
OutputBaseFilename=uproxy_{#MyAppVersion}_x64_setup
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
SignedUninstaller=no

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\μProxy Tool"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch μProxy Tool"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\uProxyTool"
