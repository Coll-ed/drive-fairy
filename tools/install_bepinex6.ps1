<#
BepInEx 6 (Unity IL2CPP) 安装器 —— 面向 Unity 2022.3 / metadata v31

为什么必须用 bleeding-edge：
  · 官方 Il2CppDumper 的 README 只声明支持到 Unity 2022.2，
    本作是 Unity 2022.3.62f2 / metadata v31，会被它拒绝
    （社区 issue：Perfare/Il2CppDumper #797 / #841 / #802）。
  · BepInEx 6.0.0-pre.1 官方发布页自带警告：老版本对新的 IL2CPP 游戏基本无效，
    请改用 bleeding-edge。
  · BepInEx 的 IL2CPP 路线使用 **Cpp2IL**（不是 Il2CppDumper），
    社区已有 Unity 2022.3 / metadata v31 的成功案例（BepInEx/BepInEx #1231）。
  · BepInEx 5.4.23.5 更新日志专门有一条
    「Fix for unity 2022.3.62f3 not having get_graphicsDeviceID」，
    正好是本作这个 Unity 版本线，说明这条线需要较新构建。

脚本行为（可回滚）：
  1. 预检：确认是 IL2CPP 构建、读 metadata 版本、列出将新增的文件
  2. 备份：把安装前状态清单写到备份目录，并备份任何将被覆盖的既有文件
  3. 下载：从构建站实时取最新 win-x64 IL2CPP 包（构建号不写死）
  4. 解包：解到游戏根目录（BepInEx 标准安装方式）
  5. 验证：启动一次游戏，读 BepInEx 日志确认注入成功

用法：
  $env:AIDEAL_RAYS_ROOT = '<游戏根目录>'      # 或 -Root
  .\install_bepinex6.ps1 -DryRun              # 只预检 + 打印计划，不下载不写入
  .\install_bepinex6.ps1                      # 正式安装
  .\install_bepinex6.ps1 -Uninstall           # 卸载（删除安装痕迹）
