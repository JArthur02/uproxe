#define MyAppName "uproxy"
#define MyAppExeName "uproxy.exe"

#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif

#ifndef MyAppPublisher
  #define MyAppPublisher "uproxy"
#endif

#ifndef MySourceDir
  #error MySourceDir must point to the dotnet publish directory.
#endif

#ifndef MyOutputDir
  #error MyOutputDir must point to the installer output directory.
#endif

#ifndef SignInstaller
  #define SignInstaller 0
#endif

[Setup]
AppId=uproxy.1cee7a36-30f1-4af4-b656-cef4f37e18f8
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=uproxy offline installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\uproxy
DefaultGroupName=uproxy
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\..\src\UProxy.UI\Assets\uproxy.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#MyOutputDir}
OutputBaseFilename=uproxy_{#MyAppVersion}_x64_setup
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
#if SignInstaller
SignTool=store-sign
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\uproxy"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch uproxy"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\uproxy"
