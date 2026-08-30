; GalleryGUI installer (Inno Setup 7). Per-user install, no admin.
; Preprocessor defines: /DAppVersion=x.y.z  /DRepoRoot=<repo root>

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
#ifndef RepoRoot
#define RepoRoot ".."
#endif

[Setup]
AppId={{8CFD3F2D-E62A-4EA2-B769-575CF5BD6149}
AppName=GalleryGUI
AppVersion={#AppVersion}
AppPublisher=GalleryGUI Project
DefaultDirName={localappdata}\Programs\GalleryGUI
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=GalleryGUI-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\GalleryGUI.exe
; User data under {localappdata}\GalleryGUI is intentionally preserved on uninstall.
UninstallFilesDir={app}\unins

[Files]
; App (dotnet publish output)
Source: "{#RepoRoot}\dist\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Engine (python embeddable + runner + sites + site-packages)
Source: "{#RepoRoot}\dist\engine\*"; DestDir: "{app}\engine"; Flags: recursesubdirs createallsubdirs ignoreversion
; GPL assets
Source: "{#RepoRoot}\dist\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GalleryGUI"; Filename: "{app}\GalleryGUI.exe"

[Messages]
ConfirmUninstall=Uninstall GalleryGUI?%n%nNote: user data (%%LOCALAPPDATA%%\GalleryGUI - download history and archive) is kept and can be deleted manually.

[Run]
Filename: "{app}\GalleryGUI.exe"; Description: "Launch GalleryGUI"; Flags: nowait postinstall skipifsilent
