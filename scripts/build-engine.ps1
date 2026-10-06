# 组装可独立运行的 Python 引擎；版本与 engine/uv.lock、THIRD-PARTY.md 保持一致。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$engineDir = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $engineDir.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '引擎输出目录必须位于仓库内'
}
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { throw '请先安装 uv' }
& (Join-Path $PSScriptRoot 'prepare-dependencies.ps1')

$pythonVersion = '3.13.7'
$galleryDlVersion = '1.32.10'
$pythonDir = Join-Path $engineDir 'python'
$packages = Join-Path $engineDir 'site-packages'
$cacheDir = Join-Path $repoRoot '.cache'
if (-not $env:UV_CACHE_DIR) { $env:UV_CACHE_DIR = Join-Path $cacheDir 'uv-managed' }
if (-not $env:UV_PYTHON_INSTALL_DIR) { $env:UV_PYTHON_INSTALL_DIR = Join-Path $cacheDir 'uv-python' }
New-Item -ItemType Directory -Path $cacheDir, $pythonDir, $packages -Force | Out-Null
$zip = Join-Path $cacheDir "python-$pythonVersion-embed-amd64.zip"
if (-not (Test-Path -LiteralPath $zip)) {
    Invoke-WebRequest "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip" -OutFile $zip
}
Expand-Archive -LiteralPath $zip -DestinationPath $pythonDir -Force

$requirements = Join-Path $engineDir 'locked-requirements.txt'
try {
    uv python install $pythonVersion
    if ($LASTEXITCODE -ne 0) { throw '安装构建用 Python 失败' }
    $python = uv python find $pythonVersion --managed-python
    if ($LASTEXITCODE -ne 0) { throw '找不到构建用 Python' }
    uv export --project (Join-Path $repoRoot 'engine') --locked --no-dev --no-emit-project --format requirements.txt --output-file $requirements | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '导出锁定依赖失败' }
    uv pip install --python $python --target $packages --no-deps --require-hashes -r $requirements
    if ($LASTEXITCODE -ne 0) { throw '安装发行依赖失败' }
}
finally {
    if (Test-Path -LiteralPath $requirements) { Remove-Item -LiteralPath $requirements }
}

# 从 wheel 自带的许可证取 gallery-dl 许可，无需另一份未跟踪的源码检出。
$galleryLicense = Join-Path $packages "gallery_dl-$galleryDlVersion.dist-info/licenses/LICENSE"
if (-not (Test-Path -LiteralPath $galleryLicense)) { throw '缺少 gallery-dl 许可证' }
foreach ($distInfo in Get-ChildItem -LiteralPath $packages -Directory -Filter '*.dist-info') {
    $licenses = @(Get-ChildItem -LiteralPath $distInfo.FullName -Recurse -File |
        Where-Object { $_.Name -match '^(licen[sc]e|copying)(\.|$)' })
    if ($licenses.Count -eq 0) { throw "依赖包缺少许可证：$($distInfo.Name)" }
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'third_party/TikTokDownloader/src') -Destination $packages -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'third_party/TikTokDownloader/locale') -Destination $packages -Recurse
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'engine') -File -Filter '*.py' |
    Copy-Item -Destination $engineDir
New-Item -ItemType Directory -Path (Join-Path $engineDir 'sites') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'engine/sites') -File -Filter '*.py' |
    Copy-Item -Destination (Join-Path $engineDir 'sites')

$pth = Join-Path $pythonDir 'python313._pth'
# 嵌入版 Python 默认忽略 PYTHONPATH；同时加入引擎根目录与第三方依赖。
@('python313.zip', '.', '..', '..\site-packages', 'import site') | Set-Content -LiteralPath $pth -Encoding ascii
@{
    python = $pythonVersion
    gallery_dl = $galleryDlVersion
    douyin = '473c90ff70c663cfb69310fff2b8d5192f200661'
    built_at = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $engineDir 'engine.json') -Encoding utf8

& (Join-Path $pythonDir 'python.exe') -c 'import gallery_dl, curl_cffi, sqlite3, rate_limit, sites.douyin, src.interface.detail'
if ($LASTEXITCODE -ne 0) { throw '内置引擎模块导入验证失败' }
$hello = & (Join-Path $pythonDir 'python.exe') (Join-Path $engineDir 'runner.py') hello
if ($LASTEXITCODE -ne 0 -or (($hello | ConvertFrom-Json).protocol -ne 1)) { throw '内置引擎协议验证失败' }
Write-Host "引擎组装完成：$engineDir"
