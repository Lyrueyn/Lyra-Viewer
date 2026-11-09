Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$back = FindId 'BackToCommentsBtn'
if ($back) {
  Write-Output "★ 楼视图已出现（返回按钮在 ✔）"
  $th = FindId 'ThreadHint'; if ($th) { Write-Output ("  楼提示: " + $th.Current.Name) }
  $tt = FindId 'ThreadTitle'; if ($tt) { Write-Output ("  楼标题: " + $tt.Current.Name) }
  $tl = FindId 'ThreadList'
  if ($tl) { $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem); Write-Output ("  楼里回复条数 = " + $tl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ic).Count) }
  try { $back.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output '  已点返回' } catch {}
  Start-Sleep -Seconds 1
  $b2 = FindId 'BackToCommentsBtn'
  if ($b2) { Write-Output "  返回后：返回按钮仍在（楼视图没关掉？）✗" } else { Write-Output "  返回后：已回到评论列表 ✔" }
} else { Write-Output "楼视图没出现 ✗（可能这个视频没有带回复的评论）" }