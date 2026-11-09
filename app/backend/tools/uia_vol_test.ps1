Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
$sl = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'VolumeSlider')))
if (-not $sl) { $sl = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider))) }
if (-not $sl) { Write-Output '✗ 找不到滑块'; exit }
Write-Output ("找到滑块 ✔ 当前值 = " + $sl.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value)
$rv = $sl.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
$rv.SetValue([double]0); Start-Sleep -Milliseconds 700; Write-Output ("设为 0 → " + $rv.Current.Value)
$rv.SetValue([double]40); Start-Sleep -Milliseconds 700; Write-Output ("设为 40 → " + $rv.Current.Value)