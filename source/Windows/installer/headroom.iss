; Headroom setup.exe
;
; Inno Setup 6.3 or newer. ChineseSimplified.isl sits beside this script
; (user-contributed translation; the 6.7 installer does not ship it).
; x64compatible is the 6.3 replacement for the deprecated x64 identifier:
; x64 Windows, and ARM64 Windows running this x64 build under emulation.
; CopyFile replaced FileCopy in 6.4; 6.3 still compiles the old name.
; Build from the repo:  .\source\Windows\build.ps1 -Installer
; Or, after build.ps1 has produced the executables in the repo root:
;   ISCC.exe source\Windows\installer\headroom.iss
;
#if Ver < EncodeVer(6, 3, 0)
  #error Headroom setup requires Inno Setup 6.3 or newer
#endif

; AppId is stable on purpose: upgrades replace program files and keep the data. Do not
; change it. Versions up to 2.0.1 installed codeusagemonit.exe under the same AppId; an
; upgrade deletes that exe and its registry key, and on first start the app moves
; %LOCALAPPDATA%\codeusagemonit to %LOCALAPPDATA%\Headroom and renames the autostart entry.
;
; Data: this installer writes installed.txt and HKCU\Software\Headroom\InstallPath.
; The app then stores settings in %LOCALAPPDATA%\Headroom (not next to the exe),
; so Program Files works. The zip has neither marker and keeps using <exe>\data.
; portable.txt next to the exe forces that portable layout.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif
#ifndef BuildDir
  #define BuildDir "..\..\.."
#endif
#ifndef RepoRoot
  #define RepoRoot "..\..\.."
#endif

#define MyAppName "余量 Headroom"
#define MyAppPublisher "Headroom"
#define MyAppURL "https://github.com/chinbor/headroom"

