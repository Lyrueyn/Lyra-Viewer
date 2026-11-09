# Click a left-nav tab via UIA (verifies the highlight follows the selected tab).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File uia_nav_tab.ps1 -Tab 下载
param([string]$Tab = '下载')
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

$btns = $mainWin.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
$target = $null
foreach ($b in $btns) {
    $n = ''
    try { $n = $b.Current.Name } catch { }
    if ($n -like "*$Tab*") { $target = $b; Write-Output ('  match button: "' + $n + '"') }
}
if (-not $target) {
    Write-Output 'FAIL: tab button not found. Buttons seen:'
    foreach ($b in $btns) { try { Write-Output ('   - ' + $b.Current.Name) } catch { } }
    exit 2
}
$target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output ('clicked tab: ' + $target.Current.Name)
Start-Sleep -Seconds 2
Write-Output 'DONE'
