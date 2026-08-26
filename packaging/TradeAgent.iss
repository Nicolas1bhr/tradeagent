; Inno Setup script for TradeAgent.
; Built by packaging/build.ps1, which passes StageDir, OutDir and AppVersion.
; Requires Inno Setup 6.3 or newer (ArchitecturesAllowed=x64compatible).
;
; Deliberately boring: install files, create shortcuts, register an uninstaller. It does NOT install
; SDKs, edit PATH, or touch ATAS. Anything that needs to happen inside ATAS is done by the app at
; runtime, where it can be verified and undone.
;
; It also never opens a console. There is exactly one [Run] entry and it is the application itself;
; no cmd.exe, no PowerShell, no post-install script. That is not an accident to be tidied away later
; - a command window appearing during setup would be the first thing this product promises will
; never happen to the user.

#ifndef StageDir
  #define StageDir "..\artifacts\stage"
#endif
#ifndef OutDir
  #define OutDir "..\artifacts"
#endif
; build.ps1 passes AppVersion in, read from Directory.Build.props, so the installer cannot drift
; away from the assemblies inside it. The fallback below only matters when ISCC is run by hand.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "TradeAgent"

[Setup]
AppId={{7B2F5C64-4C1E-4C7F-9C5E-1D3A2B7E9F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=TradeAgent
VersionInfoVersion={#AppVersion}

; Everything about this install is per-user, and that is the point: no administrator, no consent
; prompt, nothing outside this account's own folders. Under PrivilegesRequired=lowest, {autopf}
; resolves to {userpf} = %LOCALAPPDATA%\Programs, {group} to this user's Start Menu, and
; {autodesktop} to this user's Desktop - so no directive below needs a per-user variant.
; Everything the app installs afterwards (Node, the AI tool, its own working folder) goes to
; %LOCALAPPDATA%\TradeAgent by the same rule.
;
; PrivilegesRequiredOverridesAllowed is 'commandline' rather than 'dialog' on purpose. 'dialog' put
; an all-users/just-me question in front of a non-technical user as the very first screen, where the
; wrong answer triggers an elevation prompt and installs into Program Files. Nobody needs to answer
; that question; an administrator who genuinely wants a machine-wide install can still pass
; /ALLUSERS.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\TradeAgent.exe
UninstallDisplayName={#AppName}
OutputDir={#OutDir}
OutputBaseFilename=TradeAgent-Setup-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
WizardStyle=modern
MinVersion=10.0.22000

; Installing over a running copy used to fail silently on a locked TradeAgent.exe, leaving a
; half-replaced install. Two mechanisms, because they cover different cases:
;
;   CloseApplications uses the Restart Manager to find which processes hold the files about to be
;   replaced and shows the user their names. It needs no cooperation from the application, so it
;   works today.
;
;   AppMutex checks for a named mutex before Setup copies anything, which is the earlier and
;   friendlier failure. It is INERT until the application creates a mutex with this exact name at
;   startup - TradeAgent's single-instance guard is currently a file lock, which Setup cannot see.
;   Adding the mutex in the app is a one-line change; until then this line costs nothing and
;   CloseApplications carries the case on its own.
AppMutex=TradeAgent.SingleInstance
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
; The [Run] entry below is the single, deliberate launch. Letting the Restart Manager also restart
; what it closed would start the app twice, and two instances fighting over one gateway is exactly
; what the app's single-instance guard exists to prevent.
RestartApplications=no

; SetupIconFile, WizardImageFile and WizardSmallImageFile are deliberately absent: this repository
; contains no icon or bitmap, and naming a file that does not exist fails the ISCC compile. Setup
; and the application both use the default icons until real artwork exists.

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
; postinstall  - a ticked checkbox on the final page, so finishing setup opens the app.
; nowait       - Setup closes instead of waiting for a trading application to exit.
; skipifsilent - an unattended install should install, not launch a window at somebody.
; runasoriginaluser - only has an effect if Setup was elevated via /ALLUSERS. TradeAgent keeps its
;                database, its workspace and its tools in %LOCALAPPDATA%; launched as the
;                administrator, it would create all of that in the wrong profile.
; Kept on one line: line continuation in an Inno section is not something this build can test here.
Filename: "{app}\TradeAgent.exe"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

; Uninstall leaves %LOCALAPPDATA%\TradeAgent in place on purpose: it holds the user's trading records
; and the AI's work. Removing an audit trail during an uninstall is not a decision an installer makes.
[UninstallDelete]
Type: filesandordirs; Name: "{app}\bridge"
