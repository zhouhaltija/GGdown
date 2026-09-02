# 组装运行引擎到 dist\engine：Python embeddable + gallery-dl(固定版本) + runner.py + sites
# 可重入：每次先清空 dist\engine 重建。
[CmdletBinding()]
param(
    # Global Constraint：与仓库 vendored gallery-dl 源码一致（runner 已在该版本验证）
    [string]$GalleryDlVersion = "1.32.10",
    [string]$PythonVersion = "3.13.7",
    [string]$DistDir = (Join-Path $PSScriptRoot "..\dist")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DistDir = [System.IO.Path]::GetFullPath($DistDir)
$EngineDir = Join-Path $DistDir "engine"
$PythonDir = Join-Path $EngineDir "python"
$SitePackagesDir = Join-Path $EngineDir "site-packages"

Write-Host "==> 组装引擎到 $EngineDir"

# 可重入：先清空 engine 子目录
if (Test-Path $EngineDir) { Remove-Item $EngineDir -Recurse -Force }
New-Item $EngineDir -ItemType Directory -Force | Out-Null

# 1. Python embeddable（zip 缓存在仓库 .cache，不随 dist 清空；版本变了会换文件名重下）
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$CacheDir = Join-Path $RepoRoot ".cache"
New-Item $CacheDir -ItemType Directory -Force | Out-Null
$zipName = "python-$PythonVersion-embed-amd64.zip"
$zipPath = Join-Path $CacheDir $zipName
$url = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
if (Test-Path $zipPath) {
    Write-Host "==> 复用缓存 $zipPath"
} else {
    Write-Host "==> 下载 $url"
    Invoke-WebRequest -Uri $url -OutFile $zipPath
}
Expand-Archive -Path $zipPath -DestinationPath $PythonDir -Force

# 2. gallery-dl 到 --target（与系统包隔离）
Write-Host "==> pip 安装 gallery-dl==$GalleryDlVersion"
New-Item $SitePackagesDir -ItemType Directory -Force | Out-Null
pip install --disable-pip-version-check --target $SitePackagesDir "gallery-dl==$GalleryDlVersion" "PySocks"
if ($LASTEXITCODE -ne 0) { throw "pip install gallery-dl/PySocks 失败" }

# 3. 补丁 ._pth：追加 ..\site-packages 并启用 import site（使嵌入解释器可见 site-packages）
$pyTag = "python$($PythonVersion -replace '^(\d+)\.(\d+).*', '$1$2')"
$pthPath = Join-Path $PythonDir "$pyTag._pth"
if (-not (Test-Path $pthPath)) { throw "未找到 ._pth 文件：$pthPath" }
$pthLines = Get-Content $pthPath
$pthLines = $pthLines | ForEach-Object { $_ -replace '^#import site\s*$', 'import site' }
if ($pthLines -notcontains '..\site-packages') { $pthLines += '..\site-packages' }
Set-Content -Path $pthPath -Value $pthLines -Encoding ASCII
Write-Host "==> 已补丁 $pyTag._pth"

# 4. runner.py + sites\
Copy-Item (Join-Path $PSScriptRoot "..\engine\runner.py") $EngineDir -Force
$repoSites = Join-Path $PSScriptRoot "..\engine\sites"
Copy-Item $repoSites (Join-Path $EngineDir "sites") -Recurse -Force

# 5. 版本戳
@{
    gallery_dl = $GalleryDlVersion
    python     = $PythonVersion
    built_at   = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json | Set-Content (Join-Path $EngineDir "engine.json") -Encoding UTF8

# 6. 校验：嵌入 python 能 import gallery_dl
Write-Host "==> 校验 import gallery_dl"
& (Join-Path $PythonDir "python.exe") -S -c "import gallery_dl; print(gallery_dl.version.__version__)"
if ($LASTEXITCODE -ne 0) { throw "嵌入 python 无法 import gallery_dl" }

# 7. 校验：runner hello 输出 protocol 1
Write-Host "==> 校验 runner hello"
Push-Location $EngineDir
try {
    $hello = & (Join-Path $PythonDir "python.exe") "runner.py" "hello"
    if ($LASTEXITCODE -ne 0) { throw "runner.py hello 退出码 $LASTEXITCODE" }
    Write-Host $hello
    if (-not ($hello -match '"protocol"\s*:\s*1')) { throw "runner hello 未输出 protocol 1" }
}
finally { Pop-Location }

Write-Host "==> 引擎组装完成"
