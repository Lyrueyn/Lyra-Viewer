Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$list = FindId 'PlaylistItemsList'
if (-not $list) { Write-Output '  ✗ 找不到 PlaylistItemsList'; exit }
$liCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)
Write-Output ("  播放列表项数 = " + $items.Count)
$last = $items[$items.Count - 1]
try { $last.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output ("  已点最后一项: " + $last.Current.Name) } catch { Write-Output '  invoke 异常' }