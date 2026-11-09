Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$sk = FindId 'SeekSlider'; $tt = FindId 'TimeText'
$rv = $sk.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
Write-Output ("  点击前: " + $tt.Current.Name)
$rv.SetValue([double]1.0)
for ($i=1; $i -le 6; $i++) {
  Start-Sleep -Seconds 3
  Write-Output ("  +{0,2}s  进度={1:N4}  时间={2}" -f ($i*3), $rv.Current.Value, $tt.Current.Name)
}