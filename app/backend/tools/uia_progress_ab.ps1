Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$sk = FindId 'SeekSlider'
$tt = FindId 'TimeText'
if (-not $sk) { Write-Output '  ✗ 找不到进度条'; exit }
$rv = $sk.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
function Sample($tag) {
  $a = $rv.Current.Value; Start-Sleep -Seconds 3; $b = $rv.Current.Value
  $t = if ($tt) { $tt.Current.Name } else { '' }
  Write-Output ("  [{0}] 进度 {1:N5} → {2:N5}  {3}  时间文字={4}" -f $tag, $a, $b, $(if ([Math]::Abs($b-$a) -gt 0.00001) { '在动 ✔' } else { '★不动 ✗' }), $t)
}
Start-Sleep -Seconds 2
Sample '卡住期间(兜底前)'
Start-Sleep -Seconds 9
Sample '兜底之后'