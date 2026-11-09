Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$sk = FindId 'SeekSlider'; $tt = FindId 'TimeText'
if (-not $sk) { Write-Output '  ✗ 没进度条'; exit }
$rv = $sk.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
Write-Output ("  当前进度 = " + $rv.Current.Value)
# ★ 模拟"点击进度条到结尾"（_userDragging=false → SeekJump(片长)）
$rv.SetValue([double]1.0)
Write-Output '  已把进度条点到结尾（SeekJump 到片长）'
Start-Sleep -Seconds 12
Write-Output ("  跳过下一集后: 时间文字 = " + $tt.Current.Name)
$a = $rv.Current.Value; Start-Sleep -Seconds 4; $b = $rv.Current.Value
Write-Output ("  之后进度 {0:N5} → {1:N5}  {2}" -f $a, $b, $(if ([Math]::Abs($b-$a) -gt 0.00001) { '在动 ✔' } else { '★卡住 ✗ —— 复现成功' }))