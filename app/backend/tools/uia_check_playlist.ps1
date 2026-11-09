Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function F($id){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id); $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
$t = F 'PlaylistTitle'
if ($t) { Write-Output ("A 视频库打开 → 合集面板【可见】，标题=" + $t.Current.Name) }
else { Write-Output "A 视频库打开 → 合集面板【不可见】(正确 ✔)" }