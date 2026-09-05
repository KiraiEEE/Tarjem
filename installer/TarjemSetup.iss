; Inno Setup Script for Tarjem
; Requires Inno Setup 6.3+ for dark mode support
;
; Compile:    iscc TarjemSetup.iss
; Silent:     TarjemSetup.exe /VERYSILENT /NORESTART /SUPPRESSMSGBOXES

#define MyAppName "Tarjem"
#define MyAppVersion "0.5.0"
#define MyAppPublisher "KiraiEEE"
#define MyAppURL "https://kiraieee.github.io"
#define MyAppExeName "Tarjem.exe"
#define MyPublishDir "..\bin\Release\net8.0-windows10.0.19041.0\win-x64"
#define DotNetRuntimeVersion "8.0"
#define DotNetRuntimeInstaller "windowsdesktop-runtime-8.0.11-win-x64.exe"
#define DotNetRuntimeUrl "https://download.visualstudio.microsoft.com/download/pr/907765b0-0588-4258-b156-291f15088041/c8eabb02b08c03e29f1f8b05c3c44f39/windowsdesktop-runtime-8.0.11-win-x64.exe"

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
LZMANumBlockThreads=4
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
Source: "{#MyPublishDir}undled.keys"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "English to Arabic screen translator"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Tarjem"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; (New-Object Net.WebClient).DownloadFile('https://download.visualstudio.microsoft.com/download/pr/907765b0-0588-4258-b156-291f15088041/c8eabb02b08c03e29f1f8b05c3c44f39/windowsdesktop-runtime-8.0.11-win-x64.exe', '{tmp}\dotnetruntime.exe')"""; StatusMsg: "Downloading .NET Desktop Runtime 8.0..."; Flags: runhidden waituntilterminated; Check: IsDotNetRequired
Filename: "{tmp}\dotnetruntime.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET Desktop Runtime 8.0..."; Flags: waituntilterminated; Check: IsDotNetRequired
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  DotNetRequired: Boolean;

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
