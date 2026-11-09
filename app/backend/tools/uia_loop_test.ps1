Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$b = FindId 'LoopListBtn'
if ($b) { Write-Output ("  列表循环按钮可见=" + $b.Current.IsOffscreen.ToString() + " 前景=" + $b.Current.Name) } else { Write-Output '  ✗ 没找到 LoopListBtn（合集面板没出来？）' }
$sk = FindId 'SeekSlider'
if ($sk) { try { $sk.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]0.995) ; Write-Output '  已拖到 99.5%' } catch { Write-Output '  拖进度异常' } }