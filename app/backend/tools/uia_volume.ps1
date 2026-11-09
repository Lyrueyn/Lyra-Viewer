Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$vol = FindId 'VolumeSlider'
$mute = FindId 'MuteBtn'
if (-not $vol) { Write-Output '✗ 找不到 VolumeSlider（控制条没加载出来？）'; exit }
$rv = $vol.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
Write-Output ("  初始音量 = " + $rv.Current.Value)
$rv.SetValue([double]35)
Write-Output ("  设为 35 后 = " + $rv.Current.Value)
Start-Sleep -Milliseconds 900
if ($mute) { Write-Output ("  静音按钮文字(前) = " + $mute.Current.Name); try { $mute.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {} }
Start-Sleep -Milliseconds 900
if ($mute) { Write-Output ("  静音按钮文字(后) = " + $mute.Current.Name) }