Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function F($id){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id); $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
$b=F 'SettingsTab'; if($b){ try{$b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}catch{} }
Start-Sleep -Milliseconds 500
foreach($pp in @(@('DmAreaSlider',100),@('DmOpacitySlider',100))){ $e=F $pp[0]; if($e){ try{$e.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]$pp[1])}catch{} } }