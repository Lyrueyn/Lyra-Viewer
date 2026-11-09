Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$b = FindId 'SpeedBtn'
if (-not $b) { Write-Output '✗ 找不到倍速按钮'; exit }
Write-Output ("倍速按钮 ✔ 文字 = " + $b.Current.Name)
$b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1000
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
$names = @(); foreach ($i in $items) { try { if ($i.Current.Name -match 'x|×') { $names += $i.Current.Name } } catch {} }
Write-Output ("弹出选项: " + ($names -join ' | '))
foreach ($i in $items) { try { if ($i.Current.Name -match '1\.5') { $i.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output '已点 1.5×'; break } } catch {} }
Start-Sleep -Seconds 2
Write-Output ("点击后按钮文字 = " + $b.Current.Name)