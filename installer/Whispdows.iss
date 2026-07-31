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

[Types]
Name: "transcribe"; Description: "Transcribe only"
Name: "meetingnotes"; Description: "Meeting Notes only"
Name: "both"; Description: "Transcribe and Meeting Notes"

[Components]
Name: "transcribe"; Description: "Hold-to-talk transcription"; Types: transcribe both
Name: "meetingnotes"; Description: "Meeting recording and notes"; Types: meetingnotes both

[Tasks]
Name: "startup"; Description: "Launch Whispdows when I sign in"; GroupDescription: "Startup:"; Flags: unchecked checkedonce

[Dirs]
Name: "{localappdata}\Whispdows"; Flags: uninsneveruninstall

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "models\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\models\ggml-small.en.bin"; DestDir: "{app}\models"; Components: transcribe; Flags: ignoreversion
Source: "{#PublishDir}\models\ggml-medium.en.bin"; DestDir: "{app}\models"; Components: meetingnotes; Flags: ignoreversion
Source: "{#PublishDir}\settings.example.json"; DestDir: "{localappdata}\Whispdows"; DestName: "settings.json"; Flags: onlyifdoesntexist uninsneveruninstall

[InstallDelete]
Type: files; Name: "{app}\models\ggml-small.en.bin"; Check: not WizardIsComponentSelected('transcribe')
Type: files; Name: "{app}\models\ggml-medium.en.bin"; Check: not WizardIsComponentSelected('meetingnotes')

[Icons]
Name: "{group}\Whispdows"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\README"; Filename: "{app}\README.md"
Name: "{group}\Uninstall Whispdows"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--configure-features=transcribe"; Check: WizardIsComponentSelected('transcribe') and not WizardIsComponentSelected('meetingnotes'); Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Parameters: "--configure-features=meeting-notes"; Check: WizardIsComponentSelected('meetingnotes') and not WizardIsComponentSelected('transcribe'); Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Parameters: "--configure-features=both"; Check: WizardIsComponentSelected('transcribe') and WizardIsComponentSelected('meetingnotes'); Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Parameters: "--enable-startup"; Tasks: startup; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Whispdows"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserDataOnUninstall: Boolean;

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
