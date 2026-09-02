# 开发期启动：编译 Debug 并打开 GalleryGUI.exe（不打安装包）
# DEBUG 构建会用仓库 engine\runner.py + 本机 PATH 上的 python。
[CmdletBinding()]
param(
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Proj = Join-Path $RepoRoot "src\GalleryGUI.App\GalleryGUI.App.csproj"
$Exe = Join-Path $RepoRoot "src\GalleryGUI.App\bin\x64\Debug\net8.0-windows10.0.22621.0\GalleryGUI.exe"

# SOCKS 代理需要 PySocks；系统 Python 默认没有
python -c "import socks" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "==> pip install PySocks"
    python -m pip install --disable-pip-version-check PySocks
    if ($LASTEXITCODE -ne 0) { throw "安装 PySocks 失败（SOCKS 代理需要它）" }
}

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
