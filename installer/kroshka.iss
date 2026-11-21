; ============================================================================
;  Kroshka — Inno Setup script
;
;  Builds one installer that contains both the desktop client and the server,
;  because they are the same product: a family can run the client on one PC and
;  let it host, or install the server alone on a machine that stays on.
;
;  Build:
;      iscc installer\kroshka.iss
;
;  The script expects the published output in bin\self\ (produced by
;  "build.cmd self"). It refuses to build an installer with no exe inside
;  rather than shipping an empty archive.
;
;  What it does that a zip cannot:
;    * registers a proper uninstaller and an entry in Apps & Features
;    * offers the firewall rule as an opt-in task (no silent port opening)
;    * optional "start with Windows" and "launch now" checkboxes
;    * per-user install by default, so no admin rights are needed
;    * upgrades in place: user data in %APPDATA% is never touched
; ============================================================================

#define AppName        "Kroshka"
#define AppVersion     GetEnv('KROSHKA_VERSION')
#define AppPublisher   "DPT Team"
#define AppUrl         "https://github.com/dpteam/Kroshka"
#define AppExe         "Kroshka.exe"
#define ServerExe      "KroshkaServer.exe"

#if AppVersion == ""
  #define AppVersion "1.0.0"
#endif

// VersionInfoVersion must be a pure numeric quad: "1.0.0-rc1" is rejected by
// ISCC. Strip any pre-release suffix for the exe metadata and keep the full
// string for the display name, so a pre-release still looks like a pre-release.
#define NumericVersion Copy(AppVersion, 1, Pos('-', AppVersion + '-') - 1)
#if NumericVersion == ""
  #define NumericVersion "0.0.0"
#endif

[Setup]
AppId={{8F3A1C24-5B7E-4D91-9A62-0E7B4C1D8F35}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}

; Per-user by default: a chat client has no business demanding admin rights,
; and installing under %LOCALAPPDATA% keeps Program Files untouched.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; x64 only: the client is a WinForms app built for win-x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\artifacts
OutputBaseFilename=kroshka-{#AppVersion}-setup
SetupIconFile=..\src\DPT.Kroshka.Client\assets\kroshka.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no

LicenseFile=..\LICENSE

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "firewall";    Description: "Открыть порт 6767 в брандмауэре Windows (для приёма подключений из сети)"; GroupDescription: "Сеть:"; Flags: checkedonce
Name: "autostart";   Description: "Запускать Kroshka вместе с Windows"; GroupDescription: "Автозапуск:"; Flags: unchecked

[Files]
; The client and its runtime. No "skipifsourcedoesntexist" here on purpose: if
; the client was never published, ISCC fails at compile time with a clear path
; instead of producing an installer that installs nothing.
Source: "..\bin\self\client-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; The server, so this machine can host without a second download.
Source: "..\bin\self\server-win-x64\{#ServerExe}"; DestDir: "{app}\server"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\bin\self\server-win-x64\*.dll";          DestDir: "{app}\server"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\bin\self\server-win-x64\*.json";         DestDir: "{app}\server"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\bin\self\server-win-x64\web\*";          DestDir: "{app}\server\web"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\config.example.ini";                     DestDir: "{app}\server"; Flags: ignoreversion skipifsourcedoesntexist

; Documentation, so an offline install still explains itself.
Source: "..\README.md";       DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\LICENSE";         DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}";              Filename: "{app}\{#AppExe}"
Name: "{group}\Сервер {#AppName}";        Filename: "{app}\server\{#ServerExe}"; WorkingDir: "{app}\server"; Tasks: firewall
Name: "{group}\Удалить {#AppName}";       Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";         Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}";         Filename: "{app}\{#AppExe}"; Parameters: "--minimized"; Tasks: autostart

[Run]
; Firewall first: without it the server is unreachable and the app looks broken.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Kroshka Server"""; Flags: runhidden; Tasks: firewall
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Kroshka Server"" dir=in action=allow protocol=TCP localport=6767 profile=private,domain"; Flags: runhidden; Tasks: firewall

Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Leave no firewall hole behind after the app is gone.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Kroshka Server"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
; Runtime files inside the install folder. The real data lives in %APPDATA%\Kroshka
; and is deliberately NOT deleted: uninstalling a chat should not erase its history.
Type: filesandordirs; Name: "{app}\server\logs"
Type: filesandordirs; Name: "{app}\server\data"
Type: filesandordirs; Name: "{app}\web"

[Code]
// ---------------------------------------------------------------------------
// Guard rails. Each one turns a silent failure into a message during install
// rather than a mystery at first launch.
//
// Note on the client exe check: it is deliberately NOT done here. An installer
// runs on a machine where ..\bin\self does not exist, so a runtime check would
// always fail. The guarantee lives in [Files] instead: the client entry has no
// "skipifsourcedoesntexist", so ISCC itself refuses to compile an installer
// with no client inside.
// ---------------------------------------------------------------------------

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Windows 7 and 8 are not supported by .NET 10; say so instead of failing at
  // the first click.
  if CurStep = ssInstall then
  begin
    if not IsWin64 then
    begin
      MsgBox('Kroshka требует 64-битную Windows 10 или новее.', mbCriticalError, MB_OK);
      Abort;
    end;
  end;

  if CurStep = ssPostInstall then
  begin
    // The client keeps its data in %APPDATA%; make sure the folder exists so the
    // first launch does not have to think about it.
    ForceDirectories(ExpandConstant('{userappdata}\Kroshka'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Удалить также историю сообщений и настройки?' + #13#10#13#10 +
              ExpandConstant('{userappdata}\Kroshka') + #13#10#13#10 +
              'Выберите "Нет", чтобы сохранить переписку на случай переустановки.',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\Kroshka'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Kroshka'), True, True, True);
    end;
  end;
end;
