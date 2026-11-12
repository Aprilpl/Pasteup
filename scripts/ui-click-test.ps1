param(
    [int]$Rounds = 6,
    [string]$DataDir = "",
    [switch]$VerboseLog
)
# Pasteup 点击健壮性测试台：UIA 定位 + SendInput 真实点击，多轮循环，崩溃即抓事件日志。
# 预置 settings.json 关闭 PasteOnEnter，避免双击激活测试向全局发 Ctrl+V。
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
'@

$exe = "C:\MySyncthing\Development\AsukaWin11\Pasteup\src\Pasteup.App\bin\Debug\net10.0-windows\Pasteup.exe"
if (-not $DataDir) { $DataDir = Join-Path $env:TEMP ("pasteup-crash-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
if (-not (Test-Path (Join-Path $DataDir 'settings.json'))) {
    Set-Content -Path (Join-Path $DataDir 'settings.json') -Value '{"PasteOnEnter": false, "Theme": "Light"}' -Encoding UTF8
}

function Log($msg) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg) }

function ClickAt([double]$x, [double]$y, [int]$afterMs = 120) {
    [W.U]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 25
    [W.U]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [W.U]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $afterMs
}

function DblClickAt([double]$x, [double]$y, [int]$afterMs = 200) {
    [W.U]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 25
    1..2 | ForEach-Object {
        [W.U]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [W.U]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 40
    }
    Start-Sleep -Milliseconds $afterMs
}

function DragBy([double]$x, [double]$y, [int]$dx, [int]$dy) {
    [W.U]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 40
    [W.U]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [W.U]::SetCursorPos([int]($x + $dx), [int]($y + $dy)) | Out-Null
    Start-Sleep -Milliseconds 60
    [W.U]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
}

function SendKey([byte]$vk) {
    [W.U]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    [W.U]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 70
}

function GetWindows([int]$procId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
}

