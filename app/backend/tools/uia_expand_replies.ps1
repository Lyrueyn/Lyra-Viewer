Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
$idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CommentList')
$list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
$hintCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CommentsHint')
$hint = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $hintCond)
if ($hint) { Write-Output ("提示: " + $hint.Current.Name) }
if (-not $list) { Write-Output '找不到评论列表 ✗'; exit }
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
Write-Output ("查看回复按钮数 = " + $btns.Count)
$k = 0
foreach ($b in $btns) { if ($k -lt 4) { Write-Output ("  按钮: " + $b.Current.Name); $k++ } }
foreach ($b in $btns) {
  try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output ("  已点: " + $b.Current.Name); break } catch { }
}