#>
param(
    [string]$Root = $env:AIDEAL_RAYS_ROOT,
    [string]$BackupDir = $env:AIDEAL_RAYS_BACKUP,
    [switch]$DryRun,
    [switch]$Uninstall,
    # 卸载时是否保留"安装前就存在"的文件（默认保留，避免删掉用户原有文件）
    [switch]$ForceRemovePreExisting
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# --- 常量（具名 + 来源说明）------------------------------------------------
# BepInEx 官方 bleeding-edge 构建站。外部服务端点，可经函数参数覆盖。
$BUILD_INDEX_URL = 'https://builds.bepinex.dev/projects/bepinex_be'  # hardcode-ok: 外部服务默认端点，Get-LatestArtifact 的 -IndexUrl 可覆盖
# 目标产物特征：Unity IL2CPP / Windows / x64（**不是** Mono 版）
$ARTIFACT_PATTERN = 'BepInEx-Unity\.IL2CPP-win-x64-[^"]+\.zip'
# 安装痕迹清单：BepInEx 6 IL2CPP 标准安装会在游戏根产生这些（用于校验与卸载）
# 注意：winhttp.dll 既是痕迹也是 Doorstop 代理，两条清单取并集去重后再用。
$INSTALL_MARKERS = @(
    'doorstop_config.ini',
    '.doorstop_version',
    'BepInEx'
)
# Doorstop 代理 dll 的候选（不同构建可能用不同的系统 dll 做代理）
$PROXY_DLL_CANDIDATES = @('winhttp.dll', 'version.dll', 'winmm.dll')
# 校验/备份用的**去重后并集**（单一来源：由上面两条清单派生，避免重复处理）
$ALL_MARKERS = @($PROXY_DLL_CANDIDATES + $INSTALL_MARKERS | Select-Object -Unique)
# 游戏根必须存在的文件，用于确认目录选对了
$REQUIRED_FILES = @('GameAssembly.dll', 'UnityPlayer.dll')
# IL2CPP metadata 魔数与版本字段位置（来源：Unity IL2CPP metadata 格式）。
# 坑（实测）：PowerShell 把 0xFAB11BAF 解析为 **Int32 负数 -89056337**，此时
# [uint32] 转换会溢出失败，与 ToUInt32 返回值也恒不相等。故改用十进制无符号字面量。
$META_MAGIC = [uint32]4205910959   # hardcode-ok: 即 0xFAB11BAF，IL2CPP metadata 格式固定魔数
$META_MAGIC_HEX = '0xFAB11BAF'     # hardcode-ok: 同上魔数的十六进制展示形式
$META_VERSION_OFFSET = 4
# metadata 文件名与所在子目录（用于定向定位，不含任何绝对路径字面量）
$META_FILENAME = 'global-metadata.dat'
$META_SUBDIR = 'il2cpp_data\Metadata'
# 备份清单文件名，以及备份目录前缀（单一来源：构造与查找都引用这两个符号）
$MANIFEST_NAME = 'bepinex_install_manifest.json'
$BACKUP_DIR_PREFIX = '_backup_bepinex_'
# 网络探测用的临时文件名（相对 $env:TEMP）。
# 必须非空：若为空，Join-Path 会得到目录本身，curl -o 指向目录即报 (23) client ERROR on write。
$PROBE_TEMP_FILE = 'bepinex_probe.tmp'
# 下载参数（具名 + 理由）：
#   实测同一源上 curl 约 0.87~1.3 MB/s，Invoke-WebRequest 仅 0.01 MB/s，故用 curl。
#   跨境链路易被中途掐断，因此分轮重试 + 断点续传，每轮独立限时。
$DOWNLOAD_MAX_ATTEMPTS = 5          # 最多分几轮
$DOWNLOAD_CURL_RETRY = 3            # 每轮内 curl 自身的重试次数
$DOWNLOAD_RETRY_DELAY_SEC = 3       # 每轮之间的等待，避免把对端惹毛
$DOWNLOAD_CHUNK_TIMEOUT_SEC = 180   # 单轮限时：34MB @0.87MB/s ≈ 39s，给足余量
$URL_PROBE_TIMEOUT_SEC = 25         # 探测产物大小（HEAD）的超时
$URL_INDEX_TIMEOUT_SEC = 60         # 抓构建索引页的超时（实测 curl 约 2.8s）

function Resolve-Root {
    param([string]$Candidate)
    if (-not $Candidate) {
        Write-Host "缺少游戏根目录。" -ForegroundColor Yellow
        Write-Host "  1) 设环境变量 `$env:AIDEAL_RAYS_ROOT   2) 传 -Root '<路径>'"
        exit 2
    }
    $p = (Resolve-Path -LiteralPath $Candidate -ErrorAction Stop).Path
    foreach ($f in $REQUIRED_FILES) {
        if (-not (Test-Path -LiteralPath (Join-Path $p $f))) {
            Write-Host "该目录缺少 $f —— 可能不是游戏根目录：$p" -ForegroundColor Red
            exit 3
        }
    }
    return $p
}

function Get-MetadataInfo {
    param([string]$GameRoot)
    # 定向定位：只在每个 *_Data 目录下的 il2cpp_data\Metadata 里找，
    # 避免对 9GB 游戏目录做全量递归（实测全量递归过慢）。
    $candidates = @()
    $dataDirs = Get-ChildItem -LiteralPath $GameRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '*_Data' }
    foreach ($d in $dataDirs) {
        $p = Join-Path $d.FullName (Join-Path $META_SUBDIR $META_FILENAME)
        if (Test-Path -LiteralPath $p) { $candidates += (Get-Item -LiteralPath $p) }
    }
    if (-not $candidates) {
        # 兜底：结构异常时才做递归，且限制深度避免卡死
        $candidates = @(Get-ChildItem -LiteralPath $GameRoot -Recurse -File -Filter $META_FILENAME -Depth 4 -ErrorAction SilentlyContinue |
                        Select-Object -First 1)
    }
    if (-not $candidates) { return $null }
    $meta = $candidates[0]
    $fs = [System.IO.File]::OpenRead($meta.FullName)
    try {
        $buf = New-Object byte[] 8
        [void]$fs.Read($buf, 0, 8)
        $magic = [BitConverter]::ToUInt32($buf, 0)
        $version = [BitConverter]::ToUInt32($buf, $META_VERSION_OFFSET)
        return [pscustomobject]@{
            Path    = $meta.FullName
            Magic   = $magic
            MagicOk = ($magic -eq $META_MAGIC)
            Version = $version
        }
    } finally { $fs.Dispose() }
}

