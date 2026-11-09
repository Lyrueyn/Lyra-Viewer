Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$b = FindId 'PlayBtn'
function Click { param($b) try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { Write-Output ('  invoke 异常: ' + $_.Exception.Message) } }
Write-Output '  播放 6 秒…'; Start-Sleep -Seconds 6
Write-Output '  → 点暂停'; Click $b; Start-Sleep -Seconds 4
Write-Output '  → 点恢复'; Click $b; Start-Sleep -Seconds 6
Write-Output '  → 拖进度到 60%'
$sk = FindId 'SeekSlider'
if ($sk) { try { $sk.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]0.6) } catch { Write-Output '  拖进度异常' } }
Start-Sleep -Seconds 8
Write-Output '  完成'