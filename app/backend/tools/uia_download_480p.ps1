# End-to-end test of the download quality parameter via UI Automation:
#   set URL -> click 解析 -> select 480P in the format combo -> click 开始下载
# Then check dm_debug.log for "[下载] 提交 qn=32" and the DB for a 480P row.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File uia_download_480p.ps1
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
$desc = [System.Windows.Automation.TreeScope]::Descendants
$children = [System.Windows.Automation.TreeScope]::Children

function Find-ById($root, $id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $id)
    return $root.FindFirst($desc, $c)
}
function Find-ByNamePart($root, $part, $ctrl) {
    $els = $root.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, $ctrl)))
    foreach ($e in $els) { try { if ($e.Current.Name -like "*$part*") { return $e } } catch { } }
    return $null
}

$url = 'https://www.bilibili.com/video/BV1Sf4y1q7H9'

$root = $auto::RootElement
$pid2 = (Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1).Id
if (-not $pid2) { Write-Output 'FAIL: WinUIGui not running'; exit 1 }
$cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$pid2)
$wins = $root.FindAll($children, $cond)
$mainWin = $null
foreach ($w in $wins) { try { if ($w.Current.Name -ne '视频') { $mainWin = $w; break } } catch { } }
if (-not $mainWin) { Write-Output 'FAIL: main window not found'; exit 1 }
Write-Output ('main window: ' + $mainWin.Current.Name)

# 1) URL box
$box = Find-ById $mainWin 'UrlBox'
if (-not $box) {
    $box = Find-ByNamePart $mainWin '' ([System.Windows.Automation.ControlType]::Edit)
}
if (-not $box) { Write-Output 'FAIL: url box not found'; exit 1 }
Write-Output ('url box: id=' + $box.Current.AutomationId + ' class=' + $box.Current.ClassName)
$vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$vp.SetValue($url)
Write-Output 'url set'

# 2) 解析
$parseBtn = Find-ById $mainWin 'ParseBtn'
if (-not $parseBtn) { $parseBtn = Find-ByNamePart $mainWin '解析' ([System.Windows.Automation.ControlType]::Button) }
if (-not $parseBtn) { Write-Output 'FAIL: parse button not found'; exit 1 }
$parseBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'clicked 解析, waiting for formats...'
Start-Sleep -Seconds 8

# 3) format combo -> select 480P （ComboBox 的选项要先把下拉展开才会实例化）
$combo = Find-ById $mainWin 'FormatCombo'
if (-not $combo) { Write-Output 'FAIL: FormatCombo not found'; exit 1 }
try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { Write-Output ('expand failed: ' + $_.Exception.Message) }
Start-Sleep -Milliseconds 900
$items = $combo.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
if ($items.Count -eq 0) {
    # 有些实现把弹出项挂到窗口根下，而不是 combo 子树里
    $items = $mainWin.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
}
Write-Output ('format items: ' + $items.Count)
$target = $null
foreach ($it in $items) {
    Write-Output ('   item: ' + $it.Current.Name)
    if ($it.Current.Name -like '*480P*') { $target = $it }
}
if (-not $target) { Write-Output 'FAIL: 480P item not found'; exit 2 }
$target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Write-Output ('selected: ' + $target.Current.Name)
try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
Start-Sleep -Milliseconds 800

# 4) 开始下载
$dlBtn = Find-ById $mainWin 'DownloadBtn'
if (-not $dlBtn) { $dlBtn = Find-ByNamePart $mainWin '开始下载' ([System.Windows.Automation.ControlType]::Button) }
if (-not $dlBtn) { Write-Output 'FAIL: download button not found'; exit 1 }
$dlBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'clicked 开始下载'
Write-Output 'DONE'
