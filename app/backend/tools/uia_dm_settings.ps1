Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
$btn = FindId 'SettingsTab'
if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output 'clicked SettingsTab' } else { Write-Output 'SettingsTab NOT FOUND' }
Start-Sleep -Milliseconds 600
foreach ($pair in @(@('DmOpacitySlider',40), @('DmAreaSlider',10), @('DmMaxSlider',50))) {
  $el = FindId $pair[0]
  if ($el) {
    try { $el.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]$pair[1]); Write-Output ("set {0} = {1}" -f $pair[0], $pair[1]) }
    catch { Write-Output ("set FAILED {0}: {1}" -f $pair[0], $_.Exception.Message) }
  } else { Write-Output ("NOT FOUND: " + $pair[0]) }
}