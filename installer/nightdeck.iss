; Nightdeck installer (per-user by default).
; Compile with installer/build.ps1 after a publish to out\Nightdeck.exe.

#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#define MyAppName "Nightdeck"
#define MyAppPublisher "Fredrik Spång"
#define MyAppURL "https://github.com/fredrikspang-sys/nightdeck-win"
#define MyAppExeName "Nightdeck.exe"
#define MyAppId "{{885EB0EC-B8FF-47EF-BEBA-8FA59EA01DB7}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=Nightdeck-{#MyAppVersion}-setup
SetupIconFile=nightdeck.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\out\Nightdeck.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function DirHasDotNetDesktop9(const SharedDir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(SharedDir) then
    Exit;
  if FindFirst(SharedDir + '\*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
         (Length(FindRec.Name) >= 2) and (Copy(FindRec.Name, 1, 2) = '9.') then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function IsDotNetDesktop9Installed: Boolean;
var
  Root: String;
begin
  Result := True;
  if DirHasDotNetDesktop9(ExpandConstant('{pf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) then
    Exit;
  if DirHasDotNetDesktop9(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) then
    Exit;
  Root := GetEnv('ProgramW6432');
  if (Root <> '') and DirHasDotNetDesktop9(Root + '\dotnet\shared\Microsoft.WindowsDesktop.App') then
    Exit;
  Root := GetEnv('DOTNET_ROOT');
  if (Root <> '') and DirHasDotNetDesktop9(Root + '\shared\Microsoft.WindowsDesktop.App') then
    Exit;
  Root := GetEnv('DOTNET_ROOT_X64');
  if (Root <> '') and DirHasDotNetDesktop9(Root + '\shared\Microsoft.WindowsDesktop.App') then
    Exit;
  if DirHasDotNetDesktop9(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App')) then
    Exit;
  if RegQueryStringValue(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) and
     DirHasDotNetDesktop9(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App') then
    Exit;
  Result := False;
end;

function TryWingetInstallRuntime: Boolean;
var
  ResultCode: Integer;
  WingetPath: String;
  Params: String;
begin
  Result := False;
  Params := 'install --id Microsoft.DotNet.DesktopRuntime.9 -e --silent --accept-package-agreements --accept-source-agreements';
  WingetPath := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if FileExists(WingetPath) then
  begin
    if Exec(WingetPath, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    begin
      Result := IsDotNetDesktop9Installed;
      Exit;
    end;
  end;
  if Exec(ExpandConstant('{cmd}'), '/c winget ' + Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    Result := IsDotNetDesktop9Installed;
end;

function OnRuntimeDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax <> 0 then
    Log(Format('Downloading .NET 9 Desktop Runtime: %d / %d', [Progress, ProgressMax]));
  Result := True;
end;

function TryDownloadRuntime: Boolean;
var
  ResultCode: Integer;
  InstallerPath: String;
begin
  Result := False;
  try
    DownloadTemporaryFile(
      'https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe',
      'windowsdesktop-runtime-win-x64.exe',
      '',
      @OnRuntimeDownloadProgress);
    InstallerPath := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
    if FileExists(InstallerPath) and
       Exec(InstallerPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
       (ResultCode = 0) then
      Result := IsDotNetDesktop9Installed;
  except
    Result := False;
  end;
end;

function InstallDotNetDesktop9: Boolean;
begin
  Log('Microsoft.WindowsDesktop.App 9.x was not found; trying to install it.');
  Result := TryWingetInstallRuntime;
  if not Result then
    Result := TryDownloadRuntime;
end;

function InitializeSetup(): Boolean;
var
  Msg: String;
begin
  Result := True;
  if IsDotNetDesktop9Installed then
    Exit;
  if InstallDotNetDesktop9 then
    Exit;
  Msg := 'Nightdeck requires the .NET 9 Desktop Runtime (x64), and automatic installation failed. ' +
         'Install it from https://dotnet.microsoft.com/download/dotnet/9.0 and run this installer again.';
  if not WizardSilent then
    MsgBox(Msg, mbCriticalError, MB_OK);
  Log(Msg);
  Result := False;
end;
