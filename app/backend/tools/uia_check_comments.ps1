Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function F($id){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id); $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
$h = F 'CommentsHeader'; $hint = F 'CommentsHint'; $lst = F 'CommentList'
if ($h) { Write-Output ("标题: " + $h.Current.Name) } else { Write-Output '评论面板未显示 ✗' }
if ($hint) { Write-Output ("提示: " + $hint.Current.Name) }
if ($lst) {
  $items = $lst.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  Write-Output ("列表项数 = " + $items.Count)
  if ($items.Count -gt 0) { Write-Output ("第一条: " + $items[0].Current.Name.Substring(0,[Math]::Min(90,$items[0].Current.Name.Length))) }
}