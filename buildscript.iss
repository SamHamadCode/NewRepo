; ============================================================
;  MonitorBot — Inno Setup Build Script
;  Requires: Inno Setup 6.x  (https://jrsoftware.org/isinfo.php)
;
;  Before running:
;    dotnet publish src/MonitorBot.App/MonitorBot.App.csproj ^
;      -c Release -r win-x64 --self-contained true -o publish
;
;  Then open this file in Inno Setup Compiler and click Build.
; ============================================================

#define AppName      "MonitorBot"
#define AppVersion   "1.0.0"
#define AppPublisher "SamHamad"
#define AppExeName   "MonitorBot.App.exe"
#define PublishDir   "publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisherURL=https://github.com/SamHamad1/NewRepo
AppSupportURL=https://github.com/SamHamad1/NewRepo
AppUpdatesURL=https://github.com/SamHamad1/NewRepo
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
; Output installer file
OutputDir=installer
OutputBaseFilename=MonitorBot_Setup_v{#AppVersion}
; Compression
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Require 64-bit Windows
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
; Minimum Windows 10
MinVersion=10.0
; Installer appearance
WizardStyle=modern
WizardSizePercent=110
; UAC — request admin so we can write to Program Files
PrivilegesRequired=admin
; No restart needed
RestartIfNeededByRun=no
; Add uninstaller to Control Panel
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";    Description: "{cm:CreateDesktopIcon}";    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1

[Files]
; ?? Main application ??????????????????????????????????????
Source: "{#PublishDir}\{#AppExeName}";       DestDir: "{app}"; Flags: ignoreversion

; ?? All DLLs and runtime files ????????????????????????????
Source: "{#PublishDir}\*.dll";               DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\*.json";              DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.pdb";               DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; ?? Playwright browser binaries ???????????????????????????
; The .playwright folder contains Chromium used for checkout/stock checking
Source: "{#PublishDir}\.playwright\*";       DestDir: "{app}\.playwright"; Flags: ignoreversion recursesubdirs createallsubdirs

; ?? Localization resource folders ?????????????????????????
Source: "{#PublishDir}\cs\*";    DestDir: "{app}\cs";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\de\*";    DestDir: "{app}\de";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\es\*";    DestDir: "{app}\es";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\fr\*";    DestDir: "{app}\fr";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\it\*";    DestDir: "{app}\it";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\ja\*";    DestDir: "{app}\ja";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\ko\*";    DestDir: "{app}\ko";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\pl\*";    DestDir: "{app}\pl";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\pt-BR\*"; DestDir: "{app}\pt-BR"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\ru\*";    DestDir: "{app}\ru";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\tr\*";    DestDir: "{app}\tr";    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\zh-Hans\*"; DestDir: "{app}\zh-Hans"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#PublishDir}\zh-Hant\*"; DestDir: "{app}\zh-Hant"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
; Start menu shortcut
Name: "{group}\{#AppName}";                  Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}";        Filename: "{uninstallexe}"
; Desktop shortcut (optional, user chooses during install)
Name: "{autodesktop}\{#AppName}";            Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
; Quick launch (Windows XP/Vista only)
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: quicklaunchicon

[Run]
; Offer to launch the app after install finishes
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up user data folder on uninstall (optional — comment out to keep user data)
; Type: filesandordirs; Name: "{localappdata}\MonitorBot"
Type: filesandordirs; Name: "{app}"

[Code]
// ?? Detect .NET 5 Desktop Runtime ????????????????????????????????????????????
// MonitorBot is self-contained (--self-contained true) so .NET is bundled.
// No runtime check needed. Remove this section if you switch to framework-dependent.

procedure InitializeWizard();
begin
  // Nothing needed for self-contained publish
end;
