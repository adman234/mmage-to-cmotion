; Inno Setup script. Build with:  iscc /DAppVersion=0.1.0 /DSourceDir=..\publish\win-x64 millway.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

[Setup]
AppId={{6B7F2E0C-52C4-4E0F-9B1B-5E4C5B1E7A11}
AppName=Millway
AppVersion={#AppVersion}
AppPublisher=Millway contributors
AppPublisherURL=https://github.com/adman234/mmage-to-cmotion
DefaultDirName={autopf}\Millway
DefaultGroupName=Millway
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Millway-Setup-{#AppVersion}
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\millway.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequiredOverridesAllowed=dialog

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Millway"; Filename: "{app}\millway.exe"
Name: "{autodesktop}\Millway"; Filename: "{app}\millway.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\millway.exe"; Description: "Start Millway"; Flags: nowait postinstall skipifsilent
