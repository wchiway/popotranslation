; Build with ISCC /DPublishDir=<absolute publish directory> /DAppVersion=<version>
; and /O<new output directory>. Review the publish directory before packaging.
#ifndef PublishDir
  #error PublishDir must be supplied on the ISCC command line.
#endif
#ifndef AppVersion
  #error AppVersion must be supplied on the ISCC command line.
#endif
#if !FileExists(AddBackslash(PublishDir) + "popotranslation.exe")
  #error PublishDir must contain popotranslation.exe.
#endif

[Setup]
AppId={{A1EA97FC-C7BE-43F1-8C3E-184F1848A270}
AppName=PopoTranslation
AppVersion={#AppVersion}
AppPublisher=PopoTranslation
DefaultDirName={commonpf32}\PopoTranslation
UsePreviousAppDir=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
AppMutex=Local\PopoTranslation.SingleInstance
CloseApplications=no
UninstallDisplayIcon={app}\popotranslation.exe
OutputBaseFilename=PopoTranslation.{#AppVersion}.Setup.x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "settings.json,preferences.json,*.db,*.db-*,*.sqlite,*.sqlite-*,*.sqlite3,*.sqlite3-*,*.log,*.secrets.json,.env,.env.*,*.pfx,*.p12,*.pdb,logs\*,secrets\*"

[Icons]
Name: "{autoprograms}\PopoTranslation"; Filename: "{app}\popotranslation.exe"; WorkingDir: "{app}"
