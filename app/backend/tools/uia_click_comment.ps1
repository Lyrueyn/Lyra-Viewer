Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function F($id){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id); $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
$b = F 'CommentTab'
if ($b) { try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {}; Write-Output '已点评论 tab' } else { Write-Output 'CommentTab 未找到 ✗' }