Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$vol = FindId 'VolumeSlider'; $mute = FindId 'MuteBtn'
if ($vol) { Write-Output ("  音量条 = " + $vol.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value) }
if ($mute) { Write-Output ("  静音按钮 = " + $mute.Current.Name) }