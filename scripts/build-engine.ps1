# 组装运行引擎到 dist\engine：Python embeddable + gallery-dl(固定版本) + runner.py + sites
# 可重入：每次先清空 dist\engine 重建。
[CmdletBinding()]
param(
    # Global Constraint：与仓库 vendored gallery-dl 源码一致（runner 已在该版本验证）
    [string]$GalleryDlVersion = "1.32.10",
    [string]$PythonVersion = "3.13.7",
    [string]$DistDir = (Join-Path $PSScriptRoot "..\dist"),
    [string]$ThirdPartyDir = (Join-Path $PSScriptRoot "..\third_party\TikTokDownloader"),
    [switch]$CheckPrerequisites
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$DistDir = [System.IO.Path]::GetFullPath($DistDir)
$ThirdPartyDir = [System.IO.Path]::GetFullPath($ThirdPartyDir)
$EngineDir = Join-Path $DistDir "engine"
$PythonDir = Join-Path $EngineDir "python"
$SitePackagesDir = Join-Path $EngineDir "site-packages"
$ExpectedDouyinCommit = "473c90ff70c663cfb69310fff2b8d5192f200661"
$EngineProject = Join-Path $RepoRoot "engine"

# 前置条件必须在清理 dist 之前检查。-CheckPrerequisites 供构建入口和无副作用检查使用。
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { throw "未找到 uv；请先安装 uv" }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "未找到 git；无法校验抖音库源码版本" }
if (-not (Test-Path -LiteralPath (Join-Path $EngineProject "uv.lock") -PathType Leaf)) {
    throw "缺少 engine/uv.lock；请先提交项目锁文件"
}
if (-not (Test-Path -LiteralPath (Join-Path $ThirdPartyDir "src/interface/account.py") -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $ThirdPartyDir "src/interface/detail.py") -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $ThirdPartyDir "license") -PathType Leaf)) {
    throw "缺少抖音库源码或 license：$ThirdPartyDir；请检出 JoeanAmier/TikTokDownloader 到指定提交 $ExpectedDouyinCommit"
}
$actualDouyinCommit = & git -C $ThirdPartyDir rev-parse HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or $actualDouyinCommit -ne $ExpectedDouyinCommit) {
    throw "抖音库提交不匹配：当前 $actualDouyinCommit，要求 $ExpectedDouyinCommit；路径 $ThirdPartyDir"
}
if ($GalleryDlVersion -ne "1.32.10" -or $PythonVersion -ne "3.13.7") {
    throw "发行依赖由 engine/uv.lock 固定：gallery-dl=1.32.10、Python=3.13.7；不要覆盖版本参数"
}
if ($CheckPrerequisites) {
    Write-Host "==> 引擎构建前置条件已满足"
    return
}

Write-Host "==> 组装引擎到 $EngineDir"

# 可重入：先清空 engine 子目录
if (Test-Path $EngineDir) { Remove-Item $EngineDir -Recurse -Force }
New-Item $EngineDir -ItemType Directory -Force | Out-Null

# 1. Python embeddable（zip 缓存在仓库 .cache，不随 dist 清空；版本变了会换文件名重下）
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

# 2. 用项目 uv.lock 导出哈希锁定的发行依赖，安装到隔离的 --target。
Write-Host "==> uv 安装 engine/uv.lock 中的发行依赖"
New-Item $SitePackagesDir -ItemType Directory -Force | Out-Null
$env:UV_CACHE_DIR = Join-Path $CacheDir "uv-managed"
$env:UV_PYTHON_INSTALL_DIR = Join-Path $CacheDir "uv-python"
& uv python install $PythonVersion
if ($LASTEXITCODE -ne 0) { throw "uv 安装 Python $PythonVersion 失败" }
$managedPython = & uv python find $PythonVersion --managed-python
if ($LASTEXITCODE -ne 0 -or -not $managedPython) { throw "uv 未找到 Python $PythonVersion" }
$requirements = Join-Path $EngineDir "locked-requirements.txt"
try {
    & uv export --project $EngineProject --locked --no-dev --no-emit-project --format requirements.txt --output-file $requirements > $null
    if ($LASTEXITCODE -ne 0) { throw "uv export engine/uv.lock 失败" }
    # 已缓存 wheel 时无需联网；缓存不全时再按锁文件哈希下载。
    & uv pip install --offline --python $managedPython --target $SitePackagesDir --no-deps --require-hashes -r $requirements
    if ($LASTEXITCODE -ne 0) {
        & uv pip install --python $managedPython --target $SitePackagesDir --no-deps --require-hashes -r $requirements
    }
    if ($LASTEXITCODE -ne 0) { throw "uv 安装锁定发行依赖失败" }
}
finally {
    if (Test-Path -LiteralPath $requirements) { Remove-Item -LiteralPath $requirements -Force }
}

# wheel 自带的许可证保留在各自的 dist-info；缺失时拒绝发行包。
foreach ($distInfo in Get-ChildItem -LiteralPath $SitePackagesDir -Directory -Filter "*.dist-info") {
    $licenses = @(Get-ChildItem -LiteralPath $distInfo.FullName -Recurse -File |
        Where-Object { $_.Name -match '^(licen[sc]e|copying)(\.|$)' })
    if ($licenses.Count -eq 0) { throw "依赖包缺少许可证文件：$($distInfo.Name)" }
}

# 该库的 setup.py 用于 cx_Freeze；以源码包形式放入 Python 路径。
Copy-Item (Join-Path $ThirdPartyDir "src") (Join-Path $SitePackagesDir "src") -Recurse -Force
Copy-Item (Join-Path $ThirdPartyDir "locale") (Join-Path $SitePackagesDir "locale") -Recurse -Force

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
    douyin     = $ExpectedDouyinCommit
    built_at   = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json | Set-Content (Join-Path $EngineDir "engine.json") -Encoding UTF8

# 6. 校验：用嵌入 Python 加载发行依赖和抖音库。
Push-Location $EngineDir
try {
    Write-Host "==> 校验内置 Python 的模块导入"
    & (Join-Path $PythonDir "python.exe") -c "import sys; sys.path.insert(0, '.'); import gallery_dl, curl_cffi, src.interface.account, src.interface.detail, sites.douyin"
    if ($LASTEXITCODE -ne 0) { throw "内置 Python 无法导入发行依赖或抖音库" }

    # 7. 校验：runner hello 输出 protocol 1
    Write-Host "==> 校验 runner hello"
    $hello = & (Join-Path $PythonDir "python.exe") "runner.py" "hello"
    if ($LASTEXITCODE -ne 0) { throw "runner.py hello 退出码 $LASTEXITCODE" }
    Write-Host $hello
    if (-not ($hello -match '"protocol"\s*:\s*1')) { throw "runner hello 未输出 protocol 1" }
}
finally { Pop-Location }

Write-Host "==> 引擎组装完成"
