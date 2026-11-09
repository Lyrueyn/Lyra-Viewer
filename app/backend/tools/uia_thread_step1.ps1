Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$click = FindId 'CommentTab'
if ($click) { try { $click.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {} }
Start-Sleep -Seconds 12
$list = FindId 'CommentList'
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = if ($list) { $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond) } else { $null }
if ($btns -and $btns.Count -gt 0) {
  Write-Output ("列表里的'查看X条回复'按钮数 = " + $btns.Count)
  Write-Output ("  第一个: " + $btns[0].Current.Name)
  try { $btns[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output '  已点它' } catch { Write-Output ("  invoke 异常: " + $_.Exception.Message) }
} else { Write-Output "没有'查看X条回复'按钮（这个视频的评论可能没回复）" }