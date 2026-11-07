#ifndef AppVersion
  #define AppVersion "0.13.0"
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
UsePreviousAppDir=yes
DirExistsWarning=no
DisableDirPage=auto
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
UninstallFilesDir={app}\Uninstall
CloseApplications=force
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
Filename: "{app}\GProControl.exe"; Description: "{cm:LaunchProgram,G Pro Control}"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""GProControl Startup"" /F"; Flags: runhidden; RunOnceId: "RemoveGProControlStartupTask"; Check: OwnsStartupTask

[Code]
var
  UninstallCacheHash: String;
  PreviousOwnedFiles: TArrayOfString;
  PreviousCacheHash: String;

function GetFileAttributes(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function HasReparseParent(Path: String): Boolean;
var
  Attr: LongWord;
  Parent: String;
begin
  Result := False;
  while Length(Path) > 3 do begin
    Attr := GetFileAttributes(Path);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then begin
      Result := True;
      Exit;
    end;
    Parent := ExtractFileDir(Path);
    if CompareText(Parent, Path) = 0 then Exit;
    Path := Parent;
  end;
end;

procedure RegisterExtraCloseApplicationsResources;
var
  LayoutPath, Hash: String;
begin
  LayoutPath := ExpandConstant('{app}\Runtime\layout.tsv');
  if FileExists(LayoutPath) then begin
    Hash := Lowercase(GetSHA256OfFile(LayoutPath));
    RegisterExtraCloseApplicationsResource(False,
      ExpandConstant('{commonappdata}\GProControl\Runtime\') + Hash + '\GProControl.exe');
  end;
end;

function HasPreviousInstallation: Boolean;
var
  Location: String;
begin
  Result := RegQueryStringValue(HKLM64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C185DBE4-018F-45B8-9464-5474FCA1C537}_is1',
    'InstallLocation', Location);
  Result := Result and FileExists(AddBackslash(Location) + 'GProControl.exe');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := HasPreviousInstallation and ((PageID = wpSelectDir) or (PageID = wpSelectProgramGroup)
    or (PageID = wpSelectTasks) or (PageID = wpReady));
end;

function IsOwnedPath(const Path: String): Boolean;
var
  Lower: String;
begin
  Lower := Lowercase(Path);
  Result := (Pos('..', Path) = 0) and (Pos(':', Path) = 0) and (Copy(Path, 1, 1) <> '\')
    and ((Lower = 'gprocontrol.exe') or (Pos('libraries\', Lower) = 1)
    or (Pos('languages\', Lower) = 1) or (Pos('resources\', Lower) = 1)
    or (Pos('runtime\', Lower) = 1));
end;

procedure ReadPreviousFiles;
var
  Layout: TArrayOfString;
  I, FirstTab, SecondTab, N: Integer;
  Line, Relative: String;
begin
  if FileExists(ExpandConstant('{app}\Runtime\layout.tsv')) then
    PreviousCacheHash := Lowercase(GetSHA256OfFile(ExpandConstant('{app}\Runtime\layout.tsv')));
  if LoadStringsFromFile(ExpandConstant('{app}\Runtime\installed-files.txt'), PreviousOwnedFiles) then Exit;
  { Migration from versions before the owned-file inventory existed. }
  if not LoadStringsFromFile(ExpandConstant('{app}\Runtime\layout.tsv'), Layout) then Exit;
  SetArrayLength(PreviousOwnedFiles, 3);
  PreviousOwnedFiles[0] := 'GProControl.exe';
  PreviousOwnedFiles[1] := 'Runtime\layout.tsv';
  PreviousOwnedFiles[2] := 'Runtime\Directory-Layout.txt';
  for I := 0 to GetArrayLength(Layout) - 1 do begin
    Line := Layout[I];
    FirstTab := Pos(#9, Line);
    if FirstTab > 0 then begin
      Delete(Line, 1, FirstTab);
      SecondTab := Pos(#9, Line);
      if SecondTab > 0 then begin
        Relative := Copy(Line, 1, SecondTab - 1);
        if IsOwnedPath(Relative) then begin
          N := GetArrayLength(PreviousOwnedFiles);
          SetArrayLength(PreviousOwnedFiles, N + 1);
          PreviousOwnedFiles[N] := Relative;
        end;
      end;
    end;
  end;
end;

procedure RemoveRetiredFiles;
var
  Current: TArrayOfString;
  I, J: Integer;
  Found: Boolean;
  Path, CurrentHash: String;
begin
  if not LoadStringsFromFile(ExpandConstant('{app}\Runtime\installed-files.txt'), Current) then
    RaiseException('Installed file inventory is missing.');
  for I := 0 to GetArrayLength(PreviousOwnedFiles) - 1 do begin
    if IsOwnedPath(PreviousOwnedFiles[I]) then begin
      Found := False;
      for J := 0 to GetArrayLength(Current) - 1 do
        if CompareText(PreviousOwnedFiles[I], Current[J]) = 0 then Found := True;
      if not Found then begin
        Path := ExpandFileName(AddBackslash(ExpandConstant('{app}')) + PreviousOwnedFiles[I]);
        if Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(Path)) <> 1 then
          RaiseException('Unsafe retired file path.');
        if HasReparseParent(Path) then
          Log('Preserving retired path through a reparse point: ' + Path)
        else if FileExists(Path) and not DeleteFile(Path) then
          RaiseException('Unable to remove retired application file: ' + Path);
      end;
    end;
  end;
  CurrentHash := Lowercase(GetSHA256OfFile(ExpandConstant('{app}\Runtime\layout.tsv')));
  if (Length(PreviousCacheHash) = 64) and (PreviousCacheHash <> CurrentHash)
    and not HasReparseParent(ExpandConstant('{commonappdata}\GProControl\Runtime\') + PreviousCacheHash) then
    DelTree(ExpandConstant('{commonappdata}\GProControl\Runtime\') + PreviousCacheHash, True, True, True);
end;

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
    if not HasReparseParent(CachePath) and not DelTree(CachePath, True, True, True) then
      Log('Runtime cache could not be completely removed.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then ReadPreviousFiles;
  if CurStep = ssPostInstall then begin
    if not Exec(ExpandConstant('{app}\GProControl.exe'), '--prepare-runtime', ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('Unable to prepare the application runtime.');
    if ResultCode <> 0 then
      RaiseException('Application runtime integrity validation failed. Reinstall G Pro Control.');
    Log('Application runtime preparation and integrity validation passed.');
    RemoveRetiredFiles;
  end;
end;
