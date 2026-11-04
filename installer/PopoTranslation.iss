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
DefaultDirName={autopf}\PopoTranslation
UsePreviousAppDir=yes
; Per-user by default so the in-app updater needs no UAC prompt; the dialog still allows an all-users install.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
AppPublisherURL=https://github.com/wchiway/popotranslation
AppSupportURL=https://github.com/wchiway/popotranslation/issues
AppUpdatesURL=https://github.com/wchiway/popotranslation/releases
LicenseFile=..\LICENSE
SetupIconFile=..\Assets\PopoTranslation.ico
VersionInfoVersion={#AppVersion}
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

[Run]
Filename: "{app}\popotranslation.exe"; Description: "{cm:LaunchProgram,PopoTranslation}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  InstanceMutex = 'Local\PopoTranslation.SingleInstance';

// The in-app updater launches Setup with /UPDATE=1 and then exits; wait briefly for the mutex to clear.
function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  if ExpandConstant('{param:UPDATE|0}') = '1' then
  begin
    Waited := 0;
    while CheckForMutexes(InstanceMutex) and (Waited < 10000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end;
end;

// The application registers itself under HKCU\...\Run; remove the entry with the application.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, RunKey, 'PopoTranslation');
end;
