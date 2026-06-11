; Steam Switch Inno Setup Script
; https://jrsoftware.org/isinfo.php

#define MyAppName "Steam Switch"
#define MyAppVersion "2.3.0"
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
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"; LicenseFile: LICENSE
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
chinesesimplified.BeveledLabel=中文安装程序
english.BeveledLabel=English Setup

[CustomMessages]
chinesesimplified.SetupWindowTitle=安装 - %1
chinesesimplified.ExitSetupMessage=安装程序尚未完成安装。如果你现在退出，程序将不会被安装。%n%n你可以稍后再次运行安装程序完成安装。%n%n确定要退出安装吗？
chinesesimplified.RunEntryExec=运行 %1
chinesesimplified.RunEntryShellExec=查看 %1
chinesesimplified.WelcomeLabel1=欢迎安装 %1
chinesesimplified.WelcomeLabel2=这将在你的计算机上安装 %1 %2。%n%n建议你在继续之前关闭所有其他应用程序。
chinesesimplified.LicenseLabel=请仔细阅读以下许可协议：
chinesesimplified.LicenseAccepted=我同意此协议(&A)
chinesesimplified.LicenseNotAccepted=我不同意此协议(&D)
chinesesimplified.SetupTypeTitle=选择安装类型
chinesesimplified.SetupTypeDescription=你希望以哪种方式安装程序？
chinesesimplified.TypesCustomDescription=选择要安装的组件以及安装位置。
chinesesimplified.TypesRecommendedDescription=安装程序将安装推荐的组件。
chinesesimplified.TypesFullDescription=将安装程序的所有组件。
chinesesimplified.TypesCompactDescription=仅安装程序运行必需的组件。
chinesesimplified.TypesCustomCustomDescription=选择要安装的组件。
chinesesimplified.DirectoryBrowseLabel=选择安装位置：
chinesesimplified.DirectoryBrowseLabel2=安装程序将把 %1 安装到以下文件夹。
chinesesimplified.SelectStartMenuFolderTitle=选择开始菜单文件夹
chinesesimplified.SelectStartMenuFolderLabel2=安装程序将在以下开始菜单文件夹中创建程序的快捷方式。
chinesesimplified.SelectComponentsTitle=选择组件
chinesesimplified.SelectComponentsLabel2=选择要安装的组件，清除不需要安装的组件。
chinesesimplified.SelectTasksTitle=选择附加任务
chinesesimplified.SelectTasksLabel2=选择安装程序在安装 %1 时需要执行的附加任务。
chinesesimplified.ReadyTitle=准备安装
chinesesimplified.ReadyLabel1=安装程序现在准备在你的计算机上安装 %1。
chinesesimplified.ReadyLabel2=点击"安装"开始安装，或者点击"上一步"检查或更改设置。
chinesesimplified.SelectDirBrowseLabel=为 %1 选择安装文件夹：
chinesesimplified.SelectStartMenuFolderBrowseLabel=选择开始菜单文件夹：
chinesesimplified.ClickInstall=点击"安装"开始安装。
chinesesimplified.ChangingIconTitle=选择图标
chinesesimplified.ChangingIconLabel=为 %1 选择图标：
chinesesimplified.PreparingToInstallTitle=准备安装
chinesesimplified.PreparingToInstallMessage=安装程序正在准备在你的计算机上安装 %1。请稍候...
chinesesimplified.InstallingTitle=正在安装
chinesesimplified.InstallingLabel=请稍候，安装程序正在将 %1 安装到你的计算机上。
chinesesimplified.CompletedTitle=安装完成
chinesesimplified.CompletedLabel=安装程序已在你的计算机上完成 %1 的安装。可以通过选择安装的快捷方式来启动程序。
chinesesimplified.FinishedHeadingLabel=完成 %1 安装向导
chinesesimplified.FinishedLabelNoIcons=安装程序已在你的计算机上完成 %1 的安装。
chinesesimplified.FinishedLabel=安装程序已在你的计算机上完成 %1 的安装。可以通过选择安装的快捷方式来启动程序。
chinesesimplified.FinishedLabel2=点击"完成"退出安装向导。
chinesesimplified.ClickFinish=点击"完成"退出安装向导。
chinesesimplified.AssocFileExtension=将 %1 与 %2 文件扩展名关联(&A)
chinesesimplified.AssocingFileExtension=正在将 %1 与 %2 文件扩展名关联...
chinesesimplified.AutoStartProgramLogon=登录时自动启动(&S)
chinesesimplified.AutoStartProgramGroupDescription=启动项：
chinesesimplified.AdditionalIcons=附加图标：
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式(&D)
chinesesimplified.CreateQuickLaunchIcon=创建快速启动快捷方式(&Q)
chinesesimplified.ProgramOnTheWeb=%1 网站
chinesesimplified.UninstallProgram=卸载 %1
chinesesimplified.LaunchProgram=运行 %1(&L)
chinesesimplified.AssocFileExtensionTitle=文件关联
chinesesimplified.AssocingFileExtensionTitle2=正在将 %1 与 %2 文件扩展名关联...
chinesesimplified.DownloadFailed=下载失败。
chinesesimplified.RetryDownloadTitle=重试下载
chinesesimplified.RetryDownloadLabel=下载失败。是否重试？
chinesesimplified.DownloadingLabel=正在下载 %1...
chinesesimplified.DownloadingLabel2=请稍候，正在从 %3 下载 %1 (%2)...
chinesesimplified.RetryDownloadMessage=下载失败。%n%n%1%n%n是否重试？
chinesesimplified.AbortRetryIgnore=终止(&A) | 重试(&R) | 忽略(&I)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startwithwindows"; Description: "Start with Windows"; GroupDescription: "Other options:"

[Files]
Source: "release\SteamSwitch\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "release\SteamSwitch\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:ProgramOnTheWeb,{#MyAppName}}"; Filename: "{#MyAppURL}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Registry]
; 开机自启动
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SteamSwitch"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startwithwindows; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\SteamSwitch"

[Code]
// 检查是否已安装
function GetUninstallString(): String;
var
  sUnInstPath: String;
  sUnInstallString: String;
begin
  sUnInstPath := ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1');
  sUnInstallString := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstallString) then
    RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstallString);
  Result := sUnInstallString;
end;

function IsUpgrade(): Boolean;
begin
  Result := (GetUninstallString() <> '');
end;

function UnInstallOldVersion(): Integer;
var
  sUnInstallString: String;
  iResultCode: Integer;
begin
  Result := 0;
  sUnInstallString := GetUninstallString();
  if sUnInstallString <> '' then begin
    sUnInstallString := RemoveQuotes(sUnInstallString);
    if Exec(sUnInstallString, '/SILENT /NORESTART /SUPPRESSMSGBOXES','', SW_SHOW, ewWaitUntilTerminated, iResultCode) then
      Result := 3
    else
      Result := 2;
  end else
    Result := 1;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep=ssInstall) then begin
    if (IsUpgrade()) then
      UnInstallOldVersion();
  end;
end;
