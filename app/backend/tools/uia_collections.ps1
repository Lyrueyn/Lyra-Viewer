# Switch to the 合集 tab and select the first playlist (for screenshot verification).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File uia_collections.ps1
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
$desc = [System.Windows.Automation.TreeScope]::Descendants
$children = [System.Windows.Automation.TreeScope]::Children

$proc = Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'FAIL: app not running'; exit 1 }
$cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$proc.Id)
$wins = $auto::RootElement.FindAll($children, $cond)
$mainWin = $null
foreach ($w in $wins) { try { if ($w.Current.Name -ne '视频') { $mainWin = $w; break } } catch { } }
if (-not $mainWin) { Write-Output 'FAIL: main window not found'; exit 1 }

# 1) click 合集 tab：三个导航按钮的 UIA Name 是空的（内容里才是文字），
#    改用"最左侧 + 竖向排列的第 3 个"（XAML 顺序 = 首页、下载、合集）来定位。
$btns = $mainWin.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
$winLeft = $mainWin.Current.BoundingRectangle.Left
$navs = @()
foreach ($b in $btns) {
    try {
        $r = $b.Current.BoundingRectangle
        if (($r.Left - $winLeft) -lt 130 -and $r.Width -lt 130 -and $r.Height -lt 130) { $navs += $b }
    } catch { }
}
$navs = $navs | Sort-Object { $_.Current.BoundingRectangle.Top }
Write-Output ('left nav buttons: ' + $navs.Count)
if ($navs.Count -lt 3) { Write-Output 'FAIL: nav buttons not found'; exit 1 }
$tab = $navs[2]
$tab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'clicked 合集 tab (3rd nav button)'
Start-Sleep -Seconds 2

# 2) select the first playlist (ListViewItem supports SelectionItemPattern)
$items = $mainWin.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
Write-Output ('list items found: ' + $items.Count)
if ($items.Count -eq 0) { Write-Output 'FAIL: no playlist items'; exit 2 }
foreach ($it in $items) { try { Write-Output ('   - ' + $it.Current.Name) } catch { } }
$first = $items[0]
try {
    $first.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output ('selected: ' + $first.Current.Name)
} catch { Write-Output ('select failed: ' + $_.Exception.Message) }
Start-Sleep -Seconds 3
Write-Output 'DONE'
