Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
$c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'DescText')
$e=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
if ($e) { $n=$e.Current.Name; Write-Output ("简介已显示，长度=" + $n.Length); Write-Output ("前 120 字: " + $n.Substring(0,[Math]::Min(120,$n.Length))) }
else { Write-Output "简介未显示 ✗" }