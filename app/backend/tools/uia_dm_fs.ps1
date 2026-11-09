Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$tab = FindId 'SettingsTab'; if ($tab) { try { $tab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {} }
Start-Sleep -Milliseconds 700
$f = FindId 'DmFontSlider'; if ($f) { $f.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]150); Write-Output '  字号→150%' }
$sp = FindId 'DmSpeedSlider'; if ($sp) { $sp.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]200); Write-Output '  速度→200%' }