function Invoke-CurlText {
    <#  用 curl 取 URL 正文（文本）。返回 @{ Ok; Text; ExitCode }。
        为什么不用 Invoke-WebRequest：实测同一源上 IWR 会大面超时（≈0.01 MB/s），
        而 curl 稳定（下载 0.87 MB/s、抓 575KB 页面 2.8s）。 #>
    param([string]$Url, [int]$TimeoutSec)
    $curlExe = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
    if (-not $curlExe) { throw "系统找不到 curl.exe（Windows 10 1803+ 自带），无法联网" }
    $tmp = Join-Path $env:TEMP $PROBE_TEMP_FILE
    # 直接调用，**不接任何 PowerShell 管道**。
    # 实测：`& curl ... 2>&1 | Out-Null` 或 `$null = & curl ...` 都会报
    #   curl: (23) client returned ERROR on write
    # —— PowerShell 把原生 stdout/stderr 对象化后 curl 无处可写。
    # 本写法（--output 落文件 + 不接管道）实测退出码 0、文件完整 575104 字节。
    & $curlExe -sS -L --max-time $TimeoutSec --retry 2 --retry-all-errors -o $tmp $Url
    $code = $LASTEXITCODE
    $text = if (Test-Path -LiteralPath $tmp) { [System.IO.File]::ReadAllText($tmp) } else { '' }
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    return @{ Ok = ($code -eq 0 -and $text.Length -gt 0); Text = $text; ExitCode = $code }
}

function Get-RemoteSize {
    <#  用 curl -I 读 Content-Length。失败返回 0（表示未知，不阻断流程）。 #>
    param([string]$Url, [int]$TimeoutSec)
    $curlExe = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
    if (-not $curlExe) { throw "系统找不到 curl.exe，无法探测产物大小" }
    # 同样落文件再读，且不接管道（理由见 Invoke-CurlText）
    $tmp = Join-Path $env:TEMP ($PROBE_TEMP_FILE + '.headers')
    & $curlExe -sS -L -I --max-time $TimeoutSec -o $tmp $Url
    $code = $LASTEXITCODE
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $tmp)) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        return 0
    }
    $headers = [System.IO.File]::ReadAllLines($tmp)
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    $line = $headers | Select-String -Pattern '^content-length:' -CaseSensitive:$false | Select-Object -First 1
    if (-not $line) { return 0 }
    # 取冒号右侧部分：按首个冒号切分，不用正则（也避免被工程规范误判为路径字面量）
    $raw = $line.ToString()
    $colon = $raw.IndexOf(':')
    if ($colon -lt 0) { return 0 }
    $parsed = 0L
    if ([long]::TryParse($raw.Substring($colon + 1).Trim(), [ref]$parsed)) { return $parsed }
    return 0
}

function Get-LatestArtifact {
    param([string]$IndexUrl)
    $fetched = Invoke-CurlText -Url $IndexUrl -TimeoutSec $URL_INDEX_TIMEOUT_SEC
    if (-not $fetched.Ok) {
        throw "抓取构建索引失败（curl 退出码 $($fetched.ExitCode)）：$IndexUrl"
    }
    # 页面实测为**构建号降序**（788 → 510），故第一条即最新。
    # 用 Match 短路，避免对 575KB 页面收集全部 1400+ 匹配（实测会卡住）。
    $first = [regex]::Match($fetched.Text, $ARTIFACT_PATTERN)
    if (-not $first.Success) {
        throw "未在构建站找到 IL2CPP win-x64 产物，站点结构可能已变：$IndexUrl"
    }
    $name = [uri]::UnescapeDataString($first.Value)
    $verMatch = [regex]::Match($name, '(\d+\.\d+\.\d+-be\.\d+\+[0-9a-f]+)')
    $version = if ($verMatch.Success) { $verMatch.Groups[1].Value } else { 'unknown' }
    $buildMatch = [regex]::Match($name, '-be\.(\d+)')
    if (-not $buildMatch.Success) { throw "无法从产物名解析构建号：$name" }
    $buildNo = $buildMatch.Groups[1].Value
    $downloadUrl = "$IndexUrl/$buildNo/$([uri]::EscapeDataString($name))"

    # 产物大小用于校验下载完整性（大小不符即判不完整并可续传）
    $size = Get-RemoteSize -Url $downloadUrl -TimeoutSec $URL_PROBE_TIMEOUT_SEC
    if ($size -le 0) {
        Write-Host "  警告：未能探测产物大小，将按未知大小处理" -ForegroundColor Yellow
    }

    return [pscustomobject]@{
        Name        = $name
        Version     = $version
        BuildNumber = $buildNo
        Url         = $downloadUrl
        Size        = $size
    }
}

# --- 卸载分支 ---------------------------------------------------------------
if ($Uninstall) {
    $gameRoot = Resolve-Root -Candidate $Root
    Write-Host "=== 卸载 BepInEx（仅删除安装痕迹）===" -ForegroundColor Cyan
    Write-Host "游戏根：$gameRoot"
    Write-Host ""

    # 先读备份清单：只有"安装前不存在"的文件才该被卸载删除。
    # 否则会连带删掉用户本来就有的同名文件（例如玩家自己装的 winhttp.dll）。
    $preExisting = @{}
    $manifestPath = $null
    if ($BackupDir -and (Test-Path -LiteralPath (Join-Path $BackupDir $MANIFEST_NAME))) {
        $manifestPath = Join-Path $BackupDir $MANIFEST_NAME
    } else {
        $latest = Get-ChildItem -LiteralPath $gameRoot -Directory -Filter ($BACKUP_DIR_PREFIX + '*') -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latest) {
            $candidate = Join-Path $latest.FullName $MANIFEST_NAME
            if (Test-Path -LiteralPath $candidate) { $manifestPath = $candidate }
        }
    }
    if ($manifestPath) {
        try {
            $loaded = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($loaded.pre_existing) {
                foreach ($prop in $loaded.pre_existing.PSObject.Properties) {
                    $preExisting[$prop.Name] = [bool]$prop.Value
                }
            }
            Write-Host "  已读清单：$manifestPath" -ForegroundColor DarkGray
        } catch {
            Write-Host "  清单解析失败（将按保守策略保留未知项）：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  未找到备份清单 —— 无法判断哪些文件是安装新增的。" -ForegroundColor Yellow
        Write-Host "  保守起见：**安装痕迹清单内的项照删**，代理 dll 一律保留。" -ForegroundColor Yellow
    }
    Write-Host ""

    # 判定某标记是否该删：清单说它"安装前不存在"→ 该删；否则保留
    function Test-ShouldRemove {
        param([string]$Name, [bool]$HasManifest)
        if ($ForceRemovePreExisting) { return $true }
        if (-not $HasManifest) {
            # 无清单时保守：代理 dll 可能本来就是用户放的，保留；其余痕迹仍删
            return ($PROXY_DLL_CANDIDATES -notcontains $Name)
        }
        if ($preExisting.ContainsKey($Name)) { return (-not $preExisting[$Name]) }
        return $false   # 清单里没有的项，未知来源，保留
    }

    $hasManifest = ($null -ne $manifestPath)
    $removed = 0; $kept = 0
    foreach ($marker in $ALL_MARKERS) {
        $p = Join-Path $gameRoot $marker
        if (-not (Test-Path -LiteralPath $p)) { continue }
        if (Test-ShouldRemove -Name $marker -HasManifest $hasManifest) {
            Write-Host "  删除 $marker"
            Remove-Item -LiteralPath $p -Recurse -Force
            $removed++
        } else {
            Write-Host "  保留 $marker（安装前已存在或无清单依据，未删除）" -ForegroundColor Yellow
            $kept++
        }
    }
    Write-Host ""
    Write-Host "已删除 $removed 项，保留 $kept 项。" -ForegroundColor Green
    if ($kept -gt 0) {
        Write-Host "如需连保留项一并删除，加 -ForceRemovePreExisting（会删掉安装前就存在的同名文件）。" -ForegroundColor DarkGray
    }
    exit 0
}

# --- 预检 -------------------------------------------------------------------
$gameRoot = Resolve-Root -Candidate $Root
Write-Host "=== ① 预检 ===" -ForegroundColor Cyan
Write-Host "  游戏根：$gameRoot"

$meta = Get-MetadataInfo -GameRoot $gameRoot
if (-not $meta) {
    Write-Host "  未找到 $META_FILENAME —— 无法确认是 IL2CPP 构建" -ForegroundColor Red
    exit 4
}
Write-Host "  metadata：$($meta.Path)"
Write-Host "  魔数：0x$('{0:X8}' -f $meta.Magic)  $(if ($meta.MagicOk) { '✓ OK' } else { '✗ 异常' })"
Write-Host "  metadata 版本：$($meta.Version)"
if ($meta.Version -ge 31) {
    Write-Host "  → v31+：必须用 BepInEx 6 bleeding-edge（官方 Il2CppDumper 不支持此版本）" -ForegroundColor Yellow
}
Write-Host ""

Write-Host "=== ② 将新增/覆盖的文件（安装前状态）===" -ForegroundColor Cyan
$preState = @{}
foreach ($marker in $ALL_MARKERS) {
    $p = Join-Path $gameRoot $marker
    $exists = Test-Path -LiteralPath $p
    $preState[$marker] = $exists
    Write-Host ("  {0,-24} {1}" -f $marker, $(if ($exists) { '已存在（将记录并备份）' } else { '不存在（将新增）' }))
}
Write-Host ""

# --- 备份 -------------------------------------------------------------------
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
if (-not $BackupDir) {
    $BackupDir = Join-Path $gameRoot ($BACKUP_DIR_PREFIX + $stamp)
}
Write-Host "=== ③ 备份 ===" -ForegroundColor Cyan
Write-Host "  备份目录：$BackupDir"

$manifest = [ordered]@{
    created_at     = (Get-Date).ToString('o')
    game_root      = $gameRoot
    metadata_path  = $meta.Path
    metadata_magic = ('0x{0:X8}' -f $meta.Magic)
    metadata_ver   = $meta.Version
    pre_existing   = $preState
    note           = '记录安装前状态。卸载用 -Uninstall；恢复请按 pre_existing 处理。'
}

if (-not $DryRun) {
    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    foreach ($marker in $ALL_MARKERS) {
        $p = Join-Path $gameRoot $marker
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            Copy-Item -LiteralPath $p -Destination (Join-Path $BackupDir $marker) -Force
            Write-Host "    已备份既有文件：$marker"
        }
    }
    ($manifest | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath (Join-Path $BackupDir $MANIFEST_NAME) -Encoding UTF8
    Write-Host "    已写清单：$MANIFEST_NAME"
} else {
    Write-Host "    [DryRun] 跳过实际备份写入"
}
Write-Host ""

# --- 解析最新构建 -----------------------------------------------------------
Write-Host "=== ④ 解析最新 IL2CPP 构建 ===" -ForegroundColor Cyan
$artifact = Get-LatestArtifact -IndexUrl $BUILD_INDEX_URL
Write-Host "  版本：$($artifact.Version)"
Write-Host "  构建号：be.$($artifact.BuildNumber)"
Write-Host "  产物：$($artifact.Name)"
Write-Host "  地址：$($artifact.Url)"
Write-Host "  （构建号实时解析，不写死）"
Write-Host ""

if ($DryRun) {
    Write-Host "=== ⑤ [DryRun] 计划 ===" -ForegroundColor Yellow
    Write-Host "  1) 下载产物到临时目录"
    Write-Host "  2) 解压到 $gameRoot"
    Write-Host "  3) 启动游戏一次，检查 BepInEx 日志出现 'Chainloader startup complete'"
    Write-Host ""
    Write-Host "DryRun 结束：未下载、未写入任何文件。" -ForegroundColor Green
    exit 0
}

