# Verify "add video to collection": click the ＋ on the first home card, then pick the first collection
# in the flyout; afterwards we check the backend for the new item.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File uia_add_to_collection.ps1
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
Write-Output ('window: ' + $mainWin.Current.Name)

# 1) 首页卡片右上角的 ＋ 按钮（Content="＋"）
$btns = $mainWin.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
$plus = $null
foreach ($b in $btns) { try { if ($b.Current.Name -eq '＋') { $plus = $b; break } } catch { } }
if (-not $plus) {
    Write-Output 'FAIL: ＋ button not found. Titles:'
    foreach ($b in $btns[0..19]) { try { Write-Output ('   - "' + $b.Current.Name + '"') } catch { } }
    exit 2
}
Write-Output 'found ＋ button on card, invoking...'
$plus.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1500

# 2) 弹出菜单里的合集项（MenuFlyout 是独立弹窗，从桌面根节点找）
$menuItems = $auto::RootElement.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
Write-Output ('menu items found: ' + $menuItems.Count)
$first = $null
foreach ($m in $menuItems) {
    try {
        Write-Output ('   - ' + $m.Current.Name)
        if (-not $first -and $m.Current.Name -and $m.Current.Name -notlike '*新建*') { $first = $m }
    } catch { }
}
if (-not $first) { Write-Output 'FAIL: no collection menu item'; exit 3 }
Write-Output ('invoking: ' + $first.Current.Name)
$first.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
Write-Output 'DONE'
