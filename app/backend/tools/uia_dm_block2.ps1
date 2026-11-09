Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$tab = FindId 'SettingsTab'; if ($tab) { try { $tab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {} }
Start-Sleep -Milliseconds 700
$box = FindId 'DmBlockBox'
if ($box) { $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('生日'); Write-Output '  屏蔽词设为: 生日' }
$fs = FindId 'DmFontSlider'; if ($fs) { $fs.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]140); Write-Output '  字号设为 140%' }