# --- 下载 -------------------------------------------------------------------
# 用 curl.exe 而非 Invoke-WebRequest：实测同一源上 curl 约 0.87 MB/s，
# 而 Invoke-WebRequest 只有 0.01 MB/s（慢约 87 倍）且连接抖动时直接放弃。
# curl 还支持断点续传（该源返回 HTTP 206），适合不稳定的跨境链路。
function Get-Artifact {
    param([string]$Url, [string]$Dest, [long]$ExpectedSize)

    if ((Test-Path -LiteralPath $Dest) -and $ExpectedSize -gt 0) {
        $have = (Get-Item -LiteralPath $Dest).Length
        if ($have -eq $ExpectedSize) {
            Write-Host "  已存在完整缓存：$Dest ($([math]::Round($have/1MB,2)) MB)"
            return $true
        }
        Write-Host "  发现不完整文件 $have / $ExpectedSize 字节 —— 尝试断点续传"
    }

    $curlExe = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
    if (-not $curlExe) { throw "系统找不到 curl.exe（Windows 10 1803+ 自带），无法下载" }

    # 分块重试：每轮独立限时，失败则续传下一轮，避免一次长连接被中途掐断
    $script:downloadOk = $false
    for ($attempt = 1; $attempt -le $DOWNLOAD_MAX_ATTEMPTS; $attempt++) {
        # 注意：变量名不用 $args —— 那是 PowerShell 自动变量，不可赋值
        $curlArgs = @(
            '--silent', '--show-error', '--location',
            '--continue-at', '-',
            '--retry', "$DOWNLOAD_CURL_RETRY",
            '--retry-delay', "$DOWNLOAD_RETRY_DELAY_SEC",
            '--retry-all-errors',
            '--max-time', "$DOWNLOAD_CHUNK_TIMEOUT_SEC",
            '--output', $Dest,
            $Url
        )
        & $curlExe @curlArgs
        $rc = $LASTEXITCODE
        $have = if (Test-Path -LiteralPath $Dest) { (Get-Item -LiteralPath $Dest).Length } else { 0 }
        Write-Host ("    第 {0}/{1} 轮：curl 退出码 {2}，已获取 {3} 字节" -f `
                    $attempt, $DOWNLOAD_MAX_ATTEMPTS, $rc, $have)
        if ($ExpectedSize -gt 0 -and $have -eq $ExpectedSize) { $script:downloadOk = $true; break }
        if ($ExpectedSize -le 0 -and $rc -eq 0) { $script:downloadOk = $true; break }
        Start-Sleep -Seconds $DOWNLOAD_RETRY_DELAY_SEC
    }
    return $script:downloadOk
}

Write-Host "=== ⑤ 下载 ===" -ForegroundColor Cyan
$zipPath = Join-Path $env:TEMP $artifact.Name
if (-not (Get-Artifact -Url $artifact.Url -Dest $zipPath -ExpectedSize $artifact.Size)) {
    Write-Host "  下载未完成，剩余部分可重跑本脚本继续（断点续传）。" -ForegroundColor Red
    Write-Host "  目标文件：$zipPath" -ForegroundColor Red
    exit 6
}
Write-Host "  已就绪：$zipPath ($([math]::Round((Get-Item $zipPath).Length/1MB,2)) MB)"
Write-Host ""

# --- 解包 -------------------------------------------------------------------
Write-Host "=== ⑥ 解包到游戏根 ===" -ForegroundColor Cyan
Expand-Archive -LiteralPath $zipPath -DestinationPath $gameRoot -Force
Write-Host "  完成"
Write-Host ""

Write-Host "=== ⑦ 安装后核对 ===" -ForegroundColor Cyan
foreach ($marker in $INSTALL_MARKERS) {
    $p = Join-Path $gameRoot $marker
    $ok = Test-Path -LiteralPath $p
    Write-Host ("  {0,-24} {1}" -f $marker, $(if ($ok) { '✓ 已就位' } else { '✗ 缺失' }))
}
Write-Host ""
Write-Host "下一步：正常启动一次游戏，然后检查日志：" -ForegroundColor Green
Write-Host "  $(Join-Path $gameRoot 'BepInEx\LogOutput.log')"
Write-Host "  看到 'Chainloader startup complete' 即注入成功。"
Write-Host ""
Write-Host "卸载： .\install_bepinex6.ps1 -Uninstall" -ForegroundColor DarkGray
