# uia_speed_set.ps1 —— 用 UIA 把播放器的倍速设成指定值（找 SpeedBtn → Invoke → 在 MenuFlyout 里点该项）
#
# 前置条件（缺一不可）：
#   1) 播放器正在运行，且已经打开了视频窗口（标题"视频"）
#   2) 播放器是以 DSH_SPEED_UI=1 启动的 —— 否则 SpeedBtn 是 Collapsed，
#      UIA 树里根本找不到（脚本会明确报这个原因，避免误判成"界面坏了"）
#   3) 只能 UIA（本机 SendKeys/mouse_event 不可靠）
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File uia_speed_set.ps1 -Speed 1.5
#   powershell ... -File uia_speed_set.ps1 -Speed 2.0 -List        # 只列出菜单项，不点
#
param(
    [string]$Speed = "1.5",   # 目标倍速：0.5 / 0.75 / 1.0 / 1.25 / 1.5 / 2.0
    [switch]$List,            # 只展开并列出 MenuFlyout 里的选项
    [int]$SettleMs = 900      # 点完等界面刷新
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
$desc = [System.Windows.Automation.TreeScope]::Descendants
$root = $auto::RootElement

function Find-SpeedBtn {
    # 先按 AutomationId 精确找；找不到再按 Name 模糊找（按钮文字是"倍速 X.X×"）
    $c = New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, 'SpeedBtn')
    $b = $root.FindFirst($desc, $c)
    if ($b) { return $b }
    $all = $root.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    foreach ($x in $all) { try { if ($x.Current.Name -match '^倍速') { return $x } } catch { } }
    return $null
}

$proc = Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'FAIL: 播放器没在运行（先启动 WinUIGui.exe）'; exit 1 }

$btn = Find-SpeedBtn
if (-not $btn) {
    Write-Output 'FAIL: UIA 树里找不到"倍速"按钮。'
    Write-Output '      最常见原因：播放器启动时没有 DSH_SPEED_UI=1（倍速按钮按灰度开关保持隐藏）。'
    Write-Output '      正确启动示例： $env:DSH_SPEED_UI="1"; Start-Process <exe> -WorkingDirectory <bin>'
    Write-Output '      其次确认：视频窗口已打开（标题"视频"），不是停在主界面。'
    exit 2
}
Write-Output ("找到倍速按钮 ✔ 当前文字 = " + $btn.Current.Name)

$inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$inv.Invoke()
Start-Sleep -Milliseconds 800

# MenuFlyout 是独立弹窗（自己的 HWND），从 RootElement 往下找 MenuItem
$items = $root.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
$menu = @()
foreach ($i in $items) {
    try { if ($i.Current.Name -match '×') { $menu += $i } } catch { }
}
Write-Output ("弹出选项 " + $menu.Count + " 项: " + (($menu | ForEach-Object { $_.Current.Name }) -join ' | '))
if ($List) { exit 0 }
if ($menu.Count -eq 0) { Write-Output 'FAIL: MenuFlyout 里没找到带 × 的菜单项'; exit 3 }

# 目标：先归一化（1.0 / 1.0x / 1.0× 都吃），菜单文案是 "0.0#" 格式（1.0× / 1.5× / 2.0×），
# 所以把几种可能写法都当候选，避免 "2" 匹配不到 "2.0×"
$num = $Speed -replace '[^0-9\.]', ''
$cands = @($num)
try {
    $d = [double]$num
    $cands += $d.ToString('0.0#')     # 2 → 2.0
    $cands += $d.ToString('0.##')     # 2.0 → 2
} catch { }
$cands = $cands | Select-Object -Unique

$hit = $null
foreach ($c in $cands) {
    $pat = '^' + [regex]::Escape($c) + '\s*×'
    foreach ($i in $menu) { try { if ($i.Current.Name -match $pat) { $hit = $i; break } } catch { } }
    if ($hit) { break }
}
if (-not $hit) {
    # 菜单文案万一再变，退一步做模糊匹配（1.0×（原速）这类后缀也能命中）
    foreach ($c in $cands) {
        foreach ($i in $menu) { try { if ($i.Current.Name -like ("*" + $c + "×*")) { $hit = $i; break } } catch { } }
        if ($hit) { break }
    }
}
if (-not $hit) { Write-Output ("FAIL: 菜单里没有 " + $num + "× 这一项"); exit 4 }

$hit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds $SettleMs
Write-Output ("已点: " + $hit.Current.Name)
Write-Output ("点击后按钮文字 = " + $btn.Current.Name)
Write-Output 'DONE'