[Setup]
AppId={{A4E8C1D7-6F2B-4C9A-8E15-3B7D9F0A2C64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppComments=Windows taskbar widget for AI subscription quotas
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Headroom setup
VersionInfoCopyright=Copyright (C) 2026 Headroom contributors
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowRootDirectory=yes
UsePreviousAppDir=yes
DirExistsWarning=auto
AlwaysShowDirOnReadyPage=yes
OutputDir="{#BuildDir}"
OutputBaseFilename=headroom-setup-{#MyAppVersion}
SetupIconFile="{#BuildDir}\app.ico"
UninstallDisplayIcon={app}\headroom.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile="{#RepoRoot}\LICENSE"
CloseApplications=no
RestartApplications=no
ChangesAssociations=no
ShowLanguageDialog=auto

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.Options=其他选项：
english.Options=Other options:
chinesesimp.AutoStart=登录 Windows 后自动启动（只驻留托盘）
english.AutoStart=Start with Windows (tray only)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:Options}"; Flags: unchecked

[InstallDelete]
; The exe of versions up to 2.0.1.
Type: files; Name: "{app}\codeusagemonit.exe"

[Files]
Source: "{#BuildDir}\headroom.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\app.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\pricing.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\icons\*"; DestDir: "{app}\icons"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#BuildDir}\platforms\*"; DestDir: "{app}\platforms"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\使用说明.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "installed.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "setup-helper.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "setup-helper.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\headroom.exe"; WorkingDir: "{app}"; Comment: "{#MyAppName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\headroom.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Per-user even for an all-users install. installed.txt is what other Windows users see.
Root: HKCU; Subkey: "Software\codeusagemonit"; Flags: deletekey
Root: HKCU; Subkey: "Software\Headroom"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Headroom"; ValueData: """{app}\headroom.exe"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\headroom.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; An update started from inside the app runs this setup with /VERYSILENT /restart=1 and the app
; quits; DeinitializeSetup below opens it again, also when the install did not go through.

[Code]
var
  UninstallHelper: String;
  UserDataDir, BesideDir: String;
  UserLinkCode, BesideLinkCode: Integer;

function PowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(Result) then Result := 'powershell.exe';
end;

function RunHelper3(const Action, Script, Target: String): Integer;
var
  ResultCode: Integer;
  Params: String;
begin
  Result := 1;
  if (Script = '') or (not FileExists(Script)) then Exit;
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script + '" -Action ' + Action + ' -Directory "' + RemoveBackslash(ExpandConstant('{app}')) + '"';
  if Target <> '' then Params := Params + ' -Target "' + Target + '"';
  if Exec(PowerShellExe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode;
end;

function RunHelper(const Action, Script: String): Integer;
begin
  Result := RunHelper3(Action, Script, '');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  ExtractTemporaryFile('setup-helper.ps1');
  if RunHelper('stop', ExpandConstant('{tmp}\setup-helper.ps1')) <> 0 then
    Result := 'Headroom is still running from this folder. Quit it from the tray icon and run setup again.' + #13#10 +
      '这个文件夹里的 余量 Headroom 仍在运行。请在托盘图标上右键退出，然后重新安装。';
end;

{ Setup run by the app's updater (/restart=1, silent). /sidebyside=1 keeps a side-by-side copy so. }
function RelaunchAfterUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:restart|0}') = '1');
end;

function RelaunchParameters: String;
begin
  Result := '--updated';
  if ExpandConstant('{param:sidebyside|0}') = '1' then Result := Result + ' --side-by-side';
end;

procedure DeinitializeSetup;
var
  ResultCode: Integer;
  Exe: String;
begin
  if not RelaunchAfterUpdate then Exit;
  try
    { The new exe after a successful install, the previous one if setup stopped early. }
    Exe := ExpandConstant('{app}\headroom.exe');
    if FileExists(Exe) then
      ExecAsOriginalUser(Exe, RelaunchParameters, ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
  except
    { The app folder is not known when setup stops before it is set: nothing to start then. }
  end;
end;

function InitializeUninstall: Boolean;
begin
  UninstallHelper := ExpandConstant('{tmp}\setup-helper.ps1');
#if Ver >= EncodeVer(6, 4, 0)
  if not CopyFile(ExpandConstant('{app}\setup-helper.ps1'), UninstallHelper, False) then
#else
  if not FileCopy(ExpandConstant('{app}\setup-helper.ps1'), UninstallHelper, False) then
#endif
    UninstallHelper := ExpandConstant('{app}\setup-helper.ps1');
  Result := True;
end;

procedure RemoveRunValue(const Name: String);
var
  Value: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Name, Value) then
    if Pos(Uppercase(ExpandConstant('{app}\')), Uppercase(Value)) > 0 then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Name);
end;

procedure RemoveAutoStart;
begin
  RemoveRunValue('Headroom');
  RemoveRunValue('codeusagemonit');
end;

function IsUserDataDir(const Dir: String): Boolean;
begin
  Result := (Length(Dir) > 16) and (Uppercase(ExtractFileName(RemoveBackslash(Dir))) = 'HEADROOM') and DirExists(Dir);
end;

function IsBesideDataDir(const Dir: String): Boolean;
var
  AppDir: String;
begin
  AppDir := RemoveBackslash(ExpandConstant('{app}'));
  { A drive root would make this "C:\data", which may be unrelated to the app. }
  Result := (Length(AppDir) > 3) and DirExists(Dir) and (Uppercase(RemoveBackslash(Dir)) = Uppercase(AppDir + '\data'));
end;

function OfferUserData: Boolean;
begin
  { is-link exits 0 for a junction/symlink. Anything else is a real directory we created. }
  Result := IsUserDataDir(UserDataDir) and (UserLinkCode <> 0);
end;

function OfferBesideData: Boolean;
begin
  { Exit 2 means "exists and is not a link". A Scoop persist junction must be left alone,
    and a failed check must not delete a folder we could not identify. }
  Result := IsBesideDataDir(BesideDir) and (BesideLinkCode = 2);
end;

function DataPrompt(const UserData, Beside: String): String;
var
  List: String;
begin
  List := '';
  if OfferUserData then List := List + UserData + #13#10;
  if OfferBesideData then List := List + Beside + #13#10;
  if ActiveLanguage = 'english' then
    Result := 'Also delete settings, saved keys and the usage cache?' + #13#10#13#10 + List + #13#10 +
      'Choose No to keep them. A later install will keep using them.'
  else
    Result := '是否同时删除设置、已保存的密钥和用量缓存？' + #13#10#13#10 + List + #13#10 +
      '选“否”会保留这些文件。以后重新安装会继续使用它们。';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserData, Beside: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RunHelper('stop', UninstallHelper);
    RunHelper('remove-path', UninstallHelper);
    RemoveAutoStart;
    UserDataDir := ExpandConstant('{localappdata}\Headroom');
    BesideDir := ExpandConstant('{app}\data');
    { Classify before program files (and a helper that failed to copy out) disappear. }
    UserLinkCode := RunHelper3('is-link', UninstallHelper, UserDataDir);
    BesideLinkCode := RunHelper3('is-link', UninstallHelper, BesideDir);
  end;
  if CurUninstallStep <> usPostUninstall then Exit;
  if UninstallSilent then Exit;
  UserData := UserDataDir;
  Beside := BesideDir;
  if (not OfferUserData) and (not OfferBesideData) then Exit;
  if MsgBox(DataPrompt(UserData, Beside), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then Exit;
  if OfferUserData then DelTree(UserData, True, True, True);
  if OfferBesideData then DelTree(Beside, True, True, True);
end;
