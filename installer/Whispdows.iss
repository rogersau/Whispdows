#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "Whispdows"
#define MyAppPublisher "Whispdows"
#define MyAppExeName "Whispdows.exe"
#define PublishDir "..\artifacts\publish\win-x64"

[Setup]
AppId={{AD992FD4-CDFC-4E61-B035-6F87E2047088}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Whispdows
DefaultGroupName=Whispdows
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=Whispdows-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Tasks]
Name: "startup"; Description: "Launch Whispdows when I sign in"; GroupDescription: "Startup:"; Flags: unchecked checkedonce
Name: "ollama"; Description: "Install &Ollama for local AI cleanup (internet required; about 4 GB plus model storage)"; GroupDescription: "Optional local AI:"; Flags: unchecked; Check: ShouldOfferOllamaInstall

[Dirs]
Name: "{localappdata}\Whispdows"; Flags: uninsneveruninstall

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\settings.example.json"; DestDir: "{localappdata}\Whispdows"; DestName: "settings.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Whispdows"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\README"; Filename: "{app}\README.md"
Name: "{group}\Uninstall Whispdows"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--enable-startup"; Tasks: startup; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Whispdows"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserDataOnUninstall: Boolean;
  OllamaInstallAttempted: Boolean;

function FindWingetPath: String;
var
  Candidate: String;
begin
  Candidate := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if FileExists(Candidate) then
  begin
    Result := Candidate;
    Exit;
  end;

  Result := FileSearch('winget.exe', GetEnv('PATH'));
end;

function IsOllamaInstalled: Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{localappdata}\Programs\Ollama\ollama.exe')) or
    (FileSearch('ollama.exe', GetEnv('PATH')) <> '');
end;

function ShouldOfferOllamaInstall: Boolean;
begin
  Result := not IsOllamaInstalled;
end;

procedure InstallOllamaIfRequested;
var
  WingetPath: String;
  ResultCode: Integer;
  ShellErrorCode: Integer;
begin
  if OllamaInstallAttempted or
     (not WizardIsTaskSelected('ollama')) or
     IsOllamaInstalled then
  begin
    Exit;
  end;

  OllamaInstallAttempted := True;
  WingetPath := FindWingetPath;
  if WingetPath = '' then
  begin
    if MsgBox(
         'Windows Package Manager is not available, so Whispdows cannot install Ollama automatically.' + #13#10#13#10 +
         'Open the official Ollama for Windows page instead?',
         mbInformation,
         MB_YESNO or MB_DEFBUTTON1) = IDYES then
    begin
      ShellExec(
        'open',
        'https://docs.ollama.com/windows',
        '',
        '',
        SW_SHOWNORMAL,
        ewNoWait,
        ShellErrorCode);
    end;
    Exit;
  end;

  WizardForm.StatusLabel.Caption := 'Installing Ollama for local AI cleanup...';
  if not Exec(
       WingetPath,
       'install --id Ollama.Ollama --exact --source winget --scope user --silent ' +
       '--accept-package-agreements --accept-source-agreements --no-upgrade --disable-interactivity',
       '',
       SW_HIDE,
       ewWaitUntilTerminated,
       ResultCode) then
  begin
    MsgBox(
      'Whispdows could not start Windows Package Manager. Ollama was not installed.' + #13#10#13#10 +
      'You can install it later from https://docs.ollama.com/windows.',
      mbError,
      MB_OK);
  end
  else if ResultCode <> 0 then
  begin
    MsgBox(
      'Ollama installation did not complete (Windows Package Manager exit code ' +
      IntToStr(ResultCode) + ').' + #13#10#13#10 +
      'Whispdows will still work with basic cleanup. You can install Ollama later from ' +
      'https://docs.ollama.com/windows.',
      mbError,
      MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallOllamaIfRequested;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  RemoveUserDataOnUninstall := False;
  if not UninstallSilent then
    RemoveUserDataOnUninstall :=
      MsgBox(
        'Remove Whispdows settings, API keys, and logs for this Windows user?',
        mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupValue: String;
  ExpectedStartupValue: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    ExpectedStartupValue := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '"';
    if RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'Whispdows',
      StartupValue) and
      (CompareText(StartupValue, ExpectedStartupValue) = 0) then
    begin
      RegDeleteValue(
        HKCU,
        'Software\Microsoft\Windows\CurrentVersion\Run',
        'Whispdows');
    end;

    if RemoveUserDataOnUninstall then
      DelTree(
        ExpandConstant('{localappdata}\Whispdows'),
        True,
        True,
        True);
  end;
end;
