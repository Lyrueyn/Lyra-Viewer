Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$tab = FindId 'SettingsTab'; if ($tab) { try { $tab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {} }
Start-Sleep -Milliseconds 700
foreach ($pair in @(@('DmFontSlider',150), @('DmSpeedSlider',50))) {
  $el = FindId $pair[0]
  if ($el) { try { $el.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]$pair[1]); Write-Output ("  设 {0} = {1}" -f $pair[0], $pair[1]) } catch { Write-Output ("  ✗ " + $pair[0]) } }
  else { Write-Output ("  ✗ 找不到 " + $pair[0]) }
}
$box = FindId 'DmBlockBox'
if ($box) {
  $vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $vp.SetValue('打卡 前方高能')
  Write-Output ("  屏蔽词设为: " + $vp.Current.Value)
} else { Write-Output '  ✗ 找不到 DmBlockBox' }