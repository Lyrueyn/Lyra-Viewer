Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
$c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'CommentList')
$list=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
Write-Output ("按钮数 = " + $btns.Count)
foreach ($b in $btns) { Write-Output ("  " + $b.Current.Name) }
$texts = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))
$n = 0
foreach ($t in $texts) { if ($t.Current.Name -like '↳*') { $n++ } }
Write-Output ("界面里 ↳ 开头的回复行数 = " + $n)