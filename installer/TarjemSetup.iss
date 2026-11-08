; Inno Setup Script for Tarjem
; Requires Inno Setup 6.3+ for dark mode support
;
; Compile:    iscc TarjemSetup.iss
; Silent:     TarjemSetup.exe /VERYSILENT /NORESTART /SUPPRESSMSGBOXES

#define MyAppName "Tarjem"
#define MyAppVersion "0.8.0"
#define MyAppPublisher "KiraiEEE"
#define MyAppURL "https://kiraieee.github.io"
#define MyAppExeName "Tarjem.exe"
; Output of: dotnet publish Tarjem.csproj -c Release -r win-x64 --self-contained false -o bin\publish\win-x64
#define MyPublishDir "..\bin\publish\win-x64"
#define DotNetRuntimeVersion "8.0"
; Microsoft's evergreen link: always resolves to the newest 8.0.x Windows Desktop Runtime, so the
; installer never goes stale the way a pinned download/pr/... URL does.
#define DotNetRuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
#define DotNetRuntimeFile "windowsdesktop-runtime-8.0-win-x64.exe"

[Setup]
AppId={{B8A3C4D5-E6F7-4A8B-9C0D-1E2F3A4B5C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=TarjemSetup-{#MyAppVersion}
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
LZMADictionarySize=1048576
LZMANumFastBytes=273
SolidCompression=yes
WizardStyle=modern dark
WizardSizePercent=100
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=auto
DisableReadyPage=no
DisableFinishedPage=no
CloseApplications=force
CloseApplicationsFilter=Tarjem.exe
RestartApplications=no
MinVersion=10.0.17763
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\Assets\icon.ico
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startmenuicon"; Description: "Create Start Menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "autostart"; Description: "Start with Windows"; GroupDescription: "Startup:"; Flags: checkedonce

[Files]
; Everything the publish produced, rather than a hand-written list of DLLs. That list silently
; went stale when a package was added - System.Security.Cryptography.ProtectedData.dll was
; missing, which would have broken the encrypted key store on a clean machine with no error
; anyone could act on.
;
; The excludes matter as much as the include:
;   .env      - developer-only plaintext API keys. Shipping one is exactly what got the previous
;               key auto-flagged and revoked. It must never leave this machine.
;   *.pdb     - debug symbols, no value to a user and ~160 KB.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,.env,*.env";     Flags: ignoreversion recursesubdirs createallsubdirs

; The bundled free API keys, obfuscated (never a plaintext .env). "skipifsourcedoesntexist" so a
; build made without running `Tarjem.exe --pack-keys` still packages; the app then runs on the
; keyless sources, which are the defaults anyway.
Source: "{#MyPublishDir}\bundled.keys"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Screen translator overlay"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Tarjem"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  DotNetRequired: Boolean;
  DownloadPage: TDownloadWizardPage;

{ Whether a .NET 8 (or newer) Windows Desktop runtime is present.

  The previous implementation asked RegValueExists for a value literally named "8.0". The values
  under that key are named for the *full* version - "8.0.11", "8.0.14" - so it never matched, and
  every install downloaded and reinstalled a ~55 MB runtime the machine already had.

  The shared-framework folder is the authoritative answer and is what the runtime host itself
  looks at, so that is checked first; the registry is kept as a fallback for unusual layouts. }
function IsDotNet8Installed: Boolean;
var
  SharedDir: String;
  FindRec: TFindRec;
  Major: Integer;
begin
  Result := False;

  SharedDir := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(SharedDir) then
  begin
    if FindFirst(SharedDir + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          begin
            { Folder names are full versions; anything 8 or newer runs a net8.0 app. }
            Major := StrToIntDef(Copy(FindRec.Name, 1, Pos('.', FindRec.Name) - 1), 0);
            if Major >= 8 then
            begin
              Result := True;
              Break;
            end;
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;

  if not Result then
    Result := RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App');
end;

function IsDotNetRequired: Boolean;
begin
  Result := DotNetRequired;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  DotNetRequired := not IsDotNet8Installed;
  if DotNetRequired and not WizardSilent then
  begin
    if MsgBox('{#MyAppName} needs the Microsoft .NET {#DotNetRuntimeVersion} Desktop Runtime, which is not installed on this PC.' + #13#10#13#10 +
              'Setup will download it from Microsoft (about 55 MB) and install it. Continue?',
              mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
    'Downloading the Microsoft .NET {#DotNetRuntimeVersion} Desktop Runtime...', @OnDownloadProgress);
end;

{ Installs the runtime before any app files are copied. Returning a non-empty string aborts setup
  with that message, which is what we want: installing an app that cannot start helps nobody. }
function InstallDotNet: String;
var
  ResultCode: Integer;
  Installer: String;
begin
  Result := '';
  DownloadPage.Clear;
  DownloadPage.Add('{#DotNetRuntimeUrl}', '{#DotNetRuntimeFile}', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        Result := 'The .NET runtime download was cancelled.'
      else
        Result := 'Could not download the .NET {#DotNetRuntimeVersion} Desktop Runtime: ' + GetExceptionMessage + #13#10#13#10 +
                  'Install it manually from https://dotnet.microsoft.com/download/dotnet/8.0 and run this setup again.';
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  Installer := ExpandConstant('{tmp}\{#DotNetRuntimeFile}');
  WizardForm.StatusLabel.Caption := 'Installing the Microsoft .NET {#DotNetRuntimeVersion} Desktop Runtime...';
  { The runtime installer needs admin rights; it raises its own UAC prompt even when this setup runs
    per-user. 0 = success, 3010 = success but a reboot is pending, 1638 = a newer version exists. }
  if not ShellExec('runas', Installer, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Could not start the .NET runtime installer: ' + SysErrorMessage(ResultCode)
  else if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1638) then
    Result := 'The .NET runtime installer failed (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
              'Install it manually from https://dotnet.microsoft.com/download/dotnet/8.0 and run this setup again.'
  else if not IsDotNet8Installed then
    Result := 'The .NET runtime installer finished but the runtime was not found. Please install it manually from https://dotnet.microsoft.com/download/dotnet/8.0';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if DotNetRequired then
    Result := InstallDotNet;
end;

function InitializeUninstall: Boolean;
var
  DataDir: String;
  ResultCode: Integer;
begin
  Result := True;
  DataDir := ExpandConstant('{localappdata}\Tarjem');

  if DirExists(DataDir) then
  begin
    ResultCode := MsgBox(
      'Would you like to remove all Tarjem data?' + #13#10#13#10 +
      'This includes translation history and settings.' + #13#10 +
      'Location: ' + DataDir,
      mbConfirmation,
      MB_YESNO or MB_DEFBUTTON2
    );
    if ResultCode = IDYES then
      DelTree(DataDir, True, True, True);
  end;
end;
