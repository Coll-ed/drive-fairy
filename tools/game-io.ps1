# 实机取证辅助工具：截屏 + 窗口/输入控制。
#
# 为什么需要它（《AI会话工作约束》三·6、三·7）：
#   约束禁止"让人替我产生数据"。但游戏需要"进战斗"这种交互才能复现状态。
#   ⇒ 解法不是问人，而是**我自己看屏幕 + 我自己发输入**：
#     · `shot`   截屏到文件 —— 我用 read_image 直接看画面（我有视觉通道读文件，只是看不到实时屏幕）
#     · `focus`  把游戏窗口提到前台（不给前台，输入进不去）
#     · `click`  在窗口客户区坐标上点一下
#     · `key`    发一次按键
#
# 约束：本脚本只操作窗口/输入，**不改任何游戏文件**。
#
# 用法：
#   & _tools\game-io.ps1 -Action shot   [-Out <png>]
#   & _tools\game-io.ps1 -Action focus
#   & _tools\game-io.ps1 -Action click  [-X 960] [-Y 540]
#   & _tools\game-io.ps1 -Action key    -Key F2
#   & _tools\game-io.ps1 -Action clientrect

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('shot', 'focus', 'click', 'key', 'clientrect')]
    [string]$Action,

    # 截图输出路径（默认写到工作区 _shots\<时间戳>.png）
    [string]$Out,

    # 点击坐标：**客户区**坐标（左上角为 0,0），不是屏幕坐标
    [int]$X = 0,
    [int]$Y = 0,

    # 按键名（System.Windows.Forms.Keys 名，如 F2 / Space / W）
    [string]$Key = '',

    # 进程名（外置，不写死）
    [string]$ProcessName = 'AldealRaysR18'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# --- Win32 入口（具名常量，避免脚本里散落魔法数）---
if (-not ('AidealRaysWin32' -as [type])) {
    Add-Type -Namespace '' -Name 'AidealRaysWin32' -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.IntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.IntPtr dwExtraInfo);
// 抓**后台窗口**内容：前台被别的窗口遮住时 CopyFromScreen 只会截到遮挡物（实测踩到）。
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, System.IntPtr hdcBlt, uint nFlags);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
'@
}

# Win32 具名常量（来源：WinUser.h；写死是因为它们是 API 契约，不是可配置参数）
$SW_RESTORE = 9
$MOUSEEVENTF_LEFTDOWN = 0x0002
$MOUSEEVENTF_LEFTUP = 0x0004
$KEYEVENTF_KEYUP = 0x0002

function Get-GameWindow {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $proc) {
        throw "找不到带窗口的进程：$ProcessName（游戏没在跑？）"
    }
    return $proc
}

switch ($Action) {
    'focus' {
        $p = Get-GameWindow
        [void][AidealRaysWin32]::ShowWindow($p.MainWindowHandle, $SW_RESTORE)
        $ok = [AidealRaysWin32]::SetForegroundWindow($p.MainWindowHandle)
        Write-Host "focus pid=$($p.Id) hwnd=$($p.MainWindowHandle) ok=$ok"
    }

    'clientrect' {
        $p = Get-GameWindow
        $r = New-Object AidealRaysWin32+RECT
        [void][AidealRaysWin32]::GetClientRect($p.MainWindowHandle, [ref]$r)
        Write-Host "client: width=$($r.Right - $r.Left) height=$($r.Bottom - $r.Top)"
    }

    'shot' {
        $p = Get-GameWindow
        if (-not $Out) {
            $ws = Split-Path -Parent $PSScriptRoot
            $dir = Join-Path $ws '_shots'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $Out = Join-Path $dir ("{0}.png" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        }
        $r = New-Object AidealRaysWin32+RECT
        [void][AidealRaysWin32]::GetClientRect($p.MainWindowHandle, [ref]$r)
        $w = $r.Right - $r.Left
        $h = $r.Bottom - $r.Top
        if ($w -le 0 -or $h -le 0) { throw "客户区尺寸非法：${w}x${h}" }

        # 用 PrintWindow 抓**窗口本身**的像素（不依赖它是否在前台）。
        # 实测教训：CopyFromScreen 抓的是"屏幕上该矩形的内容"，
        # 前台被别的窗口遮住时只会截到遮挡物 —— 不能用它做后台取证。
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        try {
            $ok = [AidealRaysWin32]::PrintWindow($p.MainWindowHandle, $hdc, $PW_RENDERFULLCONTENT)
        }
        finally {
            $g.ReleaseHdc($hdc)
        }
        try {
            $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $g.Dispose()
            $bmp.Dispose()
        }
        Write-Host "saved: $Out  (${w}x${h}) PrintWindow=$ok"
    }

    'click' {
        $p = Get-GameWindow
        [void][AidealRaysWin32]::ShowWindow($p.MainWindowHandle, $SW_RESTORE)
        [void][AidealRaysWin32]::SetForegroundWindow($p.MainWindowHandle)
        Start-Sleep -Milliseconds 250
        $pt = New-Object AidealRaysWin32+POINT
        $pt.X = $X
        $pt.Y = $Y
        [void][AidealRaysWin32]::ClientToScreen($p.MainWindowHandle, [ref]$pt)
        [void][AidealRaysWin32]::SetCursorPos($pt.X, $pt.Y)
        Start-Sleep -Milliseconds 120
        [AidealRaysWin32]::mouse_event($MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [System.IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [AidealRaysWin32]::mouse_event($MOUSEEVENTF_LEFTUP, 0, 0, 0, [System.IntPtr]::Zero)
        Write-Host "clicked client($X,$Y) -> screen($($pt.X),$($pt.Y))"
    }

    'key' {
        if (-not $Key) { throw "-Action key 需要 -Key <键名>" }
        $p = Get-GameWindow
        [void][AidealRaysWin32]::ShowWindow($p.MainWindowHandle, $SW_RESTORE)
        [void][AidealRaysWin32]::SetForegroundWindow($p.MainWindowHandle)
        Start-Sleep -Milliseconds 250
        $vk = [byte][System.Windows.Forms.Keys]::$Key
        [AidealRaysWin32]::keybd_event($vk, 0, 0, [System.IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [AidealRaysWin32]::keybd_event($vk, 0, $KEYEVENTF_KEYUP, [System.IntPtr]::Zero)
        Write-Host "key sent: $Key (vk=$vk)"
    }
}
