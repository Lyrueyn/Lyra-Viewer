Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
foreach ($n in @('MuteBtn','LoopOneBtn','LoopListBtn','PlayBtn','HdrBtn','QualityBtn')) {
  $e = FindId $n
  if ($e) { Write-Output ("  {0,-12} 文字='{1}'  可见={2}" -f $n, $e.Current.Name, (-not $e.Current.IsOffscreen)) }
  else { Write-Output ("  {0,-12} （未找到）" -f $n) }
}
$lb = FindId 'LoopOneBtn'
if ($lb) { try { $lb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500; Write-Output ("  点后文字='" + $lb.Current.Name + "'") } catch {} }