Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$sk = FindId 'SeekSlider'
if ($sk) {
  $rv = $sk.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
  try { $rv.SetValue([double]0.99); Write-Output ("  已拖到 99%（当前=" + $rv.Current.Value + "）") } catch { Write-Output ("  拖进度异常: " + $_.Exception.Message) }
} else { Write-Output '  ✗ 找不到 SeekSlider' }
$lb = FindId 'LoopListBtn'
if ($lb) { Write-Output ("  列表循环按钮: 可见=" + (-not $lb.Current.IsOffscreen)) } else { Write-Output '  ✗ 找不到 LoopListBtn' }