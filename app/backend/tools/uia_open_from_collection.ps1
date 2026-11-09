Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
$proc = Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$main = $null; foreach ($w in $wins) { try { if ($w.Current.Name -ne '视频') { $main = $w; break } } catch {} }
if (-not $main) { Write-Output 'FAIL: main window'; exit }
# 合集页里的视频项：找 ListItem，挑一个 Name 不是合集名的
$liCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)
Write-Output ("  合集页 ListItem 数=" + $items.Count)
$clicked = $false
foreach ($it in $items) {
  $n = $it.Current.Name
  if ($n -and $n.Length -gt 4) {
    try { $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output ("  点了: " + $n.Substring(0,[Math]::Min(40,$n.Length))); $clicked = $true; break } catch {}
  }
}
if (-not $clicked) { Write-Output '  没找到可点的视频项 ✗' }