Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$lb = FindId 'LoopOneBtn'
if ($lb) { try { $lb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output '  已开启单集循环' } catch { Write-Output '  invoke 异常' } }
Start-Sleep -Milliseconds 700
$sk = FindId 'SeekSlider'
if ($sk) { try { $sk.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]0.99); Write-Output '  拖到 99%' } catch {} }