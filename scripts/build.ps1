# 完整发行构建：引擎、独立运行的 WinUI 应用、许可证和 Inno Setup 安装包。
[CmdletBinding()]
param(
    [string]$AppVersion = '1.0.0',
    [string]$OutputDirectory = 'dist',
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($AppVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw '版本号须为 x.y.z 或 x.y.z-rc.1；不要包含 v 前缀'
}
$numericVersion = ($AppVersion -split '-')[0]
foreach ($part in $numericVersion.Split('.')) {
    if ([long]$part -gt 65534) { throw '版本号每段不能超过 65534' }
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distDir = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory)))
if (-not $distDir.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '输出目录必须位于仓库内的子目录'
}
# 仅允许重建发行目录或缓存中的验证目录，避免误删源码。
if ($distDir -ne (Join-Path $repoRoot 'dist') -and
    -not $distDir.StartsWith((Join-Path $repoRoot '.cache') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '输出目录只接受 dist 或 .cache 下的子目录'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '请先安装 .NET 8 SDK' }
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { throw '请先安装 uv' }
& (Join-Path $PSScriptRoot 'prepare-dependencies.ps1')
$iscc = $null
if (-not $SkipInstaller) {
    $tool = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($tool) { $iscc = $tool.Source }
    foreach ($candidate in @(
        'C:/Program Files (x86)/Inno Setup 6/ISCC.exe', 'C:/Program Files (x86)/Inno Setup 7/ISCC.exe',
        'C:/Program Files/Inno Setup 7/ISCC.exe', 'D:/Program Files/Inno Setup 6/ISCC.exe', 'D:/Program Files/Inno Setup 7/ISCC.exe'
    )) {
        if (-not $iscc -and (Test-Path -LiteralPath $candidate)) { $iscc = $candidate }
    }
    if (-not $iscc) { throw '请安装 Inno Setup 6 或 7，或使用 -SkipInstaller' }
}
if (Test-Path -LiteralPath $distDir) { Remove-Item -LiteralPath $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
& (Join-Path $PSScriptRoot 'build-engine.ps1') -OutputDirectory (Join-Path $distDir 'engine')

dotnet publish (Join-Path $repoRoot 'src/GGdown.App/GGdown.App.csproj') -c Release -r win-x64 `
    -p:Platform=x64 --self-contained true -p:Version=$AppVersion -p:AssemblyVersion="$numericVersion.0" `
    -p:FileVersion="$numericVersion.0" -o (Join-Path $distDir 'app')
if ($LASTEXITCODE -ne 0) { throw '应用发布失败' }
if (-not (Test-Path -LiteralPath (Join-Path $distDir 'app/GGdown.exe'))) { throw '未生成 GGdown.exe' }
$licenses = Join-Path $distDir 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'THIRD-PARTY.md') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $distDir 'engine/site-packages/gallery_dl-1.32.10.dist-info/licenses/LICENSE') `
    -Destination (Join-Path $licenses 'gallery-dl-LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'third_party/TikTokDownloader/license') `
    -Destination (Join-Path $licenses 'TikTokDownloader-LICENSE')
if (-not $SkipInstaller) {
    & $iscc /Q "/DAppVersion=$AppVersion" "/DAppNumericVersion=$numericVersion" "/DRepoRoot=$repoRoot" "/DDistDir=$distDir" `
        (Join-Path $repoRoot 'installer/ggdown.iss')
    if ($LASTEXITCODE -ne 0) { throw '安装包编译失败' }
    $installer = Join-Path $distDir "GGdown-Setup-$AppVersion.exe"
    if (-not (Test-Path -LiteralPath $installer)) { throw '未生成安装包' }
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Host "安装包：$installer"
}
