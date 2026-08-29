; Steam Switch Inno Setup Script
; https://jrsoftware.org/isinfo.php

#define MyAppName "Steam Switch"
#define MyAppVersion "3.1.2"
#define MyAppPublisher "ddxgtx"
#define MyAppURL "https://github.com/ddxgtx/SteamSwitch"
#define MyAppExeName "SteamSwitch.exe"
#define MyAppAssocName MyAppName + " File"
#define MyAppAssocExt ".steamswitch"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=LICENSE
OutputDir=installer
OutputBaseFilename=SteamSwitch-v{#MyAppVersion}-win-x64-setup
SetupIconFile=src\SteamSwitcher\Resources\steam.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
CloseApplications=yes
CloseApplicationsFilter=SteamSwitch.exe
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoCopyright=Copyright (c) 2024 {#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startwithwindows"; Description: "{cm:AutoStartProgram,{#MyAppName}}"; GroupDescription: "{cm:AutoStartProgramGroupDescription}"

[Files]
Source: "publish_zip\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:ProgramOnTheWeb,{#MyAppName}}"; Filename: "{#MyAppURL}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--register-autostart"; Tasks: startwithwindows; Flags: runasoriginaluser runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Parameters: "--unregister-autostart"; Tasks: not startwithwindows; Flags: runasoriginaluser runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec runasoriginaluser
Filename: "{app}\{#MyAppExeName}"; Parameters: "--updated"; Flags: nowait skipifnotsilent shellexec runasoriginaluser

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--unregister-autostart"; RunOnceId: "SteamSwitchRemoveAutoStart"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure RemoveMainDesktopShortcuts;
begin
  { Exact filename only: account shortcuts use "Steam - ..." and games use their titles. }
  DeleteFile(ExpandConstant('{userdesktop}\{#MyAppName}.lnk'));
  DeleteFile(ExpandConstant('{commondesktop}\{#MyAppName}.lnk'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemoveMainDesktopShortcuts;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveMainDesktopShortcuts;
end;
