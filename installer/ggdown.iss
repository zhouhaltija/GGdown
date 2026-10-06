; GGdown 安装器（Inno Setup 6/7），安装到当前用户目录。
; 构建入口传入版本、仓库和发行目录。

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

#ifndef AppNumericVersion
#define AppNumericVersion AppVersion
#endif
#ifndef RepoRoot
#define RepoRoot ".."
#endif
#ifndef DistDir
#define DistDir RepoRoot + "\dist"
#endif

[Setup]
AppId={{A4E1C7B2-6D58-4F0E-9C3A-1B7E5D94F260}
AppName=GGdown
AppVersion={#AppVersion}
VersionInfoVersion={#AppNumericVersion}
AppPublisher=GGdown Project
DefaultDirName={localappdata}\Programs\GGdown
PrivilegesRequired=lowest
OutputDir={#DistDir}
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
Source: "{#DistDir}\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Engine (python embeddable + runner + sites + site-packages)
Source: "{#DistDir}\engine\*"; DestDir: "{app}\engine"; Flags: recursesubdirs createallsubdirs ignoreversion
; GPL assets
Source: "{#DistDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GGdown"; Filename: "{app}\GGdown.exe"

[Messages]
ConfirmUninstall=Uninstall GGdown?%n%nNote: user data (%%LOCALAPPDATA%%\GGdown - download history and archive) is kept and can be deleted manually.

[Run]
Filename: "{app}\GGdown.exe"; Description: "Launch GGdown"; Flags: nowait postinstall skipifsilent
