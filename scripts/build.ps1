# 全流程构建：引擎组装 → dotnet publish → 拷贝 GPL 资产 → ISCC 编译安装包
[CmdletBinding()]
param(
    [string]$AppVersion = "1.0.0",
    [string]$GalleryDlVersion = "1.32.10",
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$DistDir = Join-Path $RepoRoot "dist"

# 可重入：清空 dist（保留引擎由 build-engine 自清；这里整体清更简单）
if (Test-Path $DistDir) { Remove-Item $DistDir -Recurse -Force }

# 1. 引擎
& (Join-Path $PSScriptRoot "build-engine.ps1") -GalleryDlVersion $GalleryDlVersion
if ($LASTEXITCODE -ne 0) { throw "build-engine 失败" }

# 2. 应用发布
Write-Host "==> dotnet publish"
dotnet publish (Join-Path $RepoRoot "src\GGdown.App\GGdown.App.csproj") `
    -c Release -r win-x64 --self-contained true -o (Join-Path $DistDir "app")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# 3. GPL 资产：应用 LICENSE + THIRD-PARTY.md + gallery-dl LICENSE → dist\licenses
Write-Host "==> 拷贝许可证资产"
New-Item (Join-Path $DistDir "licenses") -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $RepoRoot "LICENSE") (Join-Path $DistDir "licenses\LICENSE") -Force
Copy-Item (Join-Path $RepoRoot "THIRD-PARTY.md") (Join-Path $DistDir "licenses\THIRD-PARTY.md") -Force
Copy-Item (Join-Path $RepoRoot "gallery-dl\LICENSE") (Join-Path $DistDir "licenses\gallery-dl-LICENSE") -Force

if ($SkipInstaller) { Write-Host "==> 跳过安装器"; exit 0 }

# 4. ISCC 编译安装包
$iscc = @(
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "未找到 ISCC.exe，请先安装 Inno Setup" }

Write-Host "==> ISCC 编译安装包"
& $iscc "/DAppVersion=$AppVersion" "/DRepoRoot=$RepoRoot" (Join-Path $RepoRoot "installer\ggdown.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败" }

Get-ChildItem (Join-Path $DistDir "GGdown-Setup-*.exe") | ForEach-Object {
    Write-Host ("==> 产物: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB))
}
