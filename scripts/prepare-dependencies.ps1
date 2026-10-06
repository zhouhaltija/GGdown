# 检出抖音库的固定源码版本；开发、CI 与发行打包共用。
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceDir = Join-Path $repoRoot 'third_party/TikTokDownloader'
$commit = '473c90ff70c663cfb69310fff2b8d5192f200661'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw '请先安装 Git' }
if (-not (Test-Path -LiteralPath $sourceDir)) {
    New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
    git -C $sourceDir init --quiet
    if ($LASTEXITCODE -ne 0) { throw '初始化第三方源码目录失败' }
    git -C $sourceDir remote add origin https://github.com/JoeanAmier/TikTokDownloader.git
    if ($LASTEXITCODE -ne 0) { throw '设置第三方源码地址失败' }
    git -C $sourceDir fetch --depth 1 origin $commit
    if ($LASTEXITCODE -ne 0) { throw '下载抖音库源码失败' }
    git -C $sourceDir checkout --detach $commit
    if ($LASTEXITCODE -ne 0) { throw '检出抖音库源码失败' }
}
$actual = git -C $sourceDir rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actual -ne $commit) {
    throw "third_party/TikTokDownloader 必须检出 $commit；请先处理已有目录，脚本不会覆盖其修改"
}
$changes = git -C $sourceDir status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $changes) { throw '抖音库源码有未提交修改，请使用固定提交的干净源码' }
foreach ($file in @('src/interface/detail.py', 'locale', 'license')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $file))) { throw "抖音库缺少 $file" }
}
Write-Host "第三方源码已就绪：$commit"
