Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function F($id){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id); $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
$b=F 'SettingsTab'; if($b){ try{$b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}catch{} }
Start-Sleep -Milliseconds 600
$s=F 'DmOpacitySlider'
if ($s) { Write-Output ("设置 tab 正常：滑块在（值=" + $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value + "）") } else { Write-Output '设置 tab 的滑块不可见 ✗' }