function WaitPanelVisible([int]$procId, [int]$timeoutMs = 4000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($w in (GetWindows $procId)) {
            if ($w.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and -not $w.Current.IsOffscreen -and $w.Current.BoundingRectangle.Width -gt 0) {
                return $w
            }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function GetButtons($panel) {
    return @($panel.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
}

function Alive($p) { return -not $p.HasExited }

function Check([string]$step, $p) {
    if (-not (Alive $p)) {
        Log "!!! 进程已退出，步骤=$step ExitCode=$($p.ExitCode)"
        return $false
    }
    return $true
}

function ShowPanelViaSignal() {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) | Out-Null
}

# ===== 启动被测实例 =====
$env:PASTEUP_DATA = $DataDir
Log "沙箱数据目录: $DataDir"
$p = Start-Process -FilePath $exe -ArgumentList '--sample', '--show' -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Milliseconds 2500
if ($p.HasExited) { Log "!!! 启动即退出 ExitCode=$($p.ExitCode)"; exit 1 }
Log "被测实例 PID=$($p.Id)"

$crashed = $false
$crashStep = ""

for ($round = 1; $round -le $Rounds -and -not $crashed; $round++) {
    Log "===== 第 $round/$Rounds 轮 ====="

    $panel = WaitPanelVisible $p.Id
    if (-not $panel) {
        Log "面板不可见，用信号唤起"
        ShowPanelViaSignal
        $panel = WaitPanelVisible $p.Id
    }
    if (-not (Check "round$round-找面板" $p)) { $crashed = $true; $crashStep = "找面板"; break }
    if (-not $panel) { Log "!! 面板未找到（未崩溃），跳过本轮"; continue }

    $rect = $panel.Current.BoundingRectangle
    Log ("面板矩形: {0},{1} {2}x{3}" -f [int]$rect.X, [int]$rect.Y, [int]$rect.Width, [int]$rect.Height)

    # -- 1. 按钮清点（索引定位：0=主题 1=设置 2..9=八个标签钮）
    $buttons = GetButtons $panel
    if ($VerboseLog) { for ($i = 0; $i -lt $buttons.Count; $i++) { Log ("  btn[{0}] '{1}' @({2},{3})" -f $i, $buttons[$i].Current.Name, [int]$buttons[$i].Current.BoundingRectangle.X, [int]$buttons[$i].Current.BoundingRectangle.Y) } }
    Log ("按钮数: {0}（期望 10：主题/设置/7 标签/新建）" -f $buttons.Count)

    # -- 2. 依次点击标签钮（索引 2..9）
    for ($i = 2; $i -lt [Math]::Min(10, $buttons.Count); $i++) {
        $r = $buttons[$i].Current.BoundingRectangle
        if ($r.Width -le 0) { continue }
        ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
        if (-not (Check "round$round-点btn[$i]" $p)) { $crashed = $true; $crashStep = "点击按钮 #$i"; break }
    }
    if ($crashed) { break }

    # -- 3. 主题快速连点 x10（资源字典热切换压力）
    if ($buttons.Count -ge 1) {
        $r = $buttons[0].Current.BoundingRectangle
        for ($i = 0; $i -lt 10; $i++) {
            ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) 60
            if (-not (Check "round$round-主题连点$i" $p)) { $crashed = $true; $crashStep = "主题连点 #$i"; break }
        }
    }
    if ($crashed) { break }

    # -- 4. 打开设置 → UIA 关闭 → 重新唤起
    if ($buttons.Count -ge 2) {
        $r = $buttons[1].Current.BoundingRectangle
        ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) 600
        if (-not (Check "round$round-开设置" $p)) { $crashed = $true; $crashStep = "打开设置"; break }
        foreach ($w in (GetWindows $p.Id)) {
            if ($w.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $w.Current.Name -match '设置') {
                try {
                    $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
                    ([System.Windows.Automation.WindowPattern]$wp).Close()
                } catch { Log "  设置窗口关闭失败: $($_.Exception.Message)" }
            }
        }
        Start-Sleep -Milliseconds 400
        if (-not (Check "round$round-关设置" $p)) { $crashed = $true; $crashStep = "关闭设置"; break }
        $panel = WaitPanelVisible $p.Id
        if (-not $panel) { ShowPanelViaSignal; $panel = WaitPanelVisible $p.Id }
        if (-not (Check "round$round-唤起刷新" $p)) { $crashed = $true; $crashStep = "设置后唤起"; break }
        $rect = $panel.Current.BoundingRectangle
        $buttons = GetButtons $panel
    }

    # -- 5. 列表项点击（≤5 个）+ Ctrl+P + Enter 激活 + 双击激活 + Esc 隐藏
    $buttons2 = GetButtons $panel
    if ($buttons2.Count -ge 3) {
        $r = $buttons2[2].Current.BoundingRectangle   # 先回「全部」标签，保证有列表项
        ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) 250
        if (-not (Check "round$round-回全部标签" $p)) { $crashed = $true; $crashStep = "回全部标签"; break }
    }
    $items = @($panel.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
    Log ("列表项数: {0}" -f $items.Count)
    $n = [Math]::Min(5, $items.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $r = $items[$i].Current.BoundingRectangle
        if ($r.Width -le 0) { continue }
        ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
        if (-not (Check "round$round-点列表项$i" $p)) { $crashed = $true; $crashStep = "点击列表项 #$i"; break }
    }
    if ($crashed) { break }
    # Ctrl+P 置顶切换
    [W.U]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    SendKey 0x50
    [W.U]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    if (-not (Check "round$round-CtrlP" $p)) { $crashed = $true; $crashStep = "Ctrl+P 置顶"; break }
    # Enter 激活（PasteOnEnter 已关，仅写剪贴板）
    if ($items.Count -gt 0) { SendKey 0x0D }
    if (-not (Check "round$round-Enter激活" $p)) { $crashed = $true; $crashStep = "Enter 激活"; break }
    Start-Sleep -Milliseconds 400
    $panel = WaitPanelVisible $p.Id
    if (-not $panel) { ShowPanelViaSignal; $panel = WaitPanelVisible $p.Id }
    if (-not (Check "round$round-激活后唤起" $p)) { $crashed = $true; $crashStep = "激活后唤起"; break }
    # 双击激活
    $items = @($panel.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
    if ($items.Count -gt 0) {
        $r = $items[0].Current.BoundingRectangle
        if ($r.Width -gt 0) {
            DblClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) 400
            if (-not (Check "round$round-双击激活" $p)) { $crashed = $true; $crashStep = "双击激活"; break }
        }
    }
    $panel = WaitPanelVisible $p.Id
    if (-not $panel) { ShowPanelViaSignal; $panel = WaitPanelVisible $p.Id }
    if (-not (Check "round$round-双击后唤起" $p)) { $crashed = $true; $crashStep = "双击后唤起"; break }
    # Esc 隐藏
    SendKey 0x1B
    Start-Sleep -Milliseconds 300
    if (-not (Check "round$round-Esc隐藏" $p)) { $crashed = $true; $crashStep = "Esc 隐藏"; break }

    # -- 6. 搜索框输入/清除
    $panel = WaitPanelVisible $p.Id
    if (-not $panel) { ShowPanelViaSignal; $panel = WaitPanelVisible $p.Id }
    if ($panel) {
        $edit = $panel.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
        if ($edit) {
            $r = $edit.Current.BoundingRectangle
            ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) 200
            [System.Windows.Forms.SendKeys]::SendWait("pa")
            Start-Sleep -Milliseconds 300
            if (-not (Check "round$round-搜索输入" $p)) { $crashed = $true; $crashStep = "搜索输入"; break }
            [System.Windows.Forms.SendKeys]::SendWait("^a")
            [System.Windows.Forms.SendKeys]::SendWait("{BS}")
            Start-Sleep -Milliseconds 150
        }
    }
    if ($crashed) { break }

    # -- 7. 边缘 8 点点击（WM_NCHITTEST 边带）+ 拖动
    $panel = WaitPanelVisible $p.Id
    if (-not $panel) { ShowPanelViaSignal; $panel = WaitPanelVisible $p.Id }
    if ($panel) {
        $rect = $panel.Current.BoundingRectangle
        $cx = $rect.X + $rect.Width / 2
        $cy = $rect.Y + $rect.Height / 2
        $L = $rect.Left + 4; $Rt = $rect.Right - 4; $T = $rect.Top + 4; $B = $rect.Bottom - 4
        ClickAt $L $cy;  if (-not (Check "round$round-边缘左" $p)) { $crashed = $true; $crashStep = "边缘点击左"; break }
        ClickAt $Rt $cy; if (-not (Check "round$round-边缘右" $p)) { $crashed = $true; $crashStep = "边缘点击右"; break }
        ClickAt $cx $T;  if (-not (Check "round$round-边缘上" $p)) { $crashed = $true; $crashStep = "边缘点击上"; break }
        ClickAt $cx $B;  if (-not (Check "round$round-边缘下" $p)) { $crashed = $true; $crashStep = "边缘点击下"; break }
        ClickAt $L $T;   if (-not (Check "round$round-边缘左上" $p)) { $crashed = $true; $crashStep = "边缘点击左上"; break }
        ClickAt $Rt $T;  if (-not (Check "round$round-边缘右上" $p)) { $crashed = $true; $crashStep = "边缘点击右上"; break }
        ClickAt $L $B;   if (-not (Check "round$round-边缘左下" $p)) { $crashed = $true; $crashStep = "边缘点击左下"; break }
        ClickAt $Rt $B;  if (-not (Check "round$round-边缘右下" $p)) { $crashed = $true; $crashStep = "边缘点击右下"; break }
        DragBy ($rect.X + 150) ($rect.Y + 18) 8 6
        if (-not (Check "round$round-标题拖动" $p)) { $crashed = $true; $crashStep = "标题栏拖动"; break }
        DragBy $Rt $cy -6 0
        if (-not (Check "round$round-边缘缩放" $p)) { $crashed = $true; $crashStep = "边缘缩放"; break }
    }
    if ($crashed) { break }

    # -- 8. 失焦隐藏 → 信号连发 3 次唤起
    ClickAt 60 300 300
    if (-not (Check "round$round-失焦隐藏" $p)) { $crashed = $true; $crashStep = "失焦隐藏"; break }
    1..3 | ForEach-Object { ShowPanelViaSignal; Start-Sleep -Milliseconds 200 }
    Start-Sleep -Milliseconds 500
    if (-not (Check "round$round-信号唤起" $p)) { $crashed = $true; $crashStep = "信号唤起"; break }

    # -- 9. Alt+F4 真关面板（WM_CLOSE）→ 全局热键 Alt+V 重唤：面板必须能复活
    $panel = WaitPanelVisible $p.Id
    if ($panel) {
        try {
            $wp = $panel.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            ([System.Windows.Automation.WindowPattern]$wp).Close()
        } catch { Log "  关面板失败: $($_.Exception.Message)" }
        Start-Sleep -Milliseconds 400
        if (-not (Check "round$round-AltF4关面板" $p)) { $crashed = $true; $crashStep = "Alt+F4 关面板"; break }
        # 发全局热键 Alt+V（应用注册的默认唤起键）
        [W.U]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [W.U]::keybd_event(0x56, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [W.U]::keybd_event(0x56, 0, 2, [UIntPtr]::Zero)
        [W.U]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 800
        # 若有错误弹窗挡住（修复前会弹 InvalidOperationException），按 Enter 关掉再试一次热键
        $panel = WaitPanelVisible $p.Id 1500
        if (-not $panel) {
            Log "  热键重唤后面板未出现，疑似错误弹窗——按 Enter 清场后重试"
            SendKey 0x0D
            Start-Sleep -Milliseconds 300
            [W.U]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 60
            [W.U]::keybd_event(0x56, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
            [W.U]::keybd_event(0x56, 0, 2, [UIntPtr]::Zero)
            [W.U]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 800
            $panel = WaitPanelVisible $p.Id 2000
            if (-not $panel) { Log "  !! 热键重唤失败（面板未复活）" }
        }
        if (-not (Check "round$round-热键重唤" $p)) { $crashed = $true; $crashStep = "热键重唤"; break }
    }

    # -- 10. 剪贴板连发（文本 x6 + 图像 x2）：压测吞入、PNG 落盘、图像网格缩略图
    $panel = WaitPanelVisible $p.Id 2000
    if (-not $panel) { ShowPanelViaSignal; $panel = WaitPanelVisible $p.Id }
    for ($i = 1; $i -le 6; $i++) {
        [System.Windows.Forms.Clipboard]::SetText("pasteup-crash-test-$round-$i " + ('x' * (20 * $i)))
        Start-Sleep -Milliseconds 120
        if (-not (Check "round$round-剪贴板文本$i" $p)) { $crashed = $true; $crashStep = "剪贴板文本 #$i"; break }
    }
    if ($crashed) { break }
    try {
        Add-Type -AssemblyName System.Drawing
        $bmp = New-Object System.Drawing.Bitmap(320, 240)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::FromArgb(40 + 20 * $round, 90, 160))
        $g.Dispose()
        1..2 | ForEach-Object {
            [System.Windows.Forms.Clipboard]::SetImage($bmp)
            Start-Sleep -Milliseconds 250
        }
        $bmp.Dispose()
    } catch { Log "  图像剪贴板设置失败: $($_.Exception.Message)" }
    Start-Sleep -Milliseconds 800
    if (-not (Check "round$round-剪贴板图像" $p)) { $crashed = $true; $crashStep = "剪贴板图像"; break }
    # 图像标签页：渲染网格缩略图并点击瓦片
    $panel = WaitPanelVisible $p.Id 2000
    if (-not $panel) { ShowPanelViaSignal; $panel = WaitPanelVisible $p.Id }
    if ($panel) {
        $buttons = GetButtons $panel
        if ($buttons.Count -ge 7) {
            $r = $buttons[6].Current.BoundingRectangle   # 索引 6 = 图像标签
            ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) 300
            if (-not (Check "round$round-图像标签" $p)) { $crashed = $true; $crashStep = "图像标签页"; break }
            $items = @($panel.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
            $n = [Math]::Min(4, $items.Count)
            for ($i = 0; $i -lt $n; $i++) {
                $r = $items[$i].Current.BoundingRectangle
                if ($r.Width -le 0) { continue }
                ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
                if (-not (Check "round$round-图像瓦片$i" $p)) { $crashed = $true; $crashStep = "图像瓦片 #$i"; break }
            }
            if ($crashed) { break }
            # 双击图像瓦片 = 激活图像条目（写回剪贴板 + 解码 PNG 路径）
            $items = @($panel.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
            if ($items.Count -gt 0) {
                $r = $items[0].Current.BoundingRectangle
                if ($r.Width -gt 0) {
                    DblClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) 400
                    if (-not (Check "round$round-图像双击激活" $p)) { $crashed = $true; $crashStep = "图像双击激活"; break }
                }
            }
        }
    }

    # -- 11. 设置窗口内部点击（导航页 + 主题三段钮）再关闭
    $panel = WaitPanelVisible $p.Id 2000
    if (-not $panel) { ShowPanelViaSignal; $panel = WaitPanelVisible $p.Id }
    if ($panel) {
        $buttons = GetButtons $panel
        if ($buttons.Count -ge 2) {
            $r = $buttons[1].Current.BoundingRectangle
            ClickAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) 700
            if (-not (Check "round$round-开设置2" $p)) { $crashed = $true; $crashStep = "打开设置(内部点击)"; break }
            foreach ($w in (GetWindows $p.Id)) {
                if ($w.Current.ControlType.ProgrammaticName -ne 'ControlType.Window') { continue }
                $sbtns = @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
                Log ("  设置窗按钮数: {0}" -f $sbtns.Count)
                # 逐个点设置窗内前 12 个按钮（导航/主题/关闭/清空确认弹窗等）
                $m = [Math]::Min(12, $sbtns.Count)
                for ($i = 0; $i -lt $m; $i++) {
                    $sr = $sbtns[$i].Current.BoundingRectangle
                    if ($sr.Width -le 0) { continue }
                    ClickAt ($sr.X + $sr.Width / 2) ($sr.Y + $sr.Height / 2) 250
                    if (-not (Check "round$round-设置btn$i" $p)) { $crashed = $true; $crashStep = "设置按钮 #$i"; break }
                    # 可能弹出确认框（如清空历史）：按 Enter 应答
                    Start-Sleep -Milliseconds 100
                    SendKey 0x0D
                }
                if ($crashed) { break }
                try {
                    $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
                    ([System.Windows.Automation.WindowPattern]$wp).Close()
                } catch { }
            }
            Start-Sleep -Milliseconds 300
            if (-not (Check "round$round-关设置2" $p)) { $crashed = $true; $crashStep = "关闭设置(内部点击)"; break }
        }
    }
}

# ===== 结果 =====
if ($crashed) {
    Log "===== 崩溃！步骤: $crashStep ====="
    Get-WinEvent -FilterHashtable @{LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-10) } -MaxEvents 60 -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Pasteup' } |
        ForEach-Object { "---- $($_.TimeCreated) [$($_.ProviderName)/$($_.Id)] ----"; $_.Message; "" }
    exit 2
}
else {
    Log "===== $Rounds 轮全部存活，无崩溃 ====="
    $logs = Get-ChildItem (Join-Path $DataDir 'logs') -File -ErrorAction SilentlyContinue
    if ($logs) {
        Log "!! 期间产生崩溃/异常日志 $($logs.Count) 条："
        $logs | ForEach-Object { Log ("   " + $_.Name); Get-Content $_.FullName -ErrorAction SilentlyContinue | Select-Object -First 12 | ForEach-Object { "     " + $_ } }
    }
    else { Log "崩溃/异常日志：无" }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    exit 0
}
