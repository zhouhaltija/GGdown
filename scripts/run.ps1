# 开发期启动：编译 Debug 并打开 GGdown.exe（不打安装包）
# DEBUG 构建使用 engine\.venv 中 uv 管理的 Python 3.13.7。
[CmdletBinding()]
param(
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Proj = Join-Path $RepoRoot "src\GGdown.App\GGdown.App.csproj"
$Exe = Join-Path $RepoRoot "src\GGdown.App\bin\x64\Debug\net8.0-windows10.0.22621.0\GGdown.exe"

$env:UV_CACHE_DIR = Join-Path $RepoRoot ".cache\uv-managed"
$env:UV_PYTHON_INSTALL_DIR = Join-Path $RepoRoot ".cache\uv-python"
Write-Host "==> uv sync engine"
uv sync --project (Join-Path $RepoRoot "engine") --locked
if ($LASTEXITCODE -ne 0) { throw "uv sync 失败，请确认已安装 uv" }

if (-not $NoBuild) {
    Write-Host "==> dotnet build Debug x64"
    dotnet build $Proj -c Debug -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "build 失败" }
}

if (-not (Test-Path $Exe)) {
    throw "找不到 $Exe ，请先去掉 -NoBuild 跑一次"
}

Write-Host "==> 启动 $Exe"
Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe)
