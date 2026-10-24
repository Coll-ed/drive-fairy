# 全自动 D1 取证跑批：启动游戏 → 进战斗 → 模拟按键（W / Shift）→ 采日志 → 出对照表。
#
# 为什么这样设计（《AI会话工作约束》三·6、三·7 + diagnosing-bugs 技能 Phase 1）：
#   约束禁止"让人替我产生数据"。进战斗需要交互 ⇒ 我用 PrintWindow 自己看屏幕、
#   用 keybd_event 自己发按键，把整条链路做成**一条命令、无人值守**。
#
# 不变量：本脚本只做三件事 —— 启动进程、发按键、读日志。
#         **不改任何游戏文件**（配置只读），不写游戏目录。
#
# 用法：
#   $env:AIDEAL_RAYS_ROOT = '<游戏根>'
#   & _tools\auto-d1-run.ps1                 # 全程自动
#   & _tools\auto-d1-run.ps1 -BaselineSeconds 6 -BoostSeconds 12
#
# 产出：
#   _logs\d1-<时间戳>\ 下：LogOutput.snapshot.log / report.txt / 截图

[CmdletBinding()]
param(
    # 游戏根（不内置默认路径：路径属外部配置）
    [string]$GameRoot = $env:AIDEAL_RAYS_ROOT,
    [string]$ProcessName = 'AldealRaysR18',
    # 基线（只按 W）持续时间
    [int]$BaselineSeconds = 6,
    # 加力（W+Shift）持续时间
    [int]$BoostSeconds = 12,
    # 启动后等待游戏加载完的秒数
    [int]$StartupSeconds = 35,
    # 进入 CONTINUE 后等待进入战斗的秒数
    [int]$BattleLoadSeconds = 30,
    # 是否重启游戏（false 时复用已运行的进程）
    [switch]$ReuseRunning
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $GameRoot) { throw '未指定游戏根：请设 $env:AIDEAL_RAYS_ROOT' }
$exe = Join-Path $GameRoot "$ProcessName.exe"
if (-not (Test-Path $exe)) { throw "找不到游戏可执行文件：$exe" }

$workspace = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $workspace "_logs\d1-$stamp"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$logPath = Join-Path $GameRoot 'BepInEx\LogOutput.log'
$shotsDir = Join-Path $outDir 'shots'
New-Item -ItemType Directory -Force -Path $shotsDir | Out-Null

$gameIo = Join-Path $PSScriptRoot 'game-io.ps1'

function Say([string]$m) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
function Shot([string]$name) { & $gameIo -Action shot -Out (Join-Path $shotsDir "$name.png") 2>&1 | Out-Null }
function SnapLog([string]$name) {
    Copy-Item $logPath (Join-Path $outDir "$name.log") -Force -ErrorAction SilentlyContinue
}

# --- 按键原语（keybd_event 发的是全局键盘状态；游戏用旧版 Input 读，实测有效）---
if (-not ('AidealRaysKeys' -as [type])) {
    Add-Type -Namespace '' -Name 'AidealRaysKeys' -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.IntPtr dwExtraInfo);
'@
}
$KEYUP = 2
function KeyDown([byte]$vk) { [AidealRaysKeys]::keybd_event($vk, 0, 0, [IntPtr]::Zero) }
function KeyUp([byte]$vk) { [AidealRaysKeys]::keybd_event($vk, 0, $KEYUP, [IntPtr]::Zero) }

# 虚拟键码（WinUser.h 契约常量，非可配置项）
$VK_W = 0x57
$VK_S = 0x53
$VK_SHIFT = 0x10     # VK_SHIFT（通用 Shift）
$VK_SPACE = 0x20
$VK_ESC = 0x1B

Say "输出目录：$outDir"

# ══ 1. 启动（或复用）游戏 ══
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -and -not $ReuseRunning) {
    Say "结束已在运行的进程 pid=$($proc.Id)（dll 需重启才生效）"
    Stop-Process -Id $proc.Id -Force
    Start-Sleep -Seconds 4
    $proc = $null
}
if (-not $proc) {
    Say "启动游戏：$exe"
    Start-Process -FilePath $exe -WorkingDirectory $GameRoot | Out-Null
    Start-Sleep -Seconds $StartupSeconds
}
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw '游戏进程没起来' }
Say "游戏进程 pid=$($proc.Id)"

# ══ 2. 进战斗（标题 → CONTINUE → 任务）══
& $gameIo -Action focus | Out-Null
Start-Sleep -Seconds 2
Shot '01-title-or-menu'

# 标题页需要"按任意键"
Say '发 Space（越过 Press Any Key）'
KeyDown $VK_SPACE; Start-Sleep -Milliseconds 60; KeyUp $VK_SPACE
Start-Sleep -Seconds 8
& $gameIo -Action focus | Out-Null
Shot '02-menu'

# CONTINUE 卡片（客户区坐标按 1706x1066 实测；不同分辨率由参数化留待后续）
Say '点 CONTINUE 卡片'
& $gameIo -Action click -X 420 -Y 640 | Out-Null
Start-Sleep -Seconds 5
Shot '03-continue-selected'
Say '再点一次（进入）'
& $gameIo -Action click -X 420 -Y 640 | Out-Null
Start-Sleep -Seconds $BattleLoadSeconds
& $gameIo -Action focus | Out-Null
Shot '04-battle'

# 确认探针/插件已就绪
$ready = Select-String -Path $logPath -Pattern 'Player components \(' -ErrorAction SilentlyContinue
if (-not $ready) {
    Say '警告：日志里还没看到探针的组件清单 —— 可能还没进战斗，继续但结果可能无效'
}

# ══ 3. 阶段 A：基线（只按 W）══
Say "阶段 A 基线开始：按住 W $BaselineSeconds 秒（无 Shift）"
$logLenBeforeA = (Get-Content $logPath).Count
KeyDown $VK_W
Start-Sleep -Seconds $BaselineSeconds
KeyUp $VK_W
Start-Sleep -Seconds 1
SnapLog 'phaseA-baseline'
Say '阶段 A 结束'

# ══ 4. 阶段 B：加力（W + Shift）══
Say "阶段 B 加力开始：按住 W + Shift $BoostSeconds 秒"
KeyDown $VK_W
Start-Sleep -Milliseconds 500
KeyDown $VK_SHIFT
Start-Sleep -Seconds $BoostSeconds
KeyUp $VK_SHIFT
Start-Sleep -Seconds 1
KeyUp $VK_W
Start-Sleep -Seconds 2
SnapLog 'phaseB-boost'
Say '阶段 B 结束'

# ══ 5. 阶段 C：加力后再基线（对照漂移）══
Say "阶段 C 再次基线：按住 W $BaselineSeconds 秒"
KeyDown $VK_W
Start-Sleep -Seconds $BaselineSeconds
KeyUp $VK_W
Start-Sleep -Seconds 2
SnapLog 'phaseC-baseline2'

# ══ 6. 出对照表 ══
Say '汇总对照表…'
$report = Join-Path $outDir 'report.txt'
& (Join-Path $PSScriptRoot 'summarize-d1.ps1') -LogDir $outDir -Out $report
Say "报告：$report"
Get-Content $report | ForEach-Object { Write-Host $_ }
Say '完成'
