# 构建 + 部署 + 校验（单一入口，避免每次手敲一长串命令）
#
# 用法：
#   $env:AIDEAL_RAYS_ROOT = '<游戏根>'      # 只需设一次（或持久化，见文末）
#   pwsh -File _tools\build-deploy.ps1                    # 构建 + 部署 + 校验哈希
#   pwsh -File _tools\build-deploy.ps1 -Deploy:$false     # 只构建，不碰游戏目录
#   pwsh -File _tools\build-deploy.ps1 -Project BoostProbe
#
# 为什么要有这个脚本：
#   交接说明指出前任的失败之一就是"每次都让人当测试台"。构建/部署必须**可复现、可校验**，
#   且部署前后要能证明"部署的确实是刚构建的那个字节"（SHA256 比对），不能靠"我编译过了"。
#
# 回滚：部署前会把旧 dll 备份到 _backup\plugins\；脚本最后一行会打印**可复制的回滚命令**。

[CmdletBinding()]
param(
    # 要构建的插件工程目录名（位于 plugin\ 下）
    [string]$Project = 'Flight',
    # 是否部署到游戏 BepInEx\plugins（默认部署）
    [bool]$Deploy = $true,
    # 游戏根目录。优先参数，其次环境变量 AIDEAL_RAYS_ROOT。
    # 刻意**不内置默认路径** —— 路径属外部配置，代码里只认符号（工程纪律·不写死）。
    [string]$GameRoot = ''
)

$ErrorActionPreference = 'Stop'

$gameRoot = if ($GameRoot) { $GameRoot } else { $env:AIDEAL_RAYS_ROOT }
if (-not $gameRoot) {
    throw @"
未指定游戏根目录。请先设置环境变量（路径不写死进脚本）：
  `$env:AIDEAL_RAYS_ROOT = '<游戏根>'                 # 仅本会话
  [Environment]::SetEnvironmentVariable('AIDEAL_RAYS_ROOT','<游戏根>','User')   # 持久化
或传参：  pwsh -File _tools\build-deploy.ps1 -GameRoot '<游戏根>'
"@
}
if (-not (Test-Path (Join-Path $gameRoot 'BepInEx\core'))) {
    throw "游戏根目录没有 BepInEx\core：$gameRoot"
}

$workspace  = Split-Path -Parent $PSScriptRoot          # ...\game\aideal_rays
$projectDir = Join-Path $workspace "plugin\$Project"
$projFile   = Join-Path $projectDir "AidealRays.$Project.csproj"

if (-not (Test-Path $projFile)) {
    throw "找不到工程文件：$projFile"
}

Write-Host "== 构建 $Project ==" -ForegroundColor Cyan
# Common.props 同时认 AIDEALRAYS_ROOT 与参数；这里两个都设，避免两套命名不一致。
$env:AIDEALRAYS_ROOT = $gameRoot
$env:AIDEAL_RAYS_ROOT = $gameRoot
# AIDEALRAYS_DEPLOY 置 0：部署与校验统一由本脚本做，避免两处都动部署逻辑（单一来源）。
$env:AIDEALRAYS_DEPLOY = '0'

Push-Location $projectDir
try {
    dotnet build -c Release --nologo 2>&1 | ForEach-Object { $_ }
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（exit $LASTEXITCODE）" }
}
finally {
    Pop-Location
}

$builtDll = Join-Path $projectDir "bin\Release\net6.0\AidealRays.$Project.dll"
if (-not (Test-Path $builtDll)) { throw "构建产物不存在：$builtDll" }
$builtHash = (Get-FileHash $builtDll -Algorithm SHA256).Hash
Write-Host ("构建产物：{0}" -f $builtDll)
Write-Host ("SHA256  ：{0}" -f $builtHash)

if (-not $Deploy) {
    Write-Host "已跳过部署（-Deploy:`$false）" -ForegroundColor Yellow
    return
}

# ---- 部署：先备份，再复制，再校验 ----
$pluginDir = Join-Path $gameRoot 'BepInEx\plugins'
$deployDll = Join-Path $pluginDir "AidealRays.$Project.dll"
$backupDir = Join-Path $workspace '_backup\plugins'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

$rollbackSource = $null
if (Test-Path $deployDll) {
    $oldHash = (Get-FileHash $deployDll -Algorithm SHA256).Hash
    if ($oldHash -eq $builtHash) {
        Write-Host "已是最新，无需部署（哈希一致）" -ForegroundColor Green
        return
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $backupDir "AidealRays.$Project.$stamp.dll"
    Copy-Item $deployDll $backup -Force
    $rollbackSource = $backup
    Write-Host ("已备份旧 dll → {0}" -f $backup)
}

Copy-Item $builtDll $deployDll -Force
$deployedHash = (Get-FileHash $deployDll -Algorithm SHA256).Hash

Write-Host ("部署到：{0}" -f $deployDll)
Write-Host ("SHA256 ：{0}" -f $deployedHash)

if ($deployedHash -ne $builtHash) {
    throw "部署校验失败：部署后哈希与构建产物不一致！built=$builtHash deployed=$deployedHash"
}
Write-Host "✅ 部署校验通过（部署字节 == 构建字节）" -ForegroundColor Green

if ($rollbackSource) {
    Write-Host ""
    Write-Host "回滚命令（可复制）：" -ForegroundColor Yellow
    Write-Host ("  Copy-Item '{0}' '{1}' -Force" -f $rollbackSource, $deployDll)
}
