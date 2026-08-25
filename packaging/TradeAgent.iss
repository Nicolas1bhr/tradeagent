; Inno Setup script for TradeAgent.
; Built by packaging/build.ps1, which passes StageDir and OutDir.
;
; Deliberately boring: install files, create shortcuts, register an uninstaller. It does NOT install
; SDKs, edit PATH, or touch ATAS. Anything that needs to happen inside ATAS is done by the app at
; runtime, where it can be verified and undone.

#ifndef StageDir
  #define StageDir "..\artifacts\stage"
#endif
#ifndef OutDir
  #define OutDir "..\artifacts"
#endif
#define AppName "TradeAgent"
#define AppVersion "0.1.0"

[Setup]
AppId={{7B2F5C64-4C1E-4C7F-9C5E-1D3A2B7E9F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=TradeAgent
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\TradeAgent.exe
OutputDir={#OutDir}
OutputBaseFilename=TradeAgent-Setup-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
WizardStyle=modern
MinVersion=10.0.22000

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\TradeAgent.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\TradeAgent.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Run]
Filename: "{app}\TradeAgent.exe"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

; Uninstall leaves %LOCALAPPDATA%\TradeAgent in place on purpose: it holds the user's trading records
; and the AI's work. Removing an audit trail during an uninstall is not a decision an installer makes.
[UninstallDelete]
Type: filesandordirs; Name: "{app}\bridge"
