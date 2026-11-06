#ifndef AppVersion
  #define AppVersion "0.11.1"
#endif
#ifndef SourceRoot
  #error SourceRoot must point to the classified installation payload.
#endif
#ifndef InstallerOutput
  #define InstallerOutput "..\artifacts\latest"
#endif

[Setup]
AppId={{C185DBE4-018F-45B8-9464-5474FCA1C537}
AppName=G Pro Control
AppVersion={#AppVersion}
AppPublisher=Memories_white
AppCopyright=Memories_white
DefaultDirName={autopf}\G Pro Control
DefaultGroupName=G Pro Control
DisableProgramGroupPage=yes
OutputDir={#InstallerOutput}
OutputBaseFilename=GProControl-{#AppVersion}-Setup-x64
SetupIconFile=..\GProControl\Assets\GProControl.ico
UninstallDisplayIcon={app}\GProControl.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
PrivilegesRequired=admin
AppMutex=Local\GProControl.InstalledApp
UninstallFilesDir={app}\Uninstall
CloseApplications=yes
RestartApplications=no
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\G Pro Control"; Filename: "{app}\GProControl.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,G Pro Control}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\G Pro Control"; Filename: "{app}\GProControl.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\GProControl.exe"; Description: "{cm:LaunchProgram,G Pro Control}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""GProControl Startup"" /F"; Flags: runhidden; RunOnceId: "RemoveGProControlStartupTask"; Check: OwnsStartupTask

[Code]
var
  UninstallCacheHash: String;

function OwnsStartupTask: Boolean;
var
  Service, Root, Task: Variant;
begin
  Result := False;
  try
    Service := CreateOleObject('Schedule.Service');
    Service.Connect;
    Root := Service.GetFolder('\');
    Task := Root.GetTask('GProControl Startup');
    Result := CompareText(Task.Definition.Actions.Item(1).Path, ExpandConstant('{app}\GProControl.exe')) = 0;
  except
    Result := False;
  end;
end;

function InitializeUninstall: Boolean;
begin
  UninstallCacheHash := '_no-cache_';
  if FileExists(ExpandConstant('{app}\Runtime\layout.tsv')) then
    UninstallCacheHash := Lowercase(GetSHA256OfFile(ExpandConstant('{app}\Runtime\layout.tsv')));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CachePath: String;
begin
  if (CurUninstallStep = usPostUninstall) and (Length(UninstallCacheHash) = 64) then begin
    CachePath := ExpandConstant('{commonappdata}\GProControl\Runtime\') + UninstallCacheHash;
    Log('Removing this installation runtime cache: ' + CachePath);
    if not DelTree(CachePath, True, True, True) then
      Log('Runtime cache could not be completely removed.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    if not Exec(ExpandConstant('{app}\GProControl.exe'), '--prepare-runtime', ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('Unable to prepare the application runtime.');
    if ResultCode <> 0 then
      RaiseException('Application runtime integrity validation failed. Reinstall G Pro Control.');
    Log('Application runtime preparation and integrity validation passed.');
  end;
end;
