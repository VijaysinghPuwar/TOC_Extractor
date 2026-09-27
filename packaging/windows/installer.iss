; Inno Setup script for the Windows installer. make-installer.ps1 runs it
; after publishing the app; it passes AppVersion, SourceDir and OutputDir.
;
; Installs for the current user by default, so no administrator prompt; the
; person can choose "all users" instead. Chromium and settings live in
; %LOCALAPPDATA%\TOC Extractor, not in the install folder, so uninstalling
; leaves the person's browser sign-ins alone.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F1C2A4E-8B3D-4E7A-9C51-2D7F0B9E4A18}
AppName=TOC Extractor
AppVersion={#AppVersion}
AppVerName=TOC Extractor {#AppVersion}
AppPublisher=Vijaysingh Puwar
AppPublisherURL=https://github.com/VijaysinghPuwar/TOC_Extractor
AppSupportURL=https://github.com/VijaysinghPuwar/TOC_Extractor/issues
DefaultDirName={autopf}\TOC Extractor
DefaultGroupName=TOC Extractor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\icon\TocExtractor.ico
UninstallDisplayIcon={app}\TocExtractor.exe
UninstallDisplayName=TOC Extractor
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=TOC-Extractor-{#AppVersion}-windows-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TOC Extractor"; Filename: "{app}\TocExtractor.exe"
Name: "{autodesktop}\TOC Extractor"; Filename: "{app}\TocExtractor.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TocExtractor.exe"; Description: "{cm:LaunchProgram,TOC Extractor}"; Flags: nowait postinstall skipifsilent
