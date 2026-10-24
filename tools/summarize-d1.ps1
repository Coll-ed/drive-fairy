# 汇总 D1 取证日志：把 [BPROBE][DISP] 的位置差按阶段（基线/加力）对照成表。
#
# 为什么要单独一个脚本：
#   对照必须**可复算**。日志是原始证据，这个脚本是"从原始证据到结论"的确定性变换；
#   结论随时可以用它重跑一遍验证（约束七·1 "收尾要验证"）。
#
# 用法：& _tools\summarize-d1.ps1 -LogDir <_logs\d1-时间戳> [-Out report.txt]

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LogDir,
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'

# --- 解析规则（具名，便于审计）---
# 样例：[BPROBE][DISP] phase=1 frames=660 dPos=(1.351,3.703,1.750) |dHoriz|=2.211 approxSpeed=n/a targetFps=-1
$DISP_RE = '\[BPROBE\]\[DISP\] phase=(\d) frames=(\d+) dPos=\(([-\d\.]+),([-\d\.]+),([-\d\.]+)\) \|dHoriz\|=([\d\.]+) approxSpeed=([\d\.n/a]+) targetFps=(-?\d+)'
# 样例：[BPROBE][boost] f=610 status=IDLE_NONBOOST point=2/7(min=2) assistMagni=4.000 ...
$SNAP_RE = '\[BPROBE\]\[(\w[\w\-]*)\] f=(\d+) status=(\w+) point=([-\d]+)/([\d]+)\(min=([-\d]+)\) assistMagni=([\d\.]+) lockonMove=([\d\.]+) lockTarget=(\S+) Move=\(([-\d\.]+),([-\d\.]+),([-\d\.]+)\)'
# 样例：[TRACE] inputDir=(x,z) moveDir=(x,z) dispDir=(x,z) dispLen=6.498 ... inputVsFacing=146.881deg
$TRACE_RE = '\[TRACE\] inputDir=\(([-\d\.]+),([-\d\.]+)\) moveDir=\(([-\d\.]+),([-\d\.]+)\) dispDir=\(([-\d\.]+),([-\d\.]+)\) dispLen=([\d\.]+) samples=(\d+) \| camFwdDir=\(([-\d\.]+),([-\d\.]+)\) camFwdY=([-\d\.]+) \| facingDir=\(([-\d\.]+),([-\d\.]+)\) inputVsFacing=([\d\.a-z]+)deg'

$files = Get-ChildItem $LogDir -Filter '*.log' | Sort-Object Name
if (-not $files) { throw "目录里没有 .log 文件：$LogDir" }

$phaseName = @{ '0' = 'pre(点火前/等待输入)'; '1' = 'boost(加力中)'; '2' = 'post(关火后)' }

$lines = New-Object System.Collections.Generic.List[string]
function W([string]$s) { $lines.Add($s); Write-Host $s }

W "D1 取证汇总   目录：$LogDir"
W ("生成时间：{0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
W ('=' * 78)

# ---- 1. 各快照文件的 status 分布 + assistMagni 取值 ----
W ''
W '【1】加力状态机与速度倍率（每个快照文件）'
W ('-' * 78)
foreach ($f in $files) {
    $text = Get-Content $f.FullName -Raw
    $snaps = [regex]::Matches($text, $SNAP_RE)
    if ($snaps.Count -eq 0) { W ("  {0,-26} 无快照" -f $f.Name); continue }
    $byStatus = @{}
    $byMagni = @{}
    foreach ($m in $snaps) {
        $st = $m.Groups[3].Value
        $mg = $m.Groups[7].Value
        $byStatus[$st] = 1 + ($byStatus[$st] -as [int])
        $byMagni[$mg] = 1 + ($byMagni[$mg] -as [int])
    }
    $stStr = ($byStatus.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
    $mgStr = ($byMagni.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)×$($_.Value)" }) -join ' '
    W ("  {0,-26} 快照{1,4}  status: {2}" -f $f.Name, $snaps.Count, $stStr)
    W ("  {0,-26}            assistMagni: {1}" -f '', $mgStr)
}

# ---- 2. 水平位移速度对照（这是"移速有没有变"的客观答案）----
W ''
W '【2】水平位移对照（|dHoriz| 是每 60 帧的位置差大小，与实现无关的客观量）'
W ('-' * 78)
W ("  {0,-26} {1,-22} {2,>6} {3,>10} {4,>10} {5,>10}" -f '文件', '阶段', '样本', '最小', '中位', '最大')
foreach ($f in $files) {
    $text = Get-Content $f.FullName -Raw
    $disp = [regex]::Matches($text, $DISP_RE)
    if ($disp.Count -eq 0) { continue }
    $groups = $disp | Group-Object { $_.Groups[1].Value }
    foreach ($g in ($groups | Sort-Object Name)) {
        $vals = $g.Group | ForEach-Object { [double]$_.Groups[6].Value } | Sort-Object
        $min = $vals[0]
        $max = $vals[-1]
        $med = $vals[[int]($vals.Count / 2)]
        W ("  {0,-26} {1,-22} {2,6} {3,10:F3} {4,10:F3} {5,10:F3}" -f $f.Name, $phaseName[$g.Name], $vals.Count, $min, $med, $max)
    }
}

# ---- 3. 朝向 vs 输入（D1 的"转身"问题）----
W ''
W '【3】朝向 vs 输入方向（inputVsFacing 越大 = 越像"不是朝输入方向"）'
W ('-' * 78)
foreach ($f in $files) {
    $text = Get-Content $f.FullName -Raw
    $tr = [regex]::Matches($text, $TRACE_RE)
    if ($tr.Count -eq 0) { continue }
    $angs = @()
    foreach ($m in $tr) {
        $a = 0.0
        if ([double]::TryParse($m.Groups[13].Value, [ref]$a)) { $angs += $a }
    }
    if ($angs.Count -eq 0) { continue }
    $s = $angs | Sort-Object
    W ("  {0,-26} TRACE样本{1,4}  inputVsFacing 最小={2:F1}° 中位={3:F1}° 最大={4:F1}°" -f `
        $f.Name, $angs.Count, $s[0], $s[[int]($s.Count / 2)], $s[-1])
}

# ---- 4. 按键送达自证（确认模拟输入真的被游戏读到了）----
W ''
W '【4】模拟按键是否送达（[IN] 是插件读到的移动输入）'
W ('-' * 78)
foreach ($f in $files) {
    $text = Get-Content $f.FullName -Raw
    $inCount = ([regex]::Matches($text, '\[IN \]')).Count
    $fbCount = ([regex]::Matches($text, '\[BOOST\] fast flight engaged')).Count
    $foCount = ([regex]::Matches($text, 'ForceBoostoff\(\) - fast flight OFF')).Count
    W ("  {0,-26} [IN]={1,4}  点火={2}  关火={3}" -f $f.Name, $inCount, $fbCount, $foCount)
}

W ''
W ('=' * 78)
W '读法提示：'
W '  · 【2】里 boost 阶段的 |dHoriz| 若与 pre/post 阶段**没有显著差异** ⇒ "移速没变化"成立（用户所见属实）。'
W '  · 【1】里 assistMagni 若全程只有一个取值 ⇒ 加速不走这个字段，得另找载体。'
W '  · 【1】里 boost 阶段若出现 FORWARD/LEFT/RIGHT_BOOST ⇒ 加力态确实进了（那"没变化"就另有原因）。'

$text = $lines -join [Environment]::NewLine
if ($Out) { [System.IO.File]::WriteAllText($Out, $text, (New-Object System.Text.UTF8Encoding($false))) }
