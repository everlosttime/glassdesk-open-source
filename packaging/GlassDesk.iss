; User-scope x64 installer for the local GlassDesk candidate.
#ifndef AppIdValue
  #error AppIdValue is required.
#endif
#ifndef ProductVersion
  #error ProductVersion is required.
#endif
#ifndef FileVersion
  #error FileVersion is required.
#endif
#ifndef PackageBaseName
  #error PackageBaseName is required.
#endif
#define StageDirectory GetEnv("GLASSDESK_PACKAGE_STAGE")
#if StageDirectory == ""
  #error GLASSDESK_PACKAGE_STAGE is required.
#endif

[Setup]
AppId={#AppIdValue}
AppName=GlassDesk
AppVersion={#ProductVersion}
AppVerName=GlassDesk {#ProductVersion}
DefaultDirName={localappdata}\Programs\GlassDesk
DefaultGroupName=GlassDesk
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UsePreviousAppDir=yes
UninstallDisplayName=GlassDesk
UninstallDisplayIcon={app}\GlassDesk.exe
SetupIconFile=..\src\GlassDesk.App\Assets\GlassDesk.ico
AppPublisher=GlassDesk
VersionInfoProductName=GlassDesk
VersionInfoProductVersion={#FileVersion}
VersionInfoVersion={#FileVersion}
VersionInfoTextVersion={#FileVersion}
VersionInfoProductTextVersion={#ProductVersion}
OutputBaseFilename={#PackageBaseName}-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
Source: "{#StageDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GlassDesk"; Filename: "{app}\GlassDesk.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\GlassDesk"; Filename: "{app}\GlassDesk.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1"; ValueName: "InstallLocation"; ValueType: string; ValueData: "{app}"; Flags: uninsdeletevalue
