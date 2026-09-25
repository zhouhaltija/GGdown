; GGdown installer (Inno Setup 7). Per-user install, no admin.
; Preprocessor defines: /DAppVersion=x.y.z  /DRepoRoot=<repo root>

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
#ifndef RepoRoot
#define RepoRoot ".."
#endif

[Setup]
AppId={{A4E1C7B2-6D58-4F0E-9C3A-1B7E5D94F260}
AppName=GGdown
AppVersion={#AppVersion}
AppPublisher=GGdown Project
DefaultDirName={localappdata}\Programs\GGdown
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=GGdown-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\GGdown.exe
SetupIconFile={#RepoRoot}\src\GGdown.App\Assets\app.ico
; User data under {localappdata}\GGdown is intentionally preserved on uninstall.
UninstallFilesDir={app}\unins

[Files]
; App (dotnet publish output)
Source: "{#RepoRoot}\dist\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Engine (python embeddable + runner + sites + site-packages)
Source: "{#RepoRoot}\dist\engine\*"; DestDir: "{app}\engine"; Flags: recursesubdirs createallsubdirs ignoreversion
; GPL assets
Source: "{#RepoRoot}\dist\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GGdown"; Filename: "{app}\GGdown.exe"

[Messages]
ConfirmUninstall=Uninstall GGdown?%n%nNote: user data (%%LOCALAPPDATA%%\GGdown - download history and archive) is kept and can be deleted manually.

[Run]
Filename: "{app}\GGdown.exe"; Description: "Launch GGdown"; Flags: nowait postinstall skipifsilent
