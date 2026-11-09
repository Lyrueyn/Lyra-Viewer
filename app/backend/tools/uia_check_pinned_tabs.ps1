Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindId($id) { $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
$tab = FindId 'CommentTab'
$list = FindId 'CommentList'
if ($null -eq $tab) { Write-Output '找不到 CommentTab ✗'; exit }
$r1 = $tab.Current.BoundingRectangle
Write-Output ("  滚动前 tab Y={0:N1}" -f $r1.Top)
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$cur = $list; $pane = $null
while ($null -ne $cur) {
  try { $sp = $cur.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); if ($sp) { $pane = $cur; break } } catch {}
  $cur = $walker.GetParent($cur)
}
if ($pane) {
  $sp = $pane.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
  for ($i = 0; $i -lt 3; $i++) { try { $sp.Scroll([System.Windows.Automation.ScrollAmount]::LargeIncrement, [System.Windows.Automation.ScrollAmount]::NoAmount) } catch {}; Start-Sleep -Milliseconds 500 }
  Write-Output ("  已滚动内容区（可滚动=" + $sp.Current.VerticallyScrollable + "）")
} else { Write-Output '  （没找到滚动容器）' }
$r2 = $tab.Current.BoundingRectangle
Write-Output ("  滚动后 tab Y={0:N1}" -f $r2.Top)
$d = [Math]::Abs($r1.Top - $r2.Top)
if ($d -lt 1.5) { Write-Output ("★ tab 固定不动 ✔（差 {0:N2} px）" -f $d) } else { Write-Output ("✗ tab 跟着滚了 {0:N1} px" -f $d) }
$hint = FindId 'CommentsHint'; if ($hint) { Write-Output ("  评论提示: " + $hint.Current.